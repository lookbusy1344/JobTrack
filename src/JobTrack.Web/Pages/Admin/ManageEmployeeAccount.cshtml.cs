namespace JobTrack.Web.Pages.Admin;

using System.ComponentModel.DataAnnotations;
using Abstractions;
using Application;
using Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;

/// <summary>
///     §8.3/§8.5 slice 10: provisions new employee accounts and enables/disables/resets an existing
///     one's account (revocation of a role, including <see cref="EmployeeRole.Administrator" /> itself,
///     is <see cref="AssignRoleModel" />'s remit). Administrator-only. One page with three named
///     handlers rather than three pages, keeping the antiforgery/DI/actor-resolution boilerplate in one
///     place (mirrors <see cref="AssignRoleModel" />'s shape). Every input model is narrow and
///     allow-listed — none binds directly to <see cref="JobTrackIdentityUser" /> or any
///     domain/persistence entity (threat-model row 9: mass assignment).
/// </summary>
[Authorize(Policy = EmployeeRoleNames.Administrator)]
public sealed class ManageEmployeeAccountModel(IJobTrackClient jobTrackClient, UserManager<JobTrackIdentityUser> userManager) : PageModel
{
	private IReadOnlyDictionary<AppUserId, EmployeeDirectoryEntry> _employeeDirectoryById =
		new Dictionary<AppUserId, EmployeeDirectoryEntry>();

	[BindProperty] public CreateEmployeeInput CreateEmployee { get; set; } = new();

	[BindProperty] public SetEnabledInput SetEnabled { get; set; } = new();

	[BindProperty] public SetDefaultHourlyRateInput SetDefaultHourlyRate { get; set; } = new();

	[BindProperty] public ResetPasswordInput ResetPassword { get; set; } = new();

	[BindProperty] public ResetTwoFactorInput ResetTwoFactor { get; set; } = new();

	[BindProperty] public RevokeAllTokensInput RevokeAllTokens { get; set; } = new();

	[TempData] public string? ErrorMessage { get; set; }

	[TempData] public string? SuccessMessage { get; set; }

	public List<SelectListItem> TargetUserOptions { get; private set; } = [];

	public IReadOnlyList<SelectListItem> EmployeeRoleOptions { get; } = [
		new(EnumDisplay.Label(EmployeeRole.Administrator), EmployeeRole.Administrator.ToString()),
		new(EnumDisplay.Label(EmployeeRole.JobManager), EmployeeRole.JobManager.ToString()),
		new(EnumDisplay.Label(EmployeeRole.Worker), EmployeeRole.Worker.ToString()),
		new(EnumDisplay.Label(EmployeeRole.RateManager), EmployeeRole.RateManager.ToString()),
		new(EnumDisplay.Label(EmployeeRole.CostViewer), EmployeeRole.CostViewer.ToString()),
		new(EnumDisplay.Label(EmployeeRole.Auditor), EmployeeRole.Auditor.ToString()),
		new(EnumDisplay.Label(EmployeeRole.Requester), EmployeeRole.Requester.ToString()),
	];

	public async Task OnGetAsync(CancellationToken cancellationToken) => await LoadTargetUserOptionsAsync(cancellationToken);

	public async Task<IActionResult> OnPostCreateEmployeeAsync(CancellationToken cancellationToken)
	{
		ModelState.Clear();
		if (!TryValidateModel(CreateEmployee, nameof(CreateEmployee))) {
			await LoadTargetUserOptionsAsync(cancellationToken);
			return Page();
		}

		var actor = await ResolveActorAsync();
		if (actor is null) {
			return Challenge();
		}

		try {
			var result = await jobTrackClient.Employees.CreateEmployeeAsync(new() {
				Context = new() { Actor = actor.Value, CorrelationId = Guid.NewGuid() },
				DisplayName = CreateEmployee.DisplayName,
				IanaTimeZone = CreateEmployee.IanaTimeZone,
				DefaultHourlyRate = new HourlyRate(CreateEmployee.DefaultHourlyRate),
				UserName = CreateEmployee.UserName,
				Password = CreateEmployee.Password,
				Role = CreateEmployee.Role,
			}, cancellationToken);

			SuccessMessage =
				$"Employee {result.Id.Value} ({result.UserName}) created, holding: {string.Join(", ", result.Roles.Select(role => EnumDisplay.Label(role)))}.";
		}
		catch (AuthorizationDeniedException) {
			return Forbid();
		}
		catch (InvariantViolationException ex) {
			ErrorMessage = ex.Message;
		}

		return RedirectToPage();
	}

