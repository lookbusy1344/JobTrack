namespace JobTrack.Web.Pages.Account;

using System.ComponentModel.DataAnnotations;
using Application;
using Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using QRCoder;

/// <summary>
///     Self-service TOTP two-factor enrolment and disablement (ADR 0037). Enabling requires the
///     signed-in user to submit one valid code before <see cref="UserManager{TUser}.SetTwoFactorEnabledAsync" />
///     is called — proving the authenticator app was actually configured correctly, per spec §7.1's
///     bar against a non-functional enrolment flow. Disabling requires the current password, the same
///     re-authentication bar as any other credential-weakening self-service action.
/// </summary>
[Authorize(Policy = JobTrackPolicyNames.AnyAuthenticatedUser)]
public sealed class ManageTwoFactorModel(
	UserManager<JobTrackIdentityUser> userManager,
	SignInManager<JobTrackIdentityUser> signInManager,
	JobTrackUserStore userStore,
	IJobTrackClient jobTrackClient) : PageModel
{
	private const string Issuer = "JobTrack";
	private const int QrPixelsPerModule = 6;

	[BindProperty] public ConfirmInput Confirm { get; set; } = new();

	[BindProperty] public DisableInput Disable { get; set; } = new();

	public bool TwoFactorEnabled { get; private set; }

	public string? AuthenticatorKey { get; private set; }

	public string? QrCodeDataUri { get; private set; }

	[TempData] public string? ErrorMessage { get; set; }

	[TempData] public string? SuccessMessage { get; set; }

	public async Task<IActionResult> OnGetAsync()
	{
		var user = await userManager.GetUserAsync(User);
		if (user is null) {
			return Challenge();
		}

		await LoadStateAsync(user);
		return Page();
	}

	public async Task<IActionResult> OnPostConfirmAsync()
	{
		var user = await userManager.GetUserAsync(User);
		if (user is null) {
			return Challenge();
		}

		ModelState.Clear();
		if (!TryValidateModel(Confirm, nameof(Confirm))) {
			await LoadStateAsync(user);
			return Page();
		}

		var codeIsValid = await userManager.VerifyTwoFactorTokenAsync(user, TokenOptions.DefaultAuthenticatorProvider, Confirm.Code);
		if (!codeIsValid) {
			ErrorMessage = "That code is incorrect. Check your authenticator app and try again.";
			await LoadStateAsync(user);
			return Page();
		}

		var updated = await jobTrackClient.Credentials.SetTwoFactorStateAsync(
			new() {
				ActorUserId = user.AppUserId,
				IdentityUserId = user.Id,
				Enabled = true,
				CorrelationId = Guid.NewGuid(),
			});
		ApplyTwoFactorState(user, updated);
		await signInManager.RefreshSignInAsync(user);
		SuccessMessage = "Two-factor authentication is now enabled on your account.";
		return RedirectToPage();
	}

	public async Task<IActionResult> OnPostDisableAsync()
	{
		var user = await userManager.GetUserAsync(User);
		if (user is null) {
			return Challenge();
		}

		ModelState.Clear();
		if (!TryValidateModel(Disable, nameof(Disable))) {
			await LoadStateAsync(user);
			return Page();
		}

		var passwordIsValid = await userManager.CheckPasswordAsync(user, Disable.CurrentPassword);
		if (!passwordIsValid) {
			ErrorMessage = "That password is incorrect.";
			await LoadStateAsync(user);
			return Page();
		}

		var updated = await jobTrackClient.Credentials.SetTwoFactorStateAsync(
			new() {
				ActorUserId = user.AppUserId,
				IdentityUserId = user.Id,
				Enabled = false,
				CorrelationId = Guid.NewGuid(),
			});
		user.AuthenticatorKeyProtected = null;
		ApplyTwoFactorState(user, updated);
		await signInManager.RefreshSignInAsync(user);
		SuccessMessage = "Two-factor authentication has been disabled on your account.";
		return RedirectToPage();
	}

	private async Task LoadStateAsync(JobTrackIdentityUser user)
	{
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

	public sealed class ConfirmInput
	{
		[Required] public string Code { get; init; } = string.Empty;
	}

	public sealed class DisableInput
	{
		[Required] public string CurrentPassword { get; init; } = string.Empty;
	}
}
