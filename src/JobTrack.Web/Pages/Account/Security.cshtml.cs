namespace JobTrack.Web.Pages.Account;

using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using Abstractions;
using Application;
using Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NodaTime;
using Passkeys;
using QRCoder;

/// <summary>
///     The one account-security hub (ADR 0071 §1.1): the signed-in employee's passkeys alongside TOTP
///     two-factor state and enrol/disable actions, plus recovery guidance. Passwords remain the
///     recovery credential; this page never removes the account password. TOTP enrolment still requires
///     one valid code before <see cref="UserManager{TUser}.SetTwoFactorEnabledAsync" /> is called (spec
///     §7.1), and every credential-weakening action requires ADR 0057 recent authentication.
///     <c>/Account/ManageTwoFactor</c> redirects here so old bookmarks keep working.
/// </summary>
[Authorize(Policy = JobTrackPolicyNames.AnyAuthenticatedUser)]
public sealed partial class SecurityModel(
	UserManager<JobTrackIdentityUser> userManager,
	SignInManager<JobTrackIdentityUser> signInManager,
	JobTrackUserStore userStore,
	IJobTrackClient jobTrackClient,
	IViewerTimeZoneResolver viewerTimeZoneResolver,
	IDataProtectionProvider dataProtectionProvider,
	IOptions<PasskeyFeatureOptions> passkeyFeature,
	CurrentCredentialVerifier credentialVerifier,
	ILogger<SecurityModel> logger) : PageModel
{
	private const string Issuer = "JobTrack";
	private const int QrPixelsPerModule = 6;
	private const string GenericAttestationFailure = "That passkey could not be added. Try again.";
	private const string CeremonyExpired = "Passkey setup expired; try again.";

	// The opaque WebAuthn credential ID never reaches the page (ADR 0071 §8.1: "Never display
	// credential ID"). Rename/remove address a row through a Data-Protection token of that ID instead,
	// which this page mints for the hidden field and unwraps on POST.
	private const string CredentialRefProtectionPurpose = "JobTrack.Web.PasskeyCredentialRef.v1";

	[BindProperty] public ConfirmInput Confirm { get; set; } = new();

	[BindProperty] public DisableInput Disable { get; set; } = new();

	[BindProperty] public AddPasskeyInput Add { get; set; } = new();

	public bool TwoFactorEnabled { get; private set; }

	public string? AuthenticatorKey { get; private set; }

	public string? QrCodeDataUri { get; private set; }

	public IReadOnlyList<PasskeySummary> Passkeys { get; private set; } = [];

	/// <summary>True when the feature is enabled, so the page shows enrolment and the ceremony handlers accept requests (ADR 0071 §10).</summary>
	public bool PasskeysEnabled => passkeyFeature.Value.Enabled;

	/// <summary>
	///     The WebAuthn creation-options JSON produced by a successful begin-add POST, embedded for
	///     <c>passkeys.js</c> to hand to <c>navigator.credentials.create</c>. Null on a normal page load.
	/// </summary>
	public string? PendingCreationOptionsJson { get; private set; }

	/// <summary>The signed-in actor's own time zone, for formatting each passkey's creation instant (<see cref="InstantDisplay" />).</summary>
	public DateTimeZone ViewerZone { get; private set; } = DateTimeZoneProviders.Tzdb["Etc/UTC"];

	[TempData] public string? ErrorMessage { get; set; }

	[TempData] public string? SuccessMessage { get; set; }

	public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
	{
		var user = await userManager.GetUserAsync(User);
		if (user is null) {
			return Challenge();
		}

		await LoadStateAsync(user, cancellationToken);
		return Page();
	}

	/// <summary>ADR 0057 (§2.2): binding a new second factor requires proof of recent authentication.</summary>
	[RequiresRecentAuthentication]
	public async Task<IActionResult> OnPostConfirmAsync(CancellationToken cancellationToken)
	{
		var user = await userManager.GetUserAsync(User);
		if (user is null) {
			return Challenge();
		}

		ModelState.Clear();
		if (!TryValidateModel(Confirm, nameof(Confirm))) {
			await LoadStateAsync(user, cancellationToken);
			return Page();
		}

		var codeIsValid = await userManager.VerifyTwoFactorTokenAsync(user, TokenOptions.DefaultAuthenticatorProvider, Confirm.Code);
		if (!codeIsValid) {
			ErrorMessage = "That code is incorrect. Check your authenticator app and try again.";
			await LoadStateAsync(user, cancellationToken);
			return Page();
		}

		var updated = await jobTrackClient.Credentials.SetTwoFactorStateAsync(
			new() {
				ActorUserId = user.AppUserId,
				IdentityUserId = user.Id,
				Enabled = true,
				CorrelationId = Guid.NewGuid(),
			}, cancellationToken);
		ApplyTwoFactorState(user, updated);
		await signInManager.RefreshSignInAsync(user);
		SuccessMessage = "Two-factor authentication is now enabled on your account.";
		return RedirectToPage();
	}

	/// <summary>ADR 0057 (§2.2): removing an enrolled second factor requires proof of recent authentication too.</summary>
	[RequiresRecentAuthentication]
	public async Task<IActionResult> OnPostDisableAsync(CancellationToken cancellationToken)
	{
		var user = await userManager.GetUserAsync(User);
		if (user is null) {
			return Challenge();
		}

		var rateLimitOutcome = await credentialVerifier.TryAcquireAsync(
			"two-factor-disable", user, GetRemoteAddress(), cancellationToken);
		if (rateLimitOutcome != RateLimitOutcome.Allowed) {
			if (rateLimitOutcome == RateLimitOutcome.Denied) {
				Response.StatusCode = StatusCodes.Status429TooManyRequests;
			}

			ErrorMessage = rateLimitOutcome == RateLimitOutcome.Denied
				? "Too many authentication attempts. Retry after the current window elapses."
				: "That password is incorrect.";
			await LoadStateAsync(user, cancellationToken);
			return Page();
		}

		ModelState.Clear();
		if (!TryValidateModel(Disable, nameof(Disable))) {
			await LoadStateAsync(user, cancellationToken);
			return Page();
		}

		var passwordCheck = await credentialVerifier.CheckPasswordAsync(user, Disable.CurrentPassword, true);
		if (!passwordCheck.Succeeded) {
			ErrorMessage = passwordCheck.IsLockedOut
				? "This account is temporarily locked out after too many failed attempts."
				: "That password is incorrect.";
			if (passwordCheck.IsLockedOut) {
				TwoFactorEnabled = await userManager.GetTwoFactorEnabledAsync(user);
			} else {
				await LoadStateAsync(user, cancellationToken);
			}
			return Page();
		}

		var updated = await jobTrackClient.Credentials.SetTwoFactorStateAsync(
			new() {
				ActorUserId = user.AppUserId,
				IdentityUserId = user.Id,
				Enabled = false,
				CorrelationId = Guid.NewGuid(),
			}, cancellationToken);
		user.AuthenticatorKeyProtected = null;
		ApplyTwoFactorState(user, updated);
		await signInManager.RefreshSignInAsync(user);
		SuccessMessage = "Two-factor authentication has been disabled on your account.";
		return RedirectToPage();
	}

	private string GetRemoteAddress() => HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown-ip";

	/// <summary>
	///     Step 1 of enrolment (ADR 0071 §8.1): validates the friendly name, ensures the account's user
	///     handle, and generates creation options. Recent authentication is required. The validated name
	///     is carried in a protected, session-bound cookie — no passkey row or reserved name exists until
	///     attestation succeeds — and the page re-renders with the options for <c>passkeys.js</c>.
	/// </summary>
	[RequiresRecentAuthentication]
	public async Task<IActionResult> OnPostBeginAddPasskeyAsync(CancellationToken cancellationToken)
	{
		var user = await userManager.GetUserAsync(User);
		if (user is null) {
			return Challenge();
		}

		if (!PasskeysEnabled) {
			return NotFound();
		}

		await LoadStateAsync(user, cancellationToken);

		var name = Add.Name?.Trim() ?? string.Empty;
		if (!PasskeyPolicy.IsNameAcceptable(name)) {
			ErrorMessage = $"Enter a name for the passkey ({PasskeyPolicy.MinimumNameLength}–{PasskeyPolicy.MaximumNameLength} characters).";
			return Page();
		}

		if (Passkeys.Count >= PasskeyPolicy.MaxPasskeysPerAccount) {
			ErrorMessage = $"You already have the maximum of {PasskeyPolicy.MaxPasskeysPerAccount} passkeys. Remove one before adding another.";
			return Page();
		}

		var normalizedName = PasskeyPolicy.Normalize(name);
		if (Passkeys.Any(passkey => string.Equals(PasskeyPolicy.Normalize(passkey.Name), normalizedName, StringComparison.Ordinal))) {
			ErrorMessage = "You already have a passkey with that name. Choose a different name.";
			return Page();
		}

		var handle = await jobTrackClient.Credentials.EnsurePasskeyUserHandleAsync(
			new() {
				ActorUserId = user.AppUserId,
				IdentityUserId = user.Id,
				CorrelationId = Guid.NewGuid(),
			}, cancellationToken);

		var userEntity = new PasskeyUserEntity {
			Id = handle.UserHandle,
			Name = user.UserName,
			DisplayName = user.UserName,
		};
		PendingCreationOptionsJson = await signInManager.MakePasskeyCreationOptionsAsync(userEntity);
		PendingPasskeyEnrolmentCookie.Publish(HttpContext, dataProtectionProvider, user.AppUserId, name, handle.UserHandle);
		return Page();
	}

	/// <summary>
	///     Step 2 of enrolment (ADR 0071 §8.1): the framework verifies the attestation, the verified user
	///     entity is matched to the signed-in employee and the pending name, and one atomic command
	///     stores the credential. A failed or mismatched attestation reserves nothing. Returns JSON for
	///     <c>passkeys.js</c>; recent authentication is required.
	/// </summary>
	[RequiresRecentAuthentication]
	public async Task<IActionResult> OnPostCompleteAddPasskeyAsync(CancellationToken cancellationToken)
	{
		var user = await userManager.GetUserAsync(User);
		if (user is null) {
			return Challenge();
		}

		if (!PasskeysEnabled) {
			return NotFound();
		}

		var correlationId = Guid.NewGuid();

		if (!PendingPasskeyEnrolmentCookie.TryConsume(HttpContext, dataProtectionProvider, user.AppUserId, out var pendingName, out var pendingHandle)) {
			LogPasskeyEnrolmentRejected(logger, correlationId, "ceremony-state-missing");
			return new JsonResult(new
			{
				error = CeremonyExpired,
			});
		}

		var credentialJson = await PasskeyCredentialJson.ReadAsync(Request, cancellationToken);
		if (credentialJson is null) {
			LogPasskeyEnrolmentRejected(logger, correlationId, "empty-or-oversized-body");
			return new JsonResult(new
			{
				error = GenericAttestationFailure,
			});
		}

		PasskeyAttestationResult attestation;
		try {
			attestation = await signInManager.PerformPasskeyAttestationAsync(credentialJson);
		}
		catch (PasskeyException ex) {
			// A malformed or unverifiable credential is an expected browser condition, not a server
			// fault; translate only this ceremony call's exception into the one generic response.
			LogPasskeyEnrolmentFailed(logger, correlationId, "attestation", ex);
			return new JsonResult(new
			{
				error = GenericAttestationFailure,
			});
		}
		catch (InvalidOperationException ex) {
			// Some .NET 10 servicing builds throw rather than surfacing a failed result when the
			// protected attestation state is absent (an expired or already-consumed ceremony) -- the
			// same guard the sign-in and step-up paths apply to PasskeySignInAsync. Treat it as the
			// expected expiry, not a server fault.
			LogPasskeyEnrolmentFailed(logger, correlationId, "ceremony-state", ex);
			return new JsonResult(new
			{
				error = CeremonyExpired,
			});
		}

		if (!attestation.Succeeded || attestation.Passkey is null || attestation.UserEntity is null
			|| !string.Equals(attestation.UserEntity.Id, pendingHandle, StringComparison.Ordinal)) {
			LogPasskeyAttestationUnverified(
				logger, correlationId, attestation.Succeeded, attestation.Passkey is not null,
				attestation.UserEntity is not null, attestation.Failure);
			return new JsonResult(new
			{
				error = GenericAttestationFailure,
			});
		}

		try {
			var stored = await jobTrackClient.Credentials.AddPasskeyAsync(
				new() {
					ActorUserId = user.AppUserId,
					IdentityUserId = user.Id,
					Name = pendingName,
					Credential = ToVerifiedPasskey(attestation.Passkey),
					CorrelationId = correlationId,
				}, cancellationToken);
			ApplyStamps(user, stored.SecurityStamp, stored.ConcurrencyStamp);
		}
		catch (InvariantViolationException ex) {
			LogPasskeyEnrolmentFailed(logger, correlationId, $"store-invariant:{ex.ConstraintId}", ex);
			return new JsonResult(new
			{
				error = GenericAttestationFailure,
			});
		}
		catch (ConcurrencyConflictException ex) {
			LogPasskeyEnrolmentFailed(logger, correlationId, "store-conflict", ex);
			return new JsonResult(new
			{
				error = GenericAttestationFailure,
			});
		}

		await signInManager.RefreshSignInAsync(user);
		SuccessMessage = $"Passkey \"{pendingName}\" added.";
		return new JsonResult(new
		{
			redirect = Url.Page("/Account/Security"),
		});
	}

	/// <summary>
	///     Renames one owned passkey (ADR 0071 §8.1). Audited metadata only — no stamp rotation or
	///     revocation, so no recent-authentication step-up. Antiforgery-protected PRG; ownership is
	///     re-checked inside the atomic command.
	/// </summary>
	public async Task<IActionResult> OnPostRenamePasskeyAsync(string credentialRef, string? newName, CancellationToken cancellationToken)
	{
		var user = await userManager.GetUserAsync(User);
		if (user is null) {
			return Challenge();
		}

		if (!TryUnprotectCredentialRef(credentialRef, out var credentialId)) {
			ErrorMessage = "That passkey does not exist.";
			await LoadStateAsync(user, cancellationToken);
			return Page();
		}

		var name = newName?.Trim() ?? string.Empty;
		if (!PasskeyPolicy.IsNameAcceptable(name)) {
			ErrorMessage = $"Enter a name for the passkey ({PasskeyPolicy.MinimumNameLength}–{PasskeyPolicy.MaximumNameLength} characters).";
			await LoadStateAsync(user, cancellationToken);
			return Page();
		}

		try {
			_ = await jobTrackClient.Credentials.RenamePasskeyAsync(
				new() {
					ActorUserId = user.AppUserId,
					IdentityUserId = user.Id,
					CredentialId = credentialId,
					NewName = name,
					CorrelationId = Guid.NewGuid(),
				}, cancellationToken);
			SuccessMessage = "Passkey renamed.";
		}
		catch (AuthorizationDeniedException) {
			return Forbid();
		}
		catch (EntityNotFoundException) {
			ErrorMessage = "That passkey does not exist.";
		}
		catch (InvariantViolationException) {
			ErrorMessage = "You already have a passkey with that name. Choose a different name.";
		}

		return RedirectToPage();
	}

	/// <summary>
	///     Removes one owned passkey (ADR 0071 §8.1). A credential transition — recent authentication is
	///     required and the atomic command rotates stamps and revokes tokens/sessions. Removing the final
	///     passkey is allowed; the password remains the recovery credential.
	/// </summary>
	[RequiresRecentAuthentication]
	public async Task<IActionResult> OnPostRemovePasskeyAsync(string credentialRef, CancellationToken cancellationToken)
	{
		var user = await userManager.GetUserAsync(User);
		if (user is null) {
			return Challenge();
		}

		if (!TryUnprotectCredentialRef(credentialRef, out var credentialId)) {
			ErrorMessage = "That passkey does not exist.";
			await LoadStateAsync(user, cancellationToken);
			return Page();
		}

		try {
			var result = await jobTrackClient.Credentials.RemovePasskeyAsync(
				new() {
					ActorUserId = user.AppUserId,
					IdentityUserId = user.Id,
					CredentialId = credentialId,
					CorrelationId = Guid.NewGuid(),
				}, cancellationToken);
			ApplyStamps(user, result.SecurityStamp, result.ConcurrencyStamp);
			await signInManager.RefreshSignInAsync(user);
			SuccessMessage = "Passkey removed. If it still appears in your device's passkey manager, remove it there too.";
		}
		catch (AuthorizationDeniedException) {
			return Forbid();
		}
		catch (EntityNotFoundException) {
			ErrorMessage = "That passkey does not exist.";
		}

		return RedirectToPage();
	}

	private static VerifiedPasskey ToVerifiedPasskey(UserPasskeyInfo passkey) =>
		new() {
			CredentialId = passkey.CredentialId,
			PublicKey = passkey.PublicKey,
			SignCount = passkey.SignCount,
			Transports = PasskeyTransports.Serialize(passkey.Transports),
			IsUserVerified = passkey.IsUserVerified,
			IsBackupEligible = passkey.IsBackupEligible,
			IsBackedUp = passkey.IsBackedUp,
			AttestationObject = passkey.AttestationObject,
			ClientDataJson = passkey.ClientDataJson,
		};

	/// <summary>Mints the opaque, tamper-evident token the page renders in place of a credential ID for rename/remove.</summary>
	public string ProtectCredentialRef(string credentialId) =>
		dataProtectionProvider.CreateProtector(CredentialRefProtectionPurpose).Protect(credentialId);

	private bool TryUnprotectCredentialRef(string? token, out string credentialId)
	{
		credentialId = string.Empty;
		if (string.IsNullOrEmpty(token)) {
			return false;
		}

		try {
			credentialId = dataProtectionProvider.CreateProtector(CredentialRefProtectionPurpose).Unprotect(token);
			return true;
		}
		catch (CryptographicException) {
			// A tampered, stale, or foreign token: treat it as a missing credential, never a server fault.
			return false;
		}
	}

	private static void ApplyStamps(JobTrackIdentityUser user, string securityStamp, string concurrencyStamp)
	{
		user.SecurityStamp = securityStamp;
		user.ConcurrencyStamp = concurrencyStamp;
	}

	private async Task LoadStateAsync(JobTrackIdentityUser user, CancellationToken cancellationToken)
	{
		ViewerZone = await viewerTimeZoneResolver.ResolveAsync(user.AppUserId, cancellationToken);
		Passkeys = await jobTrackClient.Credentials.ListPasskeysAsync(
			new() {
				ActorUserId = user.AppUserId,
				IdentityUserId = user.Id,
			}, cancellationToken);

		TwoFactorEnabled = await userManager.GetTwoFactorEnabledAsync(user);
		if (TwoFactorEnabled) {
			return;
		}

		var key = await userManager.GetAuthenticatorKeyAsync(user) ?? await GenerateAndPersistAuthenticatorKeyAsync(user);
		AuthenticatorKey = key;
		QrCodeDataUri = BuildQrCodeDataUri(user.UserName, key);
	}

	/// <summary>
	///     Generates a fresh authenticator key and persists it, tolerating a concurrent request doing the
	///     same thing for the first time (e.g. this page opened in two tabs before any key exists): if
	///     <see cref="JobTrackUserStore" />'s optimistic-concurrency-guarded update loses the race because
	///     another request's key committed first, that would otherwise surface as an unhandled
	///     <see cref="DbUpdateConcurrencyException" />; instead, <see cref="JobTrackUserStore.ReloadAsync" />
	///     overwrites <paramref name="user" />'s tracked values with what the winning request actually
	///     persisted -- re-querying by id here would not do that, since EF Core's identity map would just
	///     hand back this same tracked-but-never-persisted instance -- so both requests converge on the
	///     same key rather than one failing with a server error or, worse, rendering a key that was never
	///     actually saved.
	/// </summary>
	private async Task<string> GenerateAndPersistAuthenticatorKeyAsync(JobTrackIdentityUser user)
	{
		var key = userManager.GenerateNewAuthenticatorKey();
		await userStore.SetAuthenticatorKeyAsync(user, key, HttpContext.RequestAborted);
		try {
			var update = await userManager.UpdateAsync(user);
			if (!update.Succeeded) {
				throw new InvalidOperationException("Could not persist the pending authenticator key.");
			}
		}
		catch (DbUpdateConcurrencyException) {
			await userStore.ReloadAsync(user, HttpContext.RequestAborted);
			return await userManager.GetAuthenticatorKeyAsync(user)
				   ?? throw new InvalidOperationException("A concurrent authenticator key write lost the race but left no key persisted.");
		}

		return await userManager.GetAuthenticatorKeyAsync(user)
			   ?? throw new InvalidOperationException("The authenticator key was not persisted.");
	}

	private static string BuildQrCodeDataUri(string userName, string authenticatorKey)
	{
		var otpauthUri =
			$"otpauth://totp/{Uri.EscapeDataString(Issuer)}:{Uri.EscapeDataString(userName)}" +
			$"?secret={authenticatorKey}&issuer={Uri.EscapeDataString(Issuer)}&digits=6";

		using var generator = new QRCodeGenerator();
		using var qrData = generator.CreateQrCode(otpauthUri, QRCodeGenerator.ECCLevel.Q);
		using var pngQrCode = new PngByteQRCode(qrData);
		var pngBytes = pngQrCode.GetGraphic(QrPixelsPerModule);

		return $"data:image/png;base64,{Convert.ToBase64String(pngBytes)}";
	}

	private static void ApplyTwoFactorState(JobTrackIdentityUser user, SetTwoFactorStateResult updated)
	{
		user.SecurityStamp = updated.SecurityStamp;
		user.ConcurrencyStamp = updated.ConcurrencyStamp;
		user.TwoFactorEnabled = updated.TwoFactorEnabled;
		user.TwoFactorEnabledAt = updated.TwoFactorEnabledAt;
	}

	// Passkey enrolment collapses every server-side failure into one opaque browser message
	// (GenericAttestationFailure/CeremonyExpired) by design; without these the operator sees a 200 in
	// the request log and nothing about why. correlation_id ties the line to the AddPasskey audit row.
	[LoggerMessage(
		Level = LogLevel.Warning,
		Message = "passkey_enrolment_failed correlation_id={CorrelationId} stage={Stage}")]
	private static partial void LogPasskeyEnrolmentFailed(ILogger logger, Guid correlationId, string stage, Exception exception);

	[LoggerMessage(
		Level = LogLevel.Warning,
		Message = "passkey_enrolment_rejected correlation_id={CorrelationId} stage={Stage}")]
	private static partial void LogPasskeyEnrolmentRejected(ILogger logger, Guid correlationId, string stage);

	[LoggerMessage(
		Level = LogLevel.Warning,
		Message = "passkey_attestation_unverified correlation_id={CorrelationId} succeeded={Succeeded} has_passkey={HasPasskey} has_user_entity={HasUserEntity}")]
	private static partial void LogPasskeyAttestationUnverified(
		ILogger logger, Guid correlationId, bool succeeded, bool hasPasskey, bool hasUserEntity, Exception? exception);

	public sealed class ConfirmInput
	{
		[Required] public string Code { get; init; } = string.Empty;
	}

	public sealed class DisableInput
	{
		[Required]
		[Display(Name = "Current password")]
		public string CurrentPassword { get; init; } = string.Empty;
	}

	public sealed class AddPasskeyInput
	{
		[Display(Name = "Passkey name")] public string? Name { get; init; }
	}
}