	public async Task<IActionResult> OnPostSetEnabledAsync(CancellationToken cancellationToken)
	{
		// Automatic model binding validates every [BindProperty] member up front, so ModelState
		// already carries a spurious error for ResetPassword's unset, [Required] NewPassword field
		// -- only this handler's input is present in the POST body. Clear before re-validating just
		// the relevant sub-model, since TryValidateModel's return value reflects the whole
		// ModelState, not only the prefix just (re)validated.
		ModelState.Clear();
		if (!TryValidateModel(SetEnabled, nameof(SetEnabled))) {
			await LoadTargetUserOptionsAsync(cancellationToken);
			return Page();
		}

		var actor = await ResolveActorAsync();
		if (actor is null) {
			return Challenge();
		}

		await LoadTargetUserOptionsAsync(cancellationToken);
		var targetUserId = new AppUserId(SetEnabled.TargetUserId);

		try {
			var result = await jobTrackClient.Employees.SetEnabledAsync(
				new() {
					Context = new() { Actor = actor.Value, CorrelationId = Guid.NewGuid() },
					TargetUserId = targetUserId,
					Enabled = SetEnabled.Enabled,
				}, cancellationToken);

			SuccessMessage =
				$"{EmployeeDirectoryDisplay.Describe(_employeeDirectoryById, targetUserId.Value, "That employee")} " +
				$"is now {(result.IsEnabled ? "enabled" : "disabled")}.";
		}
		catch (AuthorizationDeniedException) {
			return Forbid();
		}
		catch (EntityNotFoundException) {
			ErrorMessage = "That employee does not exist.";
		}

		return RedirectToPage();
	}

	public async Task<IActionResult> OnPostSetDefaultHourlyRateAsync(CancellationToken cancellationToken)
	{
		ModelState.Clear();
		if (!TryValidateModel(SetDefaultHourlyRate, nameof(SetDefaultHourlyRate))) {
			await LoadTargetUserOptionsAsync(cancellationToken);
			return Page();
		}

		var actor = await ResolveActorAsync();
		if (actor is null) {
			return Challenge();
		}

		await LoadTargetUserOptionsAsync(cancellationToken);
		var targetUserId = new AppUserId(SetDefaultHourlyRate.TargetUserId);

		try {
			var result = await jobTrackClient.Employees.SetDefaultHourlyRateAsync(
				new() {
					Context = new() { Actor = actor.Value, CorrelationId = Guid.NewGuid() },
					TargetUserId = targetUserId,
					DefaultHourlyRate = new(SetDefaultHourlyRate.DefaultHourlyRate),
				}, cancellationToken);

			SuccessMessage =
				$"{EmployeeDirectoryDisplay.Describe(_employeeDirectoryById, targetUserId.Value, result.DisplayName)} " +
				$"now has a default hourly rate of {result.DefaultHourlyRate!.Value.AmountPerHour:0.00}.";
		}
		catch (AuthorizationDeniedException) {
			return Forbid();
		}
		catch (EntityNotFoundException) {
			ErrorMessage = "That employee does not exist.";
		}

		return RedirectToPage();
	}

	public async Task<IActionResult> OnPostResetPasswordAsync(CancellationToken cancellationToken)
	{
		ModelState.Clear();
		if (!TryValidateModel(ResetPassword, nameof(ResetPassword))) {
			await LoadTargetUserOptionsAsync(cancellationToken);
			return Page();
		}

		var actor = await ResolveActorAsync();
		if (actor is null) {
			return Challenge();
		}

		await LoadTargetUserOptionsAsync(cancellationToken);
		var targetUserId = new AppUserId(ResetPassword.TargetUserId);

		try {
			_ = await jobTrackClient.Employees.ResetPasswordAsync(
				new() {
					Context = new() { Actor = actor.Value, CorrelationId = Guid.NewGuid() },
					TargetUserId = targetUserId,
					NewPassword = ResetPassword.NewPassword,
				}, cancellationToken);

			SuccessMessage =
				$"{EmployeeDirectoryDisplay.Describe(_employeeDirectoryById, targetUserId.Value, "That employee")}'s " +
				"password has been reset; they must change it at next sign-in.";
		}
		catch (AuthorizationDeniedException) {
			return Forbid();
		}
		catch (EntityNotFoundException) {
			ErrorMessage = "That employee does not exist.";
		}

		return RedirectToPage();
	}

