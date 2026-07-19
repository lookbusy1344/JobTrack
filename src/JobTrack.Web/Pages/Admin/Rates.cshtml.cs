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
using NodaTime;

/// <summary>
///     Employee cost-rate and node-rate-override administration (plan §8.5 slice 7, spec §9). The
///     page uses a coarse policy admitting administrators, rate managers, and cost viewers, then
///     relies on <see cref="Domain.Authorization.RateAccessPolicy" /> and
///     <see cref="Domain.Authorization.CostAccessPolicy" /> inside the library for the finer write/read
///     split. A RateManager therefore gets the write workflow without the read section.
/// </summary>
[Authorize(Policy = JobTrackPolicyNames.RateAdministration)]
public sealed class RatesModel(IJobTrackClient jobTrackClient, UserManager<JobTrackIdentityUser> userManager) : PageModel
{
	private IReadOnlyDictionary<AppUserId, EmployeeDirectoryEntry> _employeeDirectoryById =
		new Dictionary<AppUserId, EmployeeDirectoryEntry>();

	[BindProperty(SupportsGet = true)] public long UserId { get; init; }

	[BindProperty] public AddUserCostRateInput UserCostRateInput { get; set; } = new();

	[BindProperty] public AddNodeRateOverrideInput NodeRateOverrideInput { get; set; } = new();

	public RateSnapshotResult? Snapshot { get; private set; }

	public List<SelectListItem> UserOptions { get; private set; } = [];

	/// <summary>
	///     The displayed employee's display name and username, falling back to a numeric-id
	///     label if somehow absent from the loaded directory (see <see cref="IJobQueries.GetAllEmployeesAsync" />).
	/// </summary>
	public string DisplayedUserName => EmployeeDirectoryDisplay.Describe(_employeeDirectoryById, UserId, "Unknown");

	public bool CanViewRates { get; private set; }

	public bool CanManageRates { get; private set; }

	[TempData] public string? ErrorMessage { get; set; }

	[TempData] public string? SuccessMessage { get; set; }

	public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
	{
		var actor = await ResolveActorAsync();
		if (actor is null) {
			return Challenge();
		}

		await LoadAsync(actor.Value, cancellationToken);
		return Page();
	}

	public async Task<IActionResult> OnPostAddUserCostRateAsync(CancellationToken cancellationToken)
	{
		var actor = await ResolveActorAsync();
		if (actor is null) {
			return Challenge();
		}

		// Every [BindProperty] on the page model is bound and validated regardless of which handler
		// ran, so NodeRateOverrideInput's [Required] fields would otherwise fail validation on this
		// handler even though they were never posted -- validate only UserCostRateInput.
		ModelState.Clear();
		if (TryValidateModel(UserCostRateInput, nameof(UserCostRateInput))) {
			try {
				_ = await jobTrackClient.Rates.AddUserCostRateAsync(new() {
					Context = new() { Actor = actor.Value, CorrelationId = Guid.NewGuid() },
					UserId = new(UserId),
					Rate = new(
						new(UserCostRateInput.AmountPerHour),
						ToInstant(UserCostRateInput.EffectiveStart),
						UserCostRateInput.EffectiveEnd.HasValue ? ToInstant(UserCostRateInput.EffectiveEnd.Value) : null),
				}, cancellationToken);
				SuccessMessage = "User cost rate added.";
			}
			catch (AuthorizationDeniedException) {
				return Forbid();
			}
			catch (EntityNotFoundException) {
				ErrorMessage = "That employee does not exist.";
			}
			catch (InvariantViolationException ex) {
				ErrorMessage = ex.Message;
			}
			catch (ArgumentOutOfRangeException ex) {
				ErrorMessage = ex.Message;
			}

			return RedirectToPage(new { userId = UserId });
		}

		await LoadAsync(actor.Value, cancellationToken);
		return Page();
	}

	public async Task<IActionResult> OnPostAddNodeRateOverrideAsync(CancellationToken cancellationToken)
	{
		var actor = await ResolveActorAsync();
		if (actor is null) {
			return Challenge();
		}

		ModelState.Clear();
		if (TryValidateModel(NodeRateOverrideInput, nameof(NodeRateOverrideInput))) {
			try {
				_ = await jobTrackClient.Rates.AddNodeRateOverrideAsync(new() {
					Context = new() { Actor = actor.Value, CorrelationId = Guid.NewGuid() },
					UserId = new(UserId),
					Override = new(
						new(NodeRateOverrideInput.NodeId), new(NodeRateOverrideInput.AmountPerHour),
						ToInstant(NodeRateOverrideInput.EffectiveStart),
						NodeRateOverrideInput.EffectiveEnd.HasValue ? ToInstant(NodeRateOverrideInput.EffectiveEnd.Value) : null),
				}, cancellationToken);
				SuccessMessage = "Node rate override added.";
			}
			catch (AuthorizationDeniedException) {
				return Forbid();
			}
			catch (EntityNotFoundException) {
				ErrorMessage = "That employee or job node does not exist.";
			}
			catch (InvariantViolationException ex) {
				ErrorMessage = ex.Message;
			}
			catch (ArgumentOutOfRangeException ex) {
				ErrorMessage = ex.Message;
			}

			return RedirectToPage(new { userId = UserId });
		}

		await LoadAsync(actor.Value, cancellationToken);
		return Page();
	}

	private async Task LoadAsync(AppUserId actor, CancellationToken cancellationToken)
	{
		var directory = await jobTrackClient.Query.GetAllEmployeesAsync(
			new() { Context = new() { Actor = actor, CorrelationId = Guid.NewGuid() } },
			cancellationToken);
		_employeeDirectoryById = directory.ToDictionary(entry => entry.Id);
		UserOptions = EmployeeDirectoryDisplay.BuildOptions(directory);

		CanViewRates = User.IsInRole(EmployeeRoleNames.Administrator) || User.IsInRole(EmployeeRoleNames.CostViewer);
		CanManageRates = User.IsInRole(EmployeeRoleNames.Administrator) || User.IsInRole(EmployeeRoleNames.RateManager);
		if (!CanViewRates) {
			return;
		}

		try {
			Snapshot = await jobTrackClient.Query.GetRatesAsync(
				new() { Context = new() { Actor = actor, CorrelationId = Guid.NewGuid() }, UserId = new(UserId) }, cancellationToken);
		}
		catch (AuthorizationDeniedException) {
			ErrorMessage = "You may not view that employee's rates.";
		}
		catch (EntityNotFoundException) {
			ErrorMessage = "That employee does not exist.";
		}
	}

	private async Task<AppUserId?> ResolveActorAsync()
	{
		var actor = await userManager.GetUserAsync(User);
		return actor?.AppUserId;
	}

	private static Instant ToInstant(DateTimeOffset value) => Instant.FromDateTimeOffset(value);

	public sealed class AddUserCostRateInput
	{
		[Required] public DateTimeOffset EffectiveStart { get; set; }

		public DateTimeOffset? EffectiveEnd { get; set; }

		[Required] public decimal AmountPerHour { get; set; }
	}

	public sealed class AddNodeRateOverrideInput
	{
		[Required] public long NodeId { get; set; }

		[Required] public DateTimeOffset EffectiveStart { get; set; }

		public DateTimeOffset? EffectiveEnd { get; set; }

		[Required] public decimal AmountPerHour { get; set; }
	}
}