	/// <summary>
	///     ADR 0037: clears an employee's TOTP two-factor enrolment when they have lost their
	///     authenticator device, mirroring <see cref="OnPostResetPasswordAsync" />'s shape.
	/// </summary>
	public async Task<IActionResult> OnPostResetTwoFactorAsync(CancellationToken cancellationToken)
	{
		ModelState.Clear();
		if (!TryValidateModel(ResetTwoFactor, nameof(ResetTwoFactor))) {
			await LoadTargetUserOptionsAsync(cancellationToken);
			return Page();
		}

		var actor = await ResolveActorAsync();
		if (actor is null) {
			return Challenge();
		}

		await LoadTargetUserOptionsAsync(cancellationToken);
		var targetUserId = new AppUserId(ResetTwoFactor.TargetUserId);

		try {
			_ = await jobTrackClient.Employees.ResetTwoFactorAsync(
				new() { Context = new() { Actor = actor.Value, CorrelationId = Guid.NewGuid() }, TargetUserId = targetUserId }, cancellationToken);

			SuccessMessage =
				$"Two-factor authentication has been reset for " +
				$"{EmployeeDirectoryDisplay.Describe(_employeeDirectoryById, targetUserId.Value, "that employee")}. " +
				"They can sign in with their password alone and re-enrol if they choose.";
		}
		catch (AuthorizationDeniedException) {
			return Forbid();
		}
		catch (EntityNotFoundException) {
			ErrorMessage = "That employee does not exist.";
		}

		return RedirectToPage();
	}

	/// <summary>
	///     Incident-response revocation (remediation §2.2): an administrator can cut off every one of
	///     another user's personal access tokens, but never mint one -- issuance stays strictly
	///     self-service (<see cref="Domain.Authorization.PersonalAccessTokenAccessPolicy.CanIssue" />).
	/// </summary>
	public async Task<IActionResult> OnPostRevokeAllTokensAsync(CancellationToken cancellationToken)
	{
		ModelState.Clear();
		if (!TryValidateModel(RevokeAllTokens, nameof(RevokeAllTokens))) {
			await LoadTargetUserOptionsAsync(cancellationToken);
			return Page();
		}

		var actor = await ResolveActorAsync();
		if (actor is null) {
			return Challenge();
		}

		await LoadTargetUserOptionsAsync(cancellationToken);
		var targetUserId = new AppUserId(RevokeAllTokens.TargetUserId);

		try {
			await jobTrackClient.Tokens.RevokeAllAsync(
				new() { Context = new() { Actor = actor.Value, CorrelationId = Guid.NewGuid() }, TargetUserId = targetUserId }, cancellationToken);

			SuccessMessage =
				"Every personal access token for " +
				$"{EmployeeDirectoryDisplay.Describe(_employeeDirectoryById, targetUserId.Value, "that employee")} has been revoked.";
		}
		catch (AuthorizationDeniedException) {
			return Forbid();
		}

		return RedirectToPage();
	}

	private async Task LoadTargetUserOptionsAsync(CancellationToken cancellationToken)
	{
		var actor = await ResolveActorAsync();
		if (actor is null) {
			return;
		}

		var directory = await jobTrackClient.Query.GetAllEmployeesAsync(
			new() { Context = new() { Actor = actor.Value, CorrelationId = Guid.NewGuid() } },
			cancellationToken);
		_employeeDirectoryById = directory.ToDictionary(entry => entry.Id);
		TargetUserOptions = EmployeeDirectoryDisplay.BuildOptions(directory);
	}

	private async Task<AppUserId?> ResolveActorAsync()
	{
		var actor = await userManager.GetUserAsync(User);
		return actor?.AppUserId;
	}

	public sealed class CreateEmployeeInput
	{
		public const decimal DefaultHourlyRateAmount = 20m;

		[Required] public string DisplayName { get; init; } = string.Empty;

		[Required] public string IanaTimeZone { get; init; } = string.Empty;

		[Range(typeof(decimal), "0.01", "999999.99")]
		public decimal DefaultHourlyRate { get; init; } = DefaultHourlyRateAmount;

		[Required] public string UserName { get; init; } = string.Empty;

		[Required] public string Password { get; init; } = string.Empty;

		[Required] public EmployeeRole Role { get; init; } = EmployeeRole.Worker;
	}

	public sealed class SetEnabledInput
	{
		[Required] public long TargetUserId { get; init; }

		public bool Enabled { get; init; }
	}

	public sealed class SetDefaultHourlyRateInput
	{
		[Required] public long TargetUserId { get; init; }

		[Range(typeof(decimal), "0.01", "999999.99")]
		public decimal DefaultHourlyRate { get; init; } = CreateEmployeeInput.DefaultHourlyRateAmount;
	}

	public sealed class ResetPasswordInput
	{
		[Required] public long TargetUserId { get; init; }

		[Required] public string NewPassword { get; init; } = string.Empty;
	}

	public sealed class ResetTwoFactorInput
	{
		[Required] public long TargetUserId { get; init; }
	}

	public sealed class RevokeAllTokensInput
	{
		[Required] public long TargetUserId { get; init; }
	}
}
