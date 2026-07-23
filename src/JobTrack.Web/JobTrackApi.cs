namespace JobTrack.Web;

using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Text.Json.Serialization;
using Abstractions;
using Application;
using Domain.Costing;
using Domain.Hierarchy;
using Domain.Rates;
using Domain.Schedules;
using Identity;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using NodaTime;
using NodaTime.TimeZones;

internal static partial class JobTrackApi
{
	private const string ApiPathPrefix = "/api";
	private const string OpenApiDocumentName = "v1";

	/// <summary>
	///     Shared external API paging contract (remediation plan §3.1): every growable collection
	///     endpoint defaults to this page size and rejects nothing larger -- an oversized
	///     <c>pageSize</c> query parameter is silently clamped down to <see cref="MaxPageSize" /> rather
	///     than rejected, since the caller's intent ("give me as many as you'll allow") is unambiguous.
	/// </summary>
	private const int DefaultPageSize = 50;

	/// <summary>Maximum page size any growable collection endpoint returns in one response (remediation plan §3.1).</summary>
	private const int MaxPageSize = 200;

	/// <summary>Per-client/per-user rate-limiting policy name (plan §4.4), registered in <c>Program.cs</c>.</summary>
	internal const string RateLimiterPolicyName = "api";

	// Same-origin JSON writes on `/api/*` rely solely on the Identity cookie for authentication,
	// so they need CSRF protection independent of Razor Pages' form-field antiforgery (plan
	// §8.1 fix 2.1). JSON callers cannot submit a hidden form field, so the token travels in this
	// header instead; `Program.cs` wires it to `AntiforgeryOptions.HeaderName`.
	public const string AntiforgeryHeaderName = "X-CSRF-TOKEN";

	/// <summary>
	///     Shared between <see cref="ExecuteAsync" />'s <see cref="AuthorizationDeniedException" /> catch
	///     clause and the bearer scheme's forbid handler (<see cref="PersonalAccessTokenAuthenticationHandler" />),
	///     so a role-policy denial at the ASP.NET Core authorization-middleware layer (before a handler
	///     ever runs) and a library-level ownership/subtree denial (inside a handler) report the
	///     identical problem shape (remediation plan §3.4) — bearer requests get problem-details JSON on
	///     403, not just on 401.
	/// </summary>
	internal const string ForbiddenProblemType = "/problems/authorization-denied";

	private const string NotFoundProblemType = "/problems/entity-not-found";
	private const string InvariantProblemType = "/problems/invariant-violation";
	private const string ConcurrencyProblemType = "/problems/concurrency-conflict";
	private const string ValidationProblemType = "/problems/validation";
	private const string BlockedProblemType = "/problems/prerequisite-blocked";
	private const string MissingRateProblemType = "/problems/missing-rate";
	private const string StoredTimeZoneRotProblemType = "/problems/stored-time-zone-not-recognized";

	/// <summary>
	///     Shared between the cookie scheme's <see cref="HandleRedirectAsync" /> and the bearer scheme's
	///     challenge handler (<see cref="PersonalAccessTokenAuthenticationHandler" />) so every
	///     authentication failure -- missing, empty, malformed, expired, revoked, or a disabled
	///     account's token -- reports the identical problem <c>type</c> regardless of which scheme or
	///     cause produced it (remediation plan §3.3): a caller cannot distinguish failure reasons by
	///     inspecting the response.
	/// </summary>
	internal const string AuthenticationProblemType = "/problems/authentication-required";

	public static IServiceCollection AddJobTrackApi(this IServiceCollection services)
	{
		_ = services.AddOpenApi(OpenApiDocumentName, options => options.AddDocumentTransformer<BearerSecuritySchemeTransformer>());
		_ = services.AddProblemDetails();
		_ = services.ConfigureHttpJsonOptions(options =>
			options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

		return services;
	}

	public static void MapJobTrackApi(this WebApplication app)
	{
		// Unauthenticated route/schema discovery lowers reconnaissance cost against an
		// employee-only, single-organisation system for no operational benefit (security review
		// remediation §2.5) -- gated behind the same policy every other authenticated endpoint uses,
		// reachable by either the cookie scheme or a bearer PAT.
		_ = app.MapOpenApi($"/openapi/{OpenApiDocumentName}.json").RequireAuthorization(JobTrackPolicyNames.AnyEmployee);

		var api = app.MapGroup(ApiPathPrefix)
			.WithTags("JobTrack API")
			.RequireRateLimiting(RateLimiterPolicyName)
			.AddEndpointFilter<ApiTelemetryFilter>()
			.AddEndpointFilter<RequiresPasswordChangeEndpointFilter>()
			.ProducesProblem(StatusCodes.Status429TooManyRequests)
			.ProducesProblem(StatusCodes.Status403Forbidden);

		_ = api.MapGet("/antiforgery-token", GetAntiforgeryToken)
			.RequireAuthorization(JobTrackPolicyNames.AnyAuthenticatedUser)
			.WithName("GetAntiforgeryToken")
			.WithSummary($"Get a CSRF token to send back in the '{AntiforgeryHeaderName}' header on state-changing API requests.")
			.Produces<AntiforgeryTokenResponse>()
			.ProducesProblem(StatusCodes.Status401Unauthorized);

		_ = api.MapGet("/employees/{userId:long}/rates", GetRatesAsync)
			.RequireAuthorization(JobTrackPolicyNames.RateRead)
			.WithName("GetEmployeeRates")
			.WithSummary("Get one employee's user cost rates and node rate overrides (bounded; see plan §3.1).")
			.Produces<RatesResponse>()
			.ProducesProblem(StatusCodes.Status400BadRequest)
			.ProducesProblem(StatusCodes.Status401Unauthorized)
			.ProducesProblem(StatusCodes.Status403Forbidden)
			.ProducesProblem(StatusCodes.Status404NotFound);

		_ = api.MapPost("/employees/{userId:long}/rates/user-cost-rates", AddUserCostRateAsync)
			.RequireAuthorization(JobTrackPolicyNames.RateWrite)
			.AddEndpointFilter<AntiforgeryValidationFilter>()
			.WithName("AddUserCostRate")
			.WithSummary("Add an effective-dated user cost rate for one employee.")
			.Produces<UserCostRateResponse>(StatusCodes.Status201Created)
			.ProducesProblem(StatusCodes.Status400BadRequest)
			.ProducesProblem(StatusCodes.Status401Unauthorized)
			.ProducesProblem(StatusCodes.Status403Forbidden)
			.ProducesProblem(StatusCodes.Status404NotFound)
			.ProducesProblem(StatusCodes.Status409Conflict)
			.ProducesProblem(StatusCodes.Status413PayloadTooLarge);

		_ = api.MapPost("/employees/{userId:long}/rates/node-rate-overrides", AddNodeRateOverrideAsync)
			.RequireAuthorization(JobTrackPolicyNames.RateWrite)
			.AddEndpointFilter<AntiforgeryValidationFilter>()
			.WithName("AddNodeRateOverride")
			.WithSummary("Add an effective-dated node rate override for one employee.")
			.Produces<NodeRateOverrideResponse>(StatusCodes.Status201Created)
			.ProducesProblem(StatusCodes.Status400BadRequest)
			.ProducesProblem(StatusCodes.Status401Unauthorized)
			.ProducesProblem(StatusCodes.Status403Forbidden)
			.ProducesProblem(StatusCodes.Status404NotFound)
			.ProducesProblem(StatusCodes.Status409Conflict)
			.ProducesProblem(StatusCodes.Status413PayloadTooLarge);

		_ = api.MapPost("/employees/{userId:long}/rates/user-cost-rates/{rateId:long}/correct", CorrectUserCostRateAsync)
			.RequireAuthorization(JobTrackPolicyNames.RateWrite)
			.AddEndpointFilter<AntiforgeryValidationFilter>()
			.WithName("CorrectUserCostRate")
			.WithSummary("Correct a historical user cost rate's effective range and amount, with an audited reason.")
			.Produces<UserCostRateResponse>()
			.ProducesProblem(StatusCodes.Status400BadRequest)
			.ProducesProblem(StatusCodes.Status401Unauthorized)
			.ProducesProblem(StatusCodes.Status403Forbidden)
			.ProducesProblem(StatusCodes.Status404NotFound)
			.ProducesProblem(StatusCodes.Status409Conflict)
			.ProducesProblem(StatusCodes.Status413PayloadTooLarge);

		_ = api.MapPost("/employees/{userId:long}/rates/node-rate-overrides/{overrideId:long}/correct", CorrectNodeRateOverrideAsync)
			.RequireAuthorization(JobTrackPolicyNames.RateWrite)
			.AddEndpointFilter<AntiforgeryValidationFilter>()
			.WithName("CorrectNodeRateOverride")
			.WithSummary("Correct a historical node rate override's effective range and amount, with an audited reason.")
			.Produces<NodeRateOverrideResponse>()
			.ProducesProblem(StatusCodes.Status400BadRequest)
			.ProducesProblem(StatusCodes.Status401Unauthorized)
			.ProducesProblem(StatusCodes.Status403Forbidden)
			.ProducesProblem(StatusCodes.Status404NotFound)
			.ProducesProblem(StatusCodes.Status409Conflict)
			.ProducesProblem(StatusCodes.Status413PayloadTooLarge);

		_ = api.MapGet("/jobs/root", GetRootJobNodeAsync)
			.RequireAuthorization(JobTrackPolicyNames.AnyEmployee)
			.WithName("GetRootJobNode")
			.WithSummary("Get the permanent root node's detail.")
			.Produces<JobNodeDetailResponse>()
			.ProducesProblem(StatusCodes.Status401Unauthorized);

		_ = api.MapGet("/jobs/search", SearchJobNodesAsync)
			.RequireAuthorization(JobTrackPolicyNames.AnyEmployee)
			.WithName("SearchJobNodes")
			.WithSummary("Search every node's description for a case-insensitive substring match, paged (offset/pageSize).")
			.Produces<PagedResponse<JobNodeSummaryResponse>>()
			.ProducesProblem(StatusCodes.Status400BadRequest)
			.ProducesProblem(StatusCodes.Status401Unauthorized);

		_ = api.MapGet("/jobs/{nodeId:long}", GetJobNodeAsync)
			.RequireAuthorization(JobTrackPolicyNames.AnyEmployee)
			.WithName("GetJobNode")
			.WithSummary("Get a node's full detail and root-first ancestor breadcrumb.")
			.Produces<JobNodeDetailResponse>()
			.ProducesProblem(StatusCodes.Status401Unauthorized)
			.ProducesProblem(StatusCodes.Status404NotFound);

		_ = api.MapGet("/jobs/{nodeId:long}/children", GetJobChildrenAsync)
			.RequireAuthorization(JobTrackPolicyNames.AnyEmployee)
			.WithName("GetJobChildren")
			.WithSummary("Get a node's direct children, filtered by owner and archive scope, paged (offset/pageSize).")
			.Produces<PagedResponse<JobNodeSummaryResponse>>()
			.ProducesProblem(StatusCodes.Status400BadRequest)
			.ProducesProblem(StatusCodes.Status401Unauthorized)
			.ProducesProblem(StatusCodes.Status404NotFound);

		_ = api.MapPost("/jobs/{nodeId:long}/pickup", PickUpJobNodeAsync)
			.RequireAuthorization(JobTrackPolicyNames.JobWorkflow)
			.AddEndpointFilter<AntiforgeryValidationFilter>()
			.WithName("PickUpJobNode")
			.WithSummary("Claim an unassigned node from the pickup pool, setting its direct owner to the acting user.")
			.Produces<JobNodeResponse>()
			.ProducesProblem(StatusCodes.Status401Unauthorized)
			.ProducesProblem(StatusCodes.Status403Forbidden)
			.ProducesProblem(StatusCodes.Status404NotFound)
			.ProducesProblem(StatusCodes.Status409Conflict);

		_ = api.MapGet("/jobs/{nodeId:long}/readiness", GetReadinessAsync)
			.RequireAuthorization(JobTrackPolicyNames.AnyEmployee)
			.WithName("GetJobReadiness")
			.WithSummary("Get whether a node's prerequisites are satisfied, and the diagnostic set of blockers if not.")
			.Produces<ReadinessResponse>()
			.ProducesProblem(StatusCodes.Status401Unauthorized)
			.ProducesProblem(StatusCodes.Status404NotFound);

		_ = api.MapGet("/jobs/{nodeId:long}/sessions", GetLeafSessionsAsync)
			.RequireAuthorization(JobTrackPolicyNames.JobWorkflow)
			.WithName("GetLeafSessions")
			.WithSummary("Get one worker's sessions on a leaf, most recent first, paged (offset/pageSize).")
			.Produces<PagedResponse<WorkSessionResponse>>()
			.ProducesProblem(StatusCodes.Status400BadRequest)
			.ProducesProblem(StatusCodes.Status401Unauthorized)
			.ProducesProblem(StatusCodes.Status403Forbidden)
			.ProducesProblem(StatusCodes.Status404NotFound);

		_ = api.MapPost("/jobs/{nodeId:long}/sessions", StartSessionAsync)
			.RequireAuthorization(JobTrackPolicyNames.JobWorkflow)
			.AddEndpointFilter<AntiforgeryValidationFilter>()
			.WithName("StartSession")
			.WithSummary(
				"Start a new work session on a leaf. Calling this again for an already-active worker/leaf pair is how a UI \"resume\" action is expressed.")
			.Produces<WorkSessionResponse>(StatusCodes.Status201Created)
			.ProducesProblem(StatusCodes.Status400BadRequest)
			.ProducesProblem(StatusCodes.Status401Unauthorized)
			.ProducesProblem(StatusCodes.Status403Forbidden)
			.ProducesProblem(StatusCodes.Status404NotFound)
			.ProducesProblem(StatusCodes.Status409Conflict)
			.ProducesProblem(StatusCodes.Status413PayloadTooLarge);

		_ = api.MapPost("/jobs/{nodeId:long}/sessions/{sessionId:long}/finish", FinishSessionAsync)
			.RequireAuthorization(JobTrackPolicyNames.JobWorkflow)
			.AddEndpointFilter<AntiforgeryValidationFilter>()
			.WithName("FinishSession")
			.WithSummary("Finish the active session. \"Pause\" and \"stop\" are UI descriptions of this same operation.")
			.Produces<WorkSessionResponse>()
			.ProducesProblem(StatusCodes.Status400BadRequest)
			.ProducesProblem(StatusCodes.Status401Unauthorized)
			.ProducesProblem(StatusCodes.Status403Forbidden)
			.ProducesProblem(StatusCodes.Status404NotFound)
			.ProducesProblem(StatusCodes.Status409Conflict)
			.ProducesProblem(StatusCodes.Status413PayloadTooLarge);

		_ = api.MapPost("/jobs/{nodeId:long}/sessions/{sessionId:long}/finish-and-update-write-up", FinishSessionAndUpdateWriteUpAsync)
			.RequireAuthorization(JobTrackPolicyNames.JobWorkflow)
			.AddEndpointFilter<AntiforgeryValidationFilter>()
			.WithName("FinishSessionAndUpdateWriteUp")
			.WithSummary(
				"Atomic composite (remediation plan §2.1): finish the active session and, optionally, apply a write-up change to its leaf's node, in one commit. The plain finish endpoint above remains for a caller with no write-up to change.")
			.Produces<FinishSessionAndUpdateWriteUpResponse>()
			.ProducesProblem(StatusCodes.Status400BadRequest)
			.ProducesProblem(StatusCodes.Status401Unauthorized)
			.ProducesProblem(StatusCodes.Status403Forbidden)
			.ProducesProblem(StatusCodes.Status404NotFound)
			.ProducesProblem(StatusCodes.Status409Conflict)
			.ProducesProblem(StatusCodes.Status413PayloadTooLarge);

		_ = api.MapPost("/jobs/{nodeId:long}/sessions/{sessionId:long}/correct", CorrectSessionAsync)
			.RequireAuthorization(JobTrackPolicyNames.JobWorkflow)
			.AddEndpointFilter<AntiforgeryValidationFilter>()
			.WithName("CorrectSession")
			.WithSummary("Correct a historical session's start and/or finish instants, with an audited reason.")
			.Produces<WorkSessionResponse>()
			.ProducesProblem(StatusCodes.Status400BadRequest)
			.ProducesProblem(StatusCodes.Status401Unauthorized)
			.ProducesProblem(StatusCodes.Status403Forbidden)
			.ProducesProblem(StatusCodes.Status404NotFound)
			.ProducesProblem(StatusCodes.Status409Conflict)
			.ProducesProblem(StatusCodes.Status413PayloadTooLarge);

		_ = api.MapGet("/jobs/{nodeId:long}/prerequisites", GetPrerequisitesAsync)
			.RequireAuthorization(JobTrackPolicyNames.AnyEmployee)
			.WithName("GetPrerequisites")
			.WithSummary("Get every prerequisite edge touching a node, in either direction, paged (offset/pageSize).")
			.Produces<PagedResponse<PrerequisiteEdgeResponse>>()
			.ProducesProblem(StatusCodes.Status400BadRequest)
			.ProducesProblem(StatusCodes.Status401Unauthorized)
			.ProducesProblem(StatusCodes.Status404NotFound);

		_ = api.MapPost("/jobs/{nodeId:long}/prerequisites", AddPrerequisiteAsync)
			.RequireAuthorization(JobTrackPolicyNames.JobWorkflow)
			.AddEndpointFilter<AntiforgeryValidationFilter>()
			.WithName("AddPrerequisite")
			.WithSummary("Add a prerequisite edge: the given job must reach Success before this node is ready.")
			.Produces(StatusCodes.Status204NoContent)
			.ProducesProblem(StatusCodes.Status400BadRequest)
			.ProducesProblem(StatusCodes.Status401Unauthorized)
			.ProducesProblem(StatusCodes.Status403Forbidden)
			.ProducesProblem(StatusCodes.Status404NotFound)
			.ProducesProblem(StatusCodes.Status409Conflict)
			.ProducesProblem(StatusCodes.Status413PayloadTooLarge);

		_ = api.MapDelete("/jobs/{nodeId:long}/prerequisites/{requiredJobId:long}", RemovePrerequisiteAsync)
			.RequireAuthorization(JobTrackPolicyNames.JobWorkflow)
			.AddEndpointFilter<AntiforgeryValidationFilter>()
			.WithName("RemovePrerequisite")
			.WithSummary("Remove a prerequisite edge.")
			.Produces(StatusCodes.Status204NoContent)
			.ProducesProblem(StatusCodes.Status401Unauthorized)
			.ProducesProblem(StatusCodes.Status403Forbidden)
			.ProducesProblem(StatusCodes.Status404NotFound);

		_ = api.MapGet("/jobs/{nodeId:long}/achievement", GetLeafWorkAsync)
			.RequireAuthorization(JobTrackPolicyNames.AnyEmployee)
			.WithName("GetLeafWork")
			.WithSummary("Get a leaf's current achievement state.")
			.Produces<LeafWorkResponse>()
			.ProducesProblem(StatusCodes.Status401Unauthorized)
			.ProducesProblem(StatusCodes.Status404NotFound);

		_ = api.MapPut("/jobs/{nodeId:long}/achievement", SetAchievementAsync)
			.RequireAuthorization(JobTrackPolicyNames.JobWorkflow)
			.AddEndpointFilter<AntiforgeryValidationFilter>()
			.WithName("SetAchievement")
			.WithSummary("Transition a leaf's achievement state, with an audited reason.")
			.Produces<LeafWorkResponse>()
			.ProducesProblem(StatusCodes.Status400BadRequest)
			.ProducesProblem(StatusCodes.Status401Unauthorized)
			.ProducesProblem(StatusCodes.Status403Forbidden)
			.ProducesProblem(StatusCodes.Status404NotFound)
			.ProducesProblem(StatusCodes.Status409Conflict)
			.ProducesProblem(StatusCodes.Status413PayloadTooLarge);

		_ = api.MapPost("/jobs/{nodeId:long}/complete", CompleteLeafAsync)
			.RequireAuthorization(JobTrackPolicyNames.JobWorkflow)
			.AddEndpointFilter<AntiforgeryValidationFilter>()
			.WithName("CompleteLeaf")
			.WithSummary(
				"Atomically finish the exact confirmed active-session set and record an achievement -- Success by default, or Cancelled/Unsuccessful (ADR 0045/0047). Composite of finish-session(s) and set-achievement.")
			.Produces<CompleteLeafResponse>()
			.ProducesProblem(StatusCodes.Status400BadRequest)
			.ProducesProblem(StatusCodes.Status401Unauthorized)
			.ProducesProblem(StatusCodes.Status403Forbidden)
			.ProducesProblem(StatusCodes.Status404NotFound)
			.ProducesProblem(StatusCodes.Status409Conflict)
			.ProducesProblem(StatusCodes.Status413PayloadTooLarge);

		_ = api.MapPost("/jobs/{nodeId:long}/reopen-and-start-session", ReopenAndStartWorkAsync)
			.RequireAuthorization(JobTrackPolicyNames.JobWorkflow)
			.AddEndpointFilter<AntiforgeryValidationFilter>()
			.WithName("ReopenAndStartWork")
			.WithSummary(
				"Atomically reopen a terminal leaf to Waiting, auto-advance to InProgress (ADR 0038), and start the target worker's session (ADR 0045).")
			.Produces<ReopenAndStartWorkResponse>(StatusCodes.Status201Created)
			.ProducesProblem(StatusCodes.Status400BadRequest)
			.ProducesProblem(StatusCodes.Status401Unauthorized)
			.ProducesProblem(StatusCodes.Status403Forbidden)
			.ProducesProblem(StatusCodes.Status404NotFound)
			.ProducesProblem(StatusCodes.Status409Conflict)
			.ProducesProblem(StatusCodes.Status413PayloadTooLarge);

		_ = api.MapGet("/jobs/{nodeId:long}/cost", GetCostDetailsAsync)
			.RequireAuthorization(JobTrackPolicyNames.RateRead)
			.WithName("GetCostDetails")
			.WithSummary("Get one node's exact and displayed cost, with its rate-provenance segment trace (bounded; see plan §3.1).")
			.Produces<CostDetailsResponse>()
			.ProducesProblem(StatusCodes.Status400BadRequest)
			.ProducesProblem(StatusCodes.Status401Unauthorized)
			.ProducesProblem(StatusCodes.Status403Forbidden)
			.ProducesProblem(StatusCodes.Status404NotFound)
			.ProducesProblem(StatusCodes.Status422UnprocessableEntity);

		_ = api.MapGet("/jobs/{nodeId:long}/cost/hierarchy", GetHierarchyTotalsAsync)
			.RequireAuthorization(JobTrackPolicyNames.RateRead)
			.WithName("GetHierarchyTotals")
			.WithSummary("Get reconciled cost totals for a node and its entire subtree (bounded; see plan §3.1).")
			.Produces<HierarchyTotalsResponse>()
			.ProducesProblem(StatusCodes.Status400BadRequest)
			.ProducesProblem(StatusCodes.Status401Unauthorized)
			.ProducesProblem(StatusCodes.Status403Forbidden)
			.ProducesProblem(StatusCodes.Status404NotFound)
			.ProducesProblem(StatusCodes.Status422UnprocessableEntity);

		_ = api.MapGet("/jobs/{nodeId:long}/subtree", GetJobSubtreeAsync)
			.RequireAuthorization(JobTrackPolicyNames.AnyEmployee)
			.WithName("GetJobSubtree")
			.WithSummary(
				"Get a bounded multi-level subtree rooted at a node (depth/breadth-capped, ADR 0039); " +
				"the cost roll-up is included only when the actor may view it (ADR 0040), never a whole-request denial.")
			.Produces<JobSubtreeResponse>()
			.ProducesProblem(StatusCodes.Status400BadRequest)
			.ProducesProblem(StatusCodes.Status401Unauthorized)
			.ProducesProblem(StatusCodes.Status404NotFound);

		_ = api.MapGet("/employees/{userId:long}/schedule", GetScheduleAsync)
			.RequireAuthorization(JobTrackPolicyNames.ScheduleAdministration)
			.WithName("GetEmployeeSchedule")
			.WithSummary("Get one employee's schedule versions and exceptions (bounded; see plan §3.1).")
			.Produces<ScheduleResponse>()
			.ProducesProblem(StatusCodes.Status400BadRequest)
			.ProducesProblem(StatusCodes.Status401Unauthorized)
			.ProducesProblem(StatusCodes.Status403Forbidden)
			.ProducesProblem(StatusCodes.Status404NotFound);

		_ = api.MapPost("/employees/{userId:long}/schedule/versions", AddScheduleVersionAsync)
			.RequireAuthorization(JobTrackPolicyNames.ScheduleAdministration)
			.AddEndpointFilter<AntiforgeryValidationFilter>()
			.WithName("AddScheduleVersion")
			.WithSummary("Add an effective-dated schedule version for one employee.")
			.Produces<ScheduleVersionResponse>(StatusCodes.Status201Created)
			.ProducesProblem(StatusCodes.Status400BadRequest)
			.ProducesProblem(StatusCodes.Status401Unauthorized)
			.ProducesProblem(StatusCodes.Status403Forbidden)
			.ProducesProblem(StatusCodes.Status404NotFound)
			.ProducesProblem(StatusCodes.Status409Conflict)
			.ProducesProblem(StatusCodes.Status413PayloadTooLarge);

		_ = api.MapPost("/employees/{userId:long}/schedule/exceptions", AddScheduleExceptionAsync)
			.RequireAuthorization(JobTrackPolicyNames.ScheduleAdministration)
			.AddEndpointFilter<AntiforgeryValidationFilter>()
			.WithName("AddScheduleException")
			.WithSummary("Add a dated schedule exception for one employee.")
			.Produces<ScheduleExceptionResponse>(StatusCodes.Status201Created)
			.ProducesProblem(StatusCodes.Status400BadRequest)
			.ProducesProblem(StatusCodes.Status401Unauthorized)
			.ProducesProblem(StatusCodes.Status403Forbidden)
			.ProducesProblem(StatusCodes.Status404NotFound)
			.ProducesProblem(StatusCodes.Status409Conflict)
			.ProducesProblem(StatusCodes.Status413PayloadTooLarge);

		_ = api.MapPost("/employees/{userId:long}/schedule/versions/{versionId:long}/correct", CorrectScheduleVersionAsync)
			.RequireAuthorization(JobTrackPolicyNames.ScheduleAdministration)
			.AddEndpointFilter<AntiforgeryValidationFilter>()
			.WithName("CorrectScheduleVersion")
			.WithSummary("Correct a historical schedule version's effective range, zone, and weekly intervals, with an audited reason.")
			.Produces<ScheduleVersionResponse>()
			.ProducesProblem(StatusCodes.Status400BadRequest)
			.ProducesProblem(StatusCodes.Status401Unauthorized)
			.ProducesProblem(StatusCodes.Status403Forbidden)
			.ProducesProblem(StatusCodes.Status404NotFound)
			.ProducesProblem(StatusCodes.Status409Conflict)
			.ProducesProblem(StatusCodes.Status413PayloadTooLarge);

		_ = api.MapPost("/employees/{userId:long}/schedule/exceptions/{exceptionId:long}/correct", CorrectScheduleExceptionAsync)
			.RequireAuthorization(JobTrackPolicyNames.ScheduleAdministration)
			.AddEndpointFilter<AntiforgeryValidationFilter>()
			.WithName("CorrectScheduleException")
			.WithSummary("Correct a historical schedule exception's effect, interval, and rate override, with an audited reason.")
			.Produces<ScheduleExceptionResponse>()
			.ProducesProblem(StatusCodes.Status400BadRequest)
			.ProducesProblem(StatusCodes.Status401Unauthorized)
			.ProducesProblem(StatusCodes.Status403Forbidden)
			.ProducesProblem(StatusCodes.Status404NotFound)
			.ProducesProblem(StatusCodes.Status409Conflict)
			.ProducesProblem(StatusCodes.Status413PayloadTooLarge);

		_ = api.MapGet("/request-holding-areas", GetEligibleHoldingAreasAsync)
			.RequireAuthorization(JobTrackPolicyNames.RequesterAccess)
			.WithName("GetEligibleHoldingAreas")
			.WithSummary("Get the active holding areas the acting requester may currently submit a request into (ADR 0033).")
			.Produces<HoldingAreaResponse[]>()
			.ProducesProblem(StatusCodes.Status401Unauthorized)
			.ProducesProblem(StatusCodes.Status403Forbidden);

		_ = api.MapPost("/requests", SubmitRequestAsync)
			.RequireAuthorization(JobTrackPolicyNames.RequesterAccess)
			.AddEndpointFilter<AntiforgeryValidationFilter>()
			.WithName("SubmitRequest")
			.WithSummary("Submit a new request into a holding area the acting requester is eligible for (ADR 0033).")
			.Produces<RequestResponse>(StatusCodes.Status201Created)
			.ProducesProblem(StatusCodes.Status400BadRequest)
			.ProducesProblem(StatusCodes.Status401Unauthorized)
			.ProducesProblem(StatusCodes.Status403Forbidden)
			.ProducesProblem(StatusCodes.Status404NotFound)
			.ProducesProblem(StatusCodes.Status413PayloadTooLarge);

		_ = api.MapGet("/requests", GetMyRequestsAsync)
			.RequireAuthorization(JobTrackPolicyNames.RequesterAccess)
			.WithName("GetMyRequests")
			.WithSummary("Get the acting requester's own submitted requests, most recent first (ADR 0033).")
			.Produces<RequestResponse[]>()
			.ProducesProblem(StatusCodes.Status401Unauthorized)
			.ProducesProblem(StatusCodes.Status403Forbidden);

		_ = api.MapGet("/requests/{jobNodeId:long}", GetRequestDetailAsync)
			.RequireAuthorization(JobTrackPolicyNames.RequestDetailAccess)
			.WithName("GetRequestDetail")
			.WithSummary("Get one permitted request's requester-safe detail: status, read-only subtree, and visible notes (ADR 0034).")
			.Produces<RequestDetailResponse>()
			.ProducesProblem(StatusCodes.Status401Unauthorized)
			.ProducesProblem(StatusCodes.Status403Forbidden)
			.ProducesProblem(StatusCodes.Status404NotFound)
			.ProducesProblem(StatusCodes.Status409Conflict);

		_ = api.MapPost("/requests/{jobNodeId:long}/comments", AddRequestNoteAsync)
			.RequireAuthorization(JobTrackPolicyNames.RequestDetailAccess)
			.AddEndpointFilter<AntiforgeryValidationFilter>()
			.WithName("AddRequestNote")
			.WithSummary("Add a requester-visible note or clarification, posted by staff or by the request's own requester (ADR 0034).")
			.Produces<RequestNoteResponse>(StatusCodes.Status201Created)
			.ProducesProblem(StatusCodes.Status400BadRequest)
			.ProducesProblem(StatusCodes.Status401Unauthorized)
			.ProducesProblem(StatusCodes.Status403Forbidden)
			.ProducesProblem(StatusCodes.Status404NotFound)
			.ProducesProblem(StatusCodes.Status409Conflict)
			.ProducesProblem(StatusCodes.Status413PayloadTooLarge);

		_ = api.MapPost("/requests/{jobNodeId:long}/acknowledge", AcknowledgeRequestAsync)
			.RequireAuthorization(JobTrackPolicyNames.JobWorkflow)
			.AddEndpointFilter<AntiforgeryValidationFilter>()
			.WithName("AcknowledgeRequest")
			.WithSummary("Staff acknowledgement: sets the explicit Accepted signal a requester sees (ADR 0034).")
			.Produces<RequestResponse>()
			.ProducesProblem(StatusCodes.Status401Unauthorized)
			.ProducesProblem(StatusCodes.Status403Forbidden)
			.ProducesProblem(StatusCodes.Status404NotFound)
			.ProducesProblem(StatusCodes.Status409Conflict);
	}

	public static Task HandleRedirectAsync(
		RedirectContext<CookieAuthenticationOptions> context,
		int statusCode,
		string title,
		string type)
	{
		if (!IsApiRequest(context.Request)) {
			context.Response.Redirect(context.RedirectUri);
			return Task.CompletedTask;
		}

		context.Response.StatusCode = statusCode;
		context.Response.ContentType = "application/problem+json";
		var problem = new ProblemDetails {
			Status = statusCode,
			Title = title,
			Type = type,
			Detail = statusCode == StatusCodes.Status401Unauthorized
				? "Authenticate and retry."
				: "You do not have permission to perform this action.",
		};

		return context.Response.WriteAsJsonAsync(problem, options: null, contentType: "application/problem+json");
	}

	private static bool IsApiRequest(HttpRequest request) =>
		request.Path.StartsWithSegments(ApiPathPrefix, StringComparison.OrdinalIgnoreCase);

	private static Ok<AntiforgeryTokenResponse> GetAntiforgeryToken(HttpContext httpContext, IAntiforgery antiforgery)
	{
		var tokens = antiforgery.GetAndStoreTokens(httpContext);
		return TypedResults.Ok(new AntiforgeryTokenResponse { Token = tokens.RequestToken! });
	}

	private static async Task<IResult> GetRatesAsync(
		long userId,
		HttpContext httpContext,
		UserManager<JobTrackIdentityUser> userManager,
		IJobTrackClient jobTrackClient,
		CancellationToken cancellationToken)
	{
		return await ExecuteAsync(httpContext, userManager, async context => {
			var result = await jobTrackClient.Query.GetRatesAsync(new() { Context = context, UserId = new(userId) }, cancellationToken);

			return TypedResults.Ok(Map(result));
		});
	}

	private static async Task<IResult> AddUserCostRateAsync(
		long userId,
		[FromBody] AddUserCostRateBody request,
		HttpContext httpContext,
		UserManager<JobTrackIdentityUser> userManager,
		IJobTrackClient jobTrackClient,
		CancellationToken cancellationToken)
	{
		return await ExecuteAsync(httpContext, userManager, async context => {
			var result = await jobTrackClient.Rates.AddUserCostRateAsync(new() {
				Context = context,
				UserId = new(userId),
				Rate = new(
					new(request.AmountPerHour),
					Instant.FromDateTimeOffset(request.EffectiveStart),
					request.EffectiveEnd.HasValue ? Instant.FromDateTimeOffset(request.EffectiveEnd.Value) : null),
			}, cancellationToken);

			return TypedResults.Created($"/api/employees/{userId}/rates", Map(result));
		});
	}

	private static async Task<IResult> CorrectUserCostRateAsync(
		long userId,
		long rateId,
		[FromBody] CorrectUserCostRateBody request,
		HttpContext httpContext,
		UserManager<JobTrackIdentityUser> userManager,
		IJobTrackClient jobTrackClient,
		CancellationToken cancellationToken)
	{
		return await ExecuteAsync(httpContext, userManager, async context => {
			var result = await jobTrackClient.Rates.CorrectUserCostRateAsync(new() {
				Context = context,
				RateId = new(rateId),
				UserId = new AppUserId(userId),
				Version = request.Version,
				Reason = request.Reason,
				Rate = new(
					new(request.AmountPerHour),
					Instant.FromDateTimeOffset(request.EffectiveStart),
					request.EffectiveEnd.HasValue ? Instant.FromDateTimeOffset(request.EffectiveEnd.Value) : null),
			}, cancellationToken);

			return TypedResults.Ok(Map(result));
		});
	}

	private static async Task<IResult> CorrectNodeRateOverrideAsync(
		long userId,
		long overrideId,
		[FromBody] CorrectNodeRateOverrideBody request,
		HttpContext httpContext,
		UserManager<JobTrackIdentityUser> userManager,
		IJobTrackClient jobTrackClient,
		CancellationToken cancellationToken)
	{
		return await ExecuteAsync(httpContext, userManager, async context => {
			var result = await jobTrackClient.Rates.CorrectNodeRateOverrideAsync(new() {
				Context = context,
				OverrideId = new(overrideId),
				UserId = new AppUserId(userId),
				Version = request.Version,
				Reason = request.Reason,
				Override = new(
					new(request.NodeId),
					new(request.AmountPerHour),
					Instant.FromDateTimeOffset(request.EffectiveStart),
					request.EffectiveEnd.HasValue ? Instant.FromDateTimeOffset(request.EffectiveEnd.Value) : null),
			}, cancellationToken);

			return TypedResults.Ok(Map(result));
		});
	}

	private static async Task<IResult> AddNodeRateOverrideAsync(
		long userId,
		[FromBody] AddNodeRateOverrideBody request,
		HttpContext httpContext,
		UserManager<JobTrackIdentityUser> userManager,
		IJobTrackClient jobTrackClient,
		CancellationToken cancellationToken)
	{
		return await ExecuteAsync(httpContext, userManager, async context => {
			var result = await jobTrackClient.Rates.AddNodeRateOverrideAsync(new() {
				Context = context,
				UserId = new(userId),
				Override = new(
					new(request.NodeId),
					new(request.AmountPerHour),
					Instant.FromDateTimeOffset(request.EffectiveStart),
					request.EffectiveEnd.HasValue ? Instant.FromDateTimeOffset(request.EffectiveEnd.Value) : null),
			}, cancellationToken);

			return TypedResults.Created($"/api/employees/{userId}/rates", Map(result));
		});
	}

	private static async Task<IResult> GetRootJobNodeAsync(
		HttpContext httpContext,
		UserManager<JobTrackIdentityUser> userManager,
		IJobTrackClient jobTrackClient,
		CancellationToken cancellationToken)
	{
		return await ExecuteAsync(httpContext, userManager, async context => {
			var result = await jobTrackClient.Query.GetJobNodeAsync(new() { Context = context, NodeId = null }, cancellationToken);

			return TypedResults.Ok(Map(result));
		});
	}

	private static async Task<IResult> GetJobNodeAsync(
		long nodeId,
		HttpContext httpContext,
		UserManager<JobTrackIdentityUser> userManager,
		IJobTrackClient jobTrackClient,
		CancellationToken cancellationToken)
	{
		return await ExecuteAsync(httpContext, userManager, async context => {
			var result = await jobTrackClient.Query.GetJobNodeAsync(new() { Context = context, NodeId = new JobNodeId(nodeId) }, cancellationToken);

			return TypedResults.Ok(Map(result));
		});
	}

	private static async Task<IResult> GetJobChildrenAsync(
		long nodeId,
		long? ownerUserId,
		HttpContext httpContext,
		UserManager<JobTrackIdentityUser> userManager,
		IJobTrackClient jobTrackClient,
		CancellationToken cancellationToken,
		JobArchiveFilter archiveFilter = JobArchiveFilter.ActiveOnly,
		bool unassignedOnly = false,
		int offset = 0,
		int? pageSize = null)
	{
		return await ExecuteAsync(httpContext, userManager, async context => {
			var resolvedPageSize = ResolvePageSize(pageSize);
			var ownership = ResolveOwnership(ownerUserId, unassignedOnly);

			// Fresh-eyes review §2.8: cost enrichment happens inside GetJobChildrenAsync, so the page
			// itself is fetched at exactly pageSize -- never the pageSize + 1 probe row -- and "is there
			// another page" is answered by a second, unenriched-scale (Limit = 1) call, skipped entirely
			// when this page didn't even fill up.
			var page = await jobTrackClient.Query.GetJobChildrenAsync(new() {
				Context = context,
				ParentId = new(nodeId),
				Ownership = ownership,
				ArchiveFilter = archiveFilter,
				Offset = offset,
				Limit = resolvedPageSize,
			}, cancellationToken);

			var hasMore = false;
			if (page.Count == resolvedPageSize) {
				var probe = await jobTrackClient.Query.GetJobChildrenAsync(new() {
					Context = context,
					ParentId = new(nodeId),
					Ownership = ownership,
					ArchiveFilter = archiveFilter,
					Offset = offset + resolvedPageSize,
					Limit = 1,
				}, cancellationToken);
				hasMore = probe.Count > 0;
			}

			return TypedResults.Ok(new PagedResponse<JobNodeSummaryResponse> {
				Items = [.. page.Select(Map)],
				Offset = offset,
				PageSize = resolvedPageSize,
				HasMore = hasMore,
				OrderedBy = "id ascending",
			});
		});
	}

	private static async Task<IResult> SearchJobNodesAsync(
		[Required] string searchText,
		long? ownerUserId,
		HttpContext httpContext,
		UserManager<JobTrackIdentityUser> userManager,
		IJobTrackClient jobTrackClient,
		CancellationToken cancellationToken,
		JobArchiveFilter archiveFilter = JobArchiveFilter.ActiveOnly,
		bool unassignedOnly = false,
		int offset = 0,
		int? pageSize = null)
	{
		return await ExecuteAsync(httpContext, userManager, async context => {
			var resolvedPageSize = ResolvePageSize(pageSize);
			var ownership = ResolveOwnership(ownerUserId, unassignedOnly);

			// Fresh-eyes review §2.8: same shape as GetJobChildrenAsync -- fetch exactly pageSize
			// enriched rows, only probe for another page (Limit = 1) when this page filled up.
			var page = await jobTrackClient.Query.SearchJobNodesAsync(new() {
				Context = context,
				SearchText = searchText,
				Ownership = ownership,
				ArchiveFilter = archiveFilter,
				Offset = offset,
				Limit = resolvedPageSize,
			}, cancellationToken);

			var hasMore = false;
			if (page.Count == resolvedPageSize) {
				var probe = await jobTrackClient.Query.SearchJobNodesAsync(new() {
					Context = context,
					SearchText = searchText,
					Ownership = ownership,
					ArchiveFilter = archiveFilter,
					Offset = offset + resolvedPageSize,
					Limit = 1,
				}, cancellationToken);
				hasMore = probe.Count > 0;
			}

			return TypedResults.Ok(new PagedResponse<JobNodeSummaryResponse> {
				Items = [.. page.Select(Map)],
				Offset = offset,
				PageSize = resolvedPageSize,
				HasMore = hasMore,
				OrderedBy = "id ascending",
			});
		});
	}

	/// <summary>
	///     <paramref name="unassignedOnly" /> and <paramref name="ownerUserId" /> are mutually exclusive
	///     filter shapes <see cref="OwnershipFilter" /> exists to keep distinct (ownership model §2.1) --
	///     a plain nullable owner id can't express both "no filter" and "only unassigned".
	///     <paramref name="unassignedOnly" /> wins if both are supplied.
	/// </summary>
	private static OwnershipFilter ResolveOwnership(long? ownerUserId, bool unassignedOnly) =>
		(unassignedOnly, ownerUserId) switch {
			(true, _) => OwnershipFilter.Unassigned,
			(false, long id) => OwnershipFilter.OwnedBy(new(id)),
			(false, null) => OwnershipFilter.All,
		};

	private static async Task<IResult> PickUpJobNodeAsync(
		long nodeId,
		HttpContext httpContext,
		UserManager<JobTrackIdentityUser> userManager,
		IJobTrackClient jobTrackClient,
		CancellationToken cancellationToken)
	{
		return await ExecuteAsync(httpContext, userManager, async context => {
			var result = await jobTrackClient.Jobs.PickUpAsync(new() { Context = context, NodeId = new(nodeId) }, cancellationToken);

			return TypedResults.Ok(Map(result));
		});
	}

	private static async Task<IResult> GetReadinessAsync(
		long nodeId,
		HttpContext httpContext,
		UserManager<JobTrackIdentityUser> userManager,
		IJobTrackClient jobTrackClient,
		CancellationToken cancellationToken)
	{
		return await ExecuteAsync(httpContext, userManager, async context => {
			var result = await jobTrackClient.Query.GetReadinessAsync(new() { Context = context, NodeId = new(nodeId) }, cancellationToken);

			return TypedResults.Ok(Map(result));
		});
	}

	private static async Task<IResult> GetLeafSessionsAsync(
		long nodeId,
		long workedByUserId,
		HttpContext httpContext,
		UserManager<JobTrackIdentityUser> userManager,
		IJobTrackClient jobTrackClient,
		CancellationToken cancellationToken,
		int offset = 0,
		int? pageSize = null)
	{
		return await ExecuteAsync(httpContext, userManager, async context => {
			var resolvedPageSize = ResolvePageSize(pageSize);
			var result = await jobTrackClient.Query.GetLeafSessionsAsync(new() {
				Context = context,
				LeafWorkId = new(nodeId),
				WorkedByUserId = new AppUserId(workedByUserId),
				Offset = offset,
				Limit = resolvedPageSize + 1,
			}, cancellationToken);

			return TypedResults.Ok(ToPagedResponse(result, offset, resolvedPageSize, "startedAt descending, id descending", Map));
		});
	}

	private static async Task<IResult> StartSessionAsync(
		long nodeId,
		[FromBody] StartSessionBody request,
		HttpContext httpContext,
		UserManager<JobTrackIdentityUser> userManager,
		IJobTrackClient jobTrackClient,
		CancellationToken cancellationToken)
	{
		return await ExecuteAsync(httpContext, userManager, async context => {
			var result = await jobTrackClient.Work.StartSessionAsync(new() {
				Context = context,
				LeafWorkId = new(nodeId),
				WorkedByUserId = new(request.WorkedByUserId),
				StartedAt = request.StartedAt.HasValue ? Instant.FromDateTimeOffset(request.StartedAt.Value) : null,
			}, cancellationToken);

			return TypedResults.Created($"/api/jobs/{nodeId}/sessions/{result.Id.Value}", Map(result));
		});
	}

	private static async Task<IResult> FinishSessionAsync(
		long nodeId,
		long sessionId,
		[FromBody] FinishSessionBody request,
		HttpContext httpContext,
		UserManager<JobTrackIdentityUser> userManager,
		IJobTrackClient jobTrackClient,
		CancellationToken cancellationToken)
	{
		return await ExecuteAsync(httpContext, userManager, async context => {
			var result = await jobTrackClient.Work.FinishSessionAsync(new() {
				Context = context,
				SessionId = new(sessionId),
				Version = request.Version,
				FinishedAt = request.FinishedAt.HasValue ? Instant.FromDateTimeOffset(request.FinishedAt.Value) : null,
				LeafWorkId = new JobNodeId(nodeId),
			}, cancellationToken);

			return TypedResults.Ok(Map(result));
		});
	}

	private static async Task<IResult> FinishSessionAndUpdateWriteUpAsync(
		long nodeId,
		long sessionId,
		[FromBody] FinishSessionAndUpdateWriteUpBody request,
		HttpContext httpContext,
		UserManager<JobTrackIdentityUser> userManager,
		IJobTrackClient jobTrackClient,
		CancellationToken cancellationToken)
	{
		return await ExecuteAsync(httpContext, userManager, async context => {
			var writeUpChange = request.WriteUpChange;
			var result = await jobTrackClient.Work.FinishSessionAndUpdateWriteUpAsync(new() {
				Context = context,
				SessionId = new(sessionId),
				Version = request.Version,
				FinishedAt = request.FinishedAt.HasValue ? Instant.FromDateTimeOffset(request.FinishedAt.Value) : null,
				LeafWorkId = new JobNodeId(nodeId),
				WriteUpChange = writeUpChange is not null
					? new() { NodeVersion = writeUpChange.NodeVersion, WriteUp = writeUpChange.WriteUp }
					: null,
			}, cancellationToken);

			return TypedResults.Ok(Map(result));
		});
	}

	private static async Task<IResult> CorrectSessionAsync(
		long nodeId,
		long sessionId,
		[FromBody] CorrectSessionBody request,
		HttpContext httpContext,
		UserManager<JobTrackIdentityUser> userManager,
		IJobTrackClient jobTrackClient,
		CancellationToken cancellationToken)
	{
		return await ExecuteAsync(httpContext, userManager, async context => {
			var result = await jobTrackClient.Work.CorrectSessionAsync(new() {
				Context = context,
				SessionId = new(sessionId),
				StartedAt = Instant.FromDateTimeOffset(request.StartedAt),
				FinishedAt = request.FinishedAt.HasValue ? Instant.FromDateTimeOffset(request.FinishedAt.Value) : null,
				Reason = request.Reason,
				Version = request.Version,
				LeafWorkId = new JobNodeId(nodeId),
			}, cancellationToken);

			return TypedResults.Ok(Map(result));
		});
	}

	private static async Task<IResult> GetPrerequisitesAsync(
		long nodeId,
		HttpContext httpContext,
		UserManager<JobTrackIdentityUser> userManager,
		IJobTrackClient jobTrackClient,
		CancellationToken cancellationToken,
		int offset = 0,
		int? pageSize = null)
	{
		return await ExecuteAsync(httpContext, userManager, async context => {
			var resolvedPageSize = ResolvePageSize(pageSize);
			var result = await jobTrackClient.Query.GetPrerequisitesAsync(new() {
				Context = context,
				NodeId = new(nodeId),
				Offset = offset,
				Limit = resolvedPageSize + 1,
			}, cancellationToken);

			return TypedResults.Ok(ToPagedResponse(result, offset, resolvedPageSize, "requiredJobId ascending, dependentJobId ascending", Map));
		});
	}

	private static async Task<IResult> AddPrerequisiteAsync(
		long nodeId,
		[FromBody] AddPrerequisiteBody request,
		HttpContext httpContext,
		UserManager<JobTrackIdentityUser> userManager,
		IJobTrackClient jobTrackClient,
		CancellationToken cancellationToken)
	{
		return await ExecuteAsync(httpContext, userManager, async context => {
			await jobTrackClient.Jobs.AddPrerequisiteAsync(
				new() { Context = context, RequiredJobId = new(request.RequiredJobId), DependentJobId = new(nodeId) }, cancellationToken);

			return TypedResults.NoContent();
		});
	}

	private static async Task<IResult> RemovePrerequisiteAsync(
		long nodeId,
		long requiredJobId,
		HttpContext httpContext,
		UserManager<JobTrackIdentityUser> userManager,
		IJobTrackClient jobTrackClient,
		CancellationToken cancellationToken)
	{
		return await ExecuteAsync(httpContext, userManager, async context => {
			await jobTrackClient.Jobs.RemovePrerequisiteAsync(
				new() { Context = context, RequiredJobId = new(requiredJobId), DependentJobId = new(nodeId) }, cancellationToken);

			return TypedResults.NoContent();
		});
	}

	private static async Task<IResult> GetLeafWorkAsync(
		long nodeId,
		HttpContext httpContext,
		UserManager<JobTrackIdentityUser> userManager,
		IJobTrackClient jobTrackClient,
		CancellationToken cancellationToken)
	{
		return await ExecuteAsync(httpContext, userManager, async context => {
			var result = await jobTrackClient.Query.GetLeafWorkAsync(new() { Context = context, JobNodeId = new(nodeId) }, cancellationToken);

			return TypedResults.Ok(Map(result));
		});
	}

	private static async Task<IResult> SetAchievementAsync(
		long nodeId,
		[FromBody] SetAchievementBody request,
		HttpContext httpContext,
		UserManager<JobTrackIdentityUser> userManager,
		IJobTrackClient jobTrackClient,
		CancellationToken cancellationToken)
	{
		return await ExecuteAsync(httpContext, userManager, async context => {
			var result = await jobTrackClient.Work.SetAchievementAsync(new() {
				Context = context,
				JobNodeId = new(nodeId),
				NewAchievement = request.NewAchievement,
				Reason = request.Reason,
				Version = request.Version,
			}, cancellationToken);

			return TypedResults.Ok(Map(result));
		});
	}

	private static async Task<IResult> CompleteLeafAsync(
		long nodeId,
		[FromBody] CompleteLeafBody request,
		HttpContext httpContext,
		UserManager<JobTrackIdentityUser> userManager,
		IJobTrackClient jobTrackClient,
		CancellationToken cancellationToken)
	{
		return await ExecuteAsync(httpContext, userManager, async context => {
			var writeUpChange = request.WriteUpChange;
			var result = await jobTrackClient.Work.CompleteLeafAsync(new() {
				Context = context,
				JobNodeId = new(nodeId),
				Version = request.Version,
				ExpectedActiveSessions = [
					.. request.ExpectedActiveSessions.Select(s => new ExpectedActiveSession { Id = new(s.Id), Version = s.Version }),
				],
				FinishedAt = request.FinishedAt.HasValue ? Instant.FromDateTimeOffset(request.FinishedAt.Value) : null,
				CompletionNote = request.CompletionNote,
				FinalAchievement = request.FinalAchievement ?? Achievement.Success,
				WriteUpChange = writeUpChange is not null
					? new() { NodeVersion = writeUpChange.NodeVersion, WriteUp = writeUpChange.WriteUp }
					: null,
			}, cancellationToken);

			return TypedResults.Ok(Map(result));
		});
	}

	private static async Task<IResult> ReopenAndStartWorkAsync(
		long nodeId,
		[FromBody] ReopenAndStartWorkBody request,
		HttpContext httpContext,
		UserManager<JobTrackIdentityUser> userManager,
		IJobTrackClient jobTrackClient,
		CancellationToken cancellationToken)
	{
		return await ExecuteAsync(httpContext, userManager, async context => {
			var result = await jobTrackClient.Work.ReopenAndStartWorkAsync(new() {
				Context = context,
				JobNodeId = new(nodeId),
				Version = request.Version,
				Reason = request.Reason,
				WorkedByUserId = new(request.WorkedByUserId),
				StartedAt = request.StartedAt.HasValue ? Instant.FromDateTimeOffset(request.StartedAt.Value) : null,
			}, cancellationToken);

			return TypedResults.Created($"/api/jobs/{nodeId}/sessions/{result.Session.Id.Value}", Map(result));
		});
	}

	private static async Task<IResult> GetCostDetailsAsync(
		long nodeId,
		DateTimeOffset? asOf,
		int? maxTraceSegments,
		HttpContext httpContext,
		UserManager<JobTrackIdentityUser> userManager,
		IJobTrackClient jobTrackClient,
		IClock clock,
		CancellationToken cancellationToken)
	{
		return await ExecuteAsync(httpContext, userManager, async context => {
			var result = await jobTrackClient.Costs.GetCostDetailsAsync(new() {
				Context = context,
				NodeId = new(nodeId),
				AsOf = asOf.HasValue ? Instant.FromDateTimeOffset(asOf.Value) : clock.GetCurrentInstant(),
				MaxTraceSegments = maxTraceSegments,
			}, cancellationToken);

			return TypedResults.Ok(Map(result));
		});
	}

	private static async Task<IResult> GetHierarchyTotalsAsync(
		long nodeId,
		DateTimeOffset? asOf,
		int? maxHierarchyNodes,
		HttpContext httpContext,
		UserManager<JobTrackIdentityUser> userManager,
		IJobTrackClient jobTrackClient,
		IClock clock,
		CancellationToken cancellationToken)
	{
		return await ExecuteAsync(httpContext, userManager, async context => {
			var result = await jobTrackClient.Costs.GetHierarchyTotalsAsync(new() {
				Context = context,
				NodeId = new(nodeId),
				AsOf = asOf.HasValue ? Instant.FromDateTimeOffset(asOf.Value) : clock.GetCurrentInstant(),
				MaxHierarchyNodes = maxHierarchyNodes,
			}, cancellationToken);

			return TypedResults.Ok(Map(result));
		});
	}

	private static async Task<IResult> GetJobSubtreeAsync(
		long nodeId,
		int? depth,
		long? ownerUserId,
		DateTimeOffset? asOf,
		HttpContext httpContext,
		UserManager<JobTrackIdentityUser> userManager,
		IJobTrackClient jobTrackClient,
		IClock clock,
		CancellationToken cancellationToken,
		JobArchiveFilter archiveFilter = JobArchiveFilter.ActiveOnly,
		bool unassignedOnly = false)
	{
		return await ExecuteAsync(httpContext, userManager, async context => {
			var result = await jobTrackClient.Query.GetJobSubtreeAsync(new() {
				Context = context,
				RootId = new(nodeId),
				MaxDepth = depth,
				Ownership = ResolveOwnership(ownerUserId, unassignedOnly),
				ArchiveFilter = archiveFilter,
				AsOf = asOf.HasValue ? Instant.FromDateTimeOffset(asOf.Value) : clock.GetCurrentInstant(),
			}, cancellationToken);

			return TypedResults.Ok(Map(result));
		});
	}

	private static async Task<IResult> GetScheduleAsync(
		long userId,
		HttpContext httpContext,
		UserManager<JobTrackIdentityUser> userManager,
		IJobTrackClient jobTrackClient,
		CancellationToken cancellationToken)
	{
		return await ExecuteAsync(httpContext, userManager, async context => {
			var result = await jobTrackClient.Query.GetScheduleAsync(new() { Context = context, UserId = new(userId) }, cancellationToken);

			return TypedResults.Ok(Map(result));
		});
	}

	private static async Task<IResult> AddScheduleVersionAsync(
		long userId,
		[FromBody] AddScheduleVersionBody request,
		HttpContext httpContext,
		UserManager<JobTrackIdentityUser> userManager,
		IJobTrackClient jobTrackClient,
		CancellationToken cancellationToken)
	{
		return await ExecuteAsync(httpContext, userManager, async context => {
			var zone = ScheduleZoneId.Resolve(request.IanaTimeZone);
			var weeklyIntervals = request.WeeklyIntervals
				.Select(interval => new WeeklyInterval(
					ToIsoDayOfWeek(interval.Day),
					new(interval.Start.Hour, interval.Start.Minute, interval.Start.Second),
					new(interval.End.Hour, interval.End.Minute, interval.End.Second)))
				.ToArray();

			var result = await jobTrackClient.Schedules.AddScheduleVersionAsync(new() {
				Context = context,
				UserId = new(userId),
				Schedule = new(
					zone,
					new(request.EffectiveStart.Year, request.EffectiveStart.Month, request.EffectiveStart.Day),
					request.EffectiveEnd.HasValue
						? new LocalDate(request.EffectiveEnd.Value.Year, request.EffectiveEnd.Value.Month, request.EffectiveEnd.Value.Day)
						: null,
					[.. weeklyIntervals]),
			}, cancellationToken);

			return TypedResults.Created($"/api/employees/{userId}/schedule", Map(result));
		});
	}

	private static async Task<IResult> CorrectScheduleVersionAsync(
		long userId,
		long versionId,
		[FromBody] CorrectScheduleVersionBody request,
		HttpContext httpContext,
		UserManager<JobTrackIdentityUser> userManager,
		IJobTrackClient jobTrackClient,
		CancellationToken cancellationToken)
	{
		return await ExecuteAsync(httpContext, userManager, async context => {
			var zone = ScheduleZoneId.Resolve(request.IanaTimeZone);
			var weeklyIntervals = request.WeeklyIntervals
				.Select(interval => new WeeklyInterval(
					ToIsoDayOfWeek(interval.Day),
					new(interval.Start.Hour, interval.Start.Minute, interval.Start.Second),
					new(interval.End.Hour, interval.End.Minute, interval.End.Second)))
				.ToArray();

			var result = await jobTrackClient.Schedules.CorrectScheduleVersionAsync(new() {
				Context = context,
				VersionId = new(versionId),
				UserId = new AppUserId(userId),
				Version = request.Version,
				Reason = request.Reason,
				Schedule = new(
					zone,
					new(request.EffectiveStart.Year, request.EffectiveStart.Month, request.EffectiveStart.Day),
					request.EffectiveEnd.HasValue
						? new LocalDate(request.EffectiveEnd.Value.Year, request.EffectiveEnd.Value.Month, request.EffectiveEnd.Value.Day)
						: null,
					[.. weeklyIntervals]),
			}, cancellationToken);

			return TypedResults.Ok(Map(result));
		});
	}

	private static async Task<IResult> CorrectScheduleExceptionAsync(
		long userId,
		long exceptionId,
		[FromBody] CorrectScheduleExceptionBody request,
		HttpContext httpContext,
		UserManager<JobTrackIdentityUser> userManager,
		IJobTrackClient jobTrackClient,
		CancellationToken cancellationToken)
	{
		return await ExecuteAsync(httpContext, userManager, async context => {
			var result = await jobTrackClient.Schedules.CorrectScheduleExceptionAsync(new() {
				Context = context,
				ExceptionId = new(exceptionId),
				UserId = new AppUserId(userId),
				Version = request.Version,
				Reason = request.Reason,
				Entry = new(
					request.Effect,
					new(
						Instant.FromDateTimeOffset(request.Start),
						Instant.FromDateTimeOffset(request.End)),
					request.RateOverrideAmountPerHour.HasValue ? new HourlyRate(request.RateOverrideAmountPerHour.Value) : null),
			}, cancellationToken);

			return TypedResults.Ok(Map(result));
		});
	}

	private static async Task<IResult> AddScheduleExceptionAsync(
		long userId,
		[FromBody] AddScheduleExceptionBody request,
		HttpContext httpContext,
		UserManager<JobTrackIdentityUser> userManager,
		IJobTrackClient jobTrackClient,
		CancellationToken cancellationToken)
	{
		return await ExecuteAsync(httpContext, userManager, async context => {
			var result = await jobTrackClient.Schedules.AddScheduleExceptionAsync(new() {
				Context = context,
				UserId = new(userId),
				Entry = new(
					request.Effect,
					new(
						Instant.FromDateTimeOffset(request.Start),
						Instant.FromDateTimeOffset(request.End)),
					request.RateOverrideAmountPerHour.HasValue ? new HourlyRate(request.RateOverrideAmountPerHour.Value) : null),
				Reason = request.Reason,
			}, cancellationToken);

			return TypedResults.Created($"/api/employees/{userId}/schedule", Map(result));
		});
	}

	private static async Task<IResult> GetEligibleHoldingAreasAsync(
		HttpContext httpContext,
		UserManager<JobTrackIdentityUser> userManager,
		IJobTrackClient jobTrackClient,
		CancellationToken cancellationToken)
	{
		return await ExecuteAsync(httpContext, userManager, async context => {
			var result = await jobTrackClient.Requests.GetEligibleHoldingAreasAsync(context, cancellationToken);

			return TypedResults.Ok(result.Select(Map).ToArray());
		});
	}

	private static async Task<IResult> SubmitRequestAsync(
		[FromBody] SubmitRequestBody? request,
		HttpContext httpContext,
		UserManager<JobTrackIdentityUser> userManager,
		IJobTrackClient jobTrackClient,
		CancellationToken cancellationToken)
	{
		var validationProblem = ValidateSubmitRequestBody(request);
		if (validationProblem is not null) {
			return validationProblem;
		}

		var body = request ?? throw new ArgumentNullException(nameof(request));
		return await ExecuteAsync(httpContext, userManager, async context => {
			var result = await jobTrackClient.Requests.SubmitAsync(
				new() { Context = context, HoldingAreaId = new(body.HoldingAreaId), Description = body.Description }, cancellationToken);

			return TypedResults.Created("/api/requests", Map(result));
		});
	}

	private static async Task<IResult> GetMyRequestsAsync(
		HttpContext httpContext,
		UserManager<JobTrackIdentityUser> userManager,
		IJobTrackClient jobTrackClient,
		CancellationToken cancellationToken)
	{
		return await ExecuteAsync(httpContext, userManager, async context => {
			var result = await jobTrackClient.Requests.GetMyRequestsAsync(context, cancellationToken);

			return TypedResults.Ok(result.Select(Map).ToArray());
		});
	}

	private static async Task<IResult> GetRequestDetailAsync(
		long jobNodeId,
		HttpContext httpContext,
		UserManager<JobTrackIdentityUser> userManager,
		IJobTrackClient jobTrackClient,
		CancellationToken cancellationToken)
	{
		return await ExecuteAsync(httpContext, userManager, async context => {
			var result = await jobTrackClient.Requests.GetDetailAsync(new() { Context = context, NodeId = new(jobNodeId) }, cancellationToken);

			return TypedResults.Ok(Map(result));
		});
	}

	private static async Task<IResult> AddRequestNoteAsync(
		long jobNodeId,
		[FromBody] AddRequestNoteBody? request,
		HttpContext httpContext,
		UserManager<JobTrackIdentityUser> userManager,
		IJobTrackClient jobTrackClient,
		CancellationToken cancellationToken)
	{
		var validationProblem = ValidateAddRequestNoteBody(request);
		if (validationProblem is not null) {
			return validationProblem;
		}

		var body = request ?? throw new ArgumentNullException(nameof(request));
		return await ExecuteAsync(httpContext, userManager, async context => {
			var result = await jobTrackClient.Requests.AddNoteAsync(new() {
				Context = context,
				NodeId = new(jobNodeId),
				Content = body.Content,
				VisibleToRequester = body.VisibleToRequester,
			}, cancellationToken);

			return TypedResults.Created($"/api/requests/{jobNodeId}", Map(result));
		});
	}

	private static ProblemHttpResult? ValidateSubmitRequestBody(SubmitRequestBody? request)
	{
		if (request is null || string.IsNullOrWhiteSpace(request.Description)) {
			return Problem(
				StatusCodes.Status400BadRequest,
				"Invalid request",
				"The request description is required.",
				ValidationProblemType);
		}

		return null;
	}

	private static ProblemHttpResult? ValidateAddRequestNoteBody(AddRequestNoteBody? request)
	{
		if (request is null || string.IsNullOrWhiteSpace(request.Content)) {
			return Problem(
				StatusCodes.Status400BadRequest,
				"Invalid request",
				"The note content is required.",
				ValidationProblemType);
		}

		return null;
	}

	private static async Task<IResult> AcknowledgeRequestAsync(
		long jobNodeId,
		[FromBody] AcknowledgeRequestBody request,
		HttpContext httpContext,
		UserManager<JobTrackIdentityUser> userManager,
		IJobTrackClient jobTrackClient,
		CancellationToken cancellationToken)
	{
		return await ExecuteAsync(httpContext, userManager, async context => {
			var result = await jobTrackClient.Requests.AcknowledgeAsync(
				new() { Context = context, NodeId = new(jobNodeId), Version = request.Version }, cancellationToken);

			return TypedResults.Ok(Map(result));
		});
	}

	private static async Task<IResult> ExecuteAsync(
		HttpContext httpContext,
		UserManager<JobTrackIdentityUser> userManager,
		Func<CommandContext, Task<IResult>> action)
	{
		var actor = await userManager.GetUserAsync(httpContext.User);
		if (actor is null) {
			return Problem(
				StatusCodes.Status401Unauthorized,
				"Authentication required",
				"Authenticate and retry.",
				AuthenticationProblemType);
		}

		var correlationId = httpContext.Items[ApiTelemetryFilter.CorrelationIdItemKey] as Guid? ?? Guid.NewGuid();

		try {
			return await action(new() { Actor = actor.AppUserId, CorrelationId = correlationId });
		}
		catch (AuthorizationDeniedException) {
			return Problem(
				StatusCodes.Status403Forbidden, "Forbidden", "You do not have permission to perform this action.", ForbiddenProblemType);
		}
		catch (EntityNotFoundException) {
			return Problem(StatusCodes.Status404NotFound, "Not found", "The requested resource does not exist.", NotFoundProblemType);
		}
		catch (ConcurrencyConflictException) {
			return Problem(
				StatusCodes.Status409Conflict,
				"Concurrency conflict",
				"The resource has changed since it was last read. Reload and retry.",
				ConcurrencyProblemType);
		}
		catch (InvariantViolationException) {
			return Problem(
				StatusCodes.Status409Conflict,
				"Invariant violation",
				"The request conflicts with an existing record or violates a data constraint.",
				InvariantProblemType);
		}
		catch (PrerequisiteBlockedException) {
			return Problem(
				StatusCodes.Status409Conflict,
				"Prerequisite blocked",
				"This action is blocked until its prerequisites are satisfied.",
				BlockedProblemType);
		}
		catch (MissingRateException) {
			return Problem(
				StatusCodes.Status422UnprocessableEntity,
				"No rate resolves",
				"No rate resolves for one or more contributing sessions, so cost cannot be calculated.",
				MissingRateProblemType);
		}
		catch (ArgumentOutOfRangeException) {
			return Problem(
				StatusCodes.Status400BadRequest, "Invalid request", "The request contains an invalid value.", ValidationProblemType);
		}
		catch (UnknownStoredTimeZoneException ex) {
			LogStoredTimeZoneRot(
				httpContext.RequestServices.GetRequiredService<ILogger<ApiTelemetryFilter>>(), correlationId, ex.Message);
			return Problem(
				StatusCodes.Status500InternalServerError,
				"Stored time zone not recognized",
				"A stored record references a time zone the server no longer recognizes. This is a server-side data issue, not a problem with your request.",
				StoredTimeZoneRotProblemType);
		}
		catch (DateTimeZoneNotFoundException) {
			return Problem(
				StatusCodes.Status400BadRequest, "Invalid request", "The specified time zone is not recognized.", ValidationProblemType);
		}
		catch (ArgumentException) {
			// This maps every ArgumentException the library raises to a client 400 -- a deliberate,
			// conscious trade-off. The library uses ArgumentException/ArgumentOutOfRangeException as its
			// documented channel for client-input contract violations that survive model binding: a blank
			// WorkSession Reason, an empty prerequisite edge set, a missing token lifetime, an out-of-range
			// trace/node cap. All of those are genuinely the caller's bad value, so 400 is correct. The
			// residual risk -- a server-side mapping bug that constructs an internally-invalid library
			// request -- would also surface here as a 400 rather than a 500 we'd alert on. That path is
			// narrow: endpoints always pass a non-null library request (so ArgumentNullException from the
			// request guard never originates server-side), and malformed/absent bodies are already rejected
			// by System.Text.Json binding before the handler runs. Kept as 400 rather than split by subtype,
			// because no argument exception reaching this point is known to be server-originated.
			return Problem(StatusCodes.Status400BadRequest, "Invalid request", "The request is invalid.", ValidationProblemType);
		}
	}

	[LoggerMessage(
		Level = LogLevel.Error,
		Message = "stored_time_zone_rot correlation_id={CorrelationId} detail={Detail}")]
	private static partial void LogStoredTimeZoneRot(ILogger logger, Guid correlationId, string detail);

	private static ProblemHttpResult Problem(int statusCode, string title, string detail, string type) =>
		TypedResults.Problem(statusCode: statusCode, title: title, detail: detail, type: type);

	/// <summary>
	///     Clamps an explicit <c>pageSize</c> query parameter down to <see cref="MaxPageSize" />
	///     (remediation plan §3.1's "clamping of excessive limits"), or applies <see cref="DefaultPageSize" />
	///     when the caller omits it. A non-positive explicit value is a caller usage error -- rejected,
	///     not silently coerced -- so it flows through to the library's own <c>Limit</c> validation and
	///     surfaces as <c>400</c>.
	/// </summary>
	private static int ResolvePageSize(int? pageSize) => pageSize.HasValue ? Math.Min(pageSize.Value, MaxPageSize) : DefaultPageSize;

	/// <summary>
	///     Builds the paged response envelope (remediation plan §3.1): the library call always requests
	///     one more item than <paramref name="pageSize" /> so <paramref name="results" />'s length reveals
	///     whether another page exists, without a separate count query.
	/// </summary>
	private static PagedResponse<TResponse> ToPagedResponse<TResult, TResponse>(
		IReadOnlyCollection<TResult> results, int offset, int pageSize, string orderedBy, Func<TResult, TResponse> map)
	{
		return new() {
			Items = [.. results.Take(pageSize).Select(map)],
			Offset = offset,
			PageSize = pageSize,
			HasMore = results.Count > pageSize,
			OrderedBy = orderedBy,
		};
	}

	private static JobNodeDetailResponse Map(JobNodeDetailResult result) =>
		new() { Node = Map(result.Node), Ancestors = [.. result.Ancestors.Select(Map)] };

	private static JobNodeResponse Map(JobNodeResult result) =>
		new() {
			Id = result.Id.Value,
			ParentId = result.ParentId?.Value,
			Kind = result.Kind,
			HasChildren = result.HasChildren,
			HasLeafWork = result.HasLeafWork,
			Description = result.Description,
			WriteUp = result.WriteUp,
			PostedByUserId = result.PostedByUserId.Value,
			OwnerUserId = result.OwnerUserId?.Value,
			ExpectedDurationHours = result.ExpectedDurationHours,
			ExpectedCost = result.ExpectedCost?.Amount,
			NeededStart = result.NeededStart?.ToDateTimeOffset(),
			NeededFinish = result.NeededFinish?.ToDateTimeOffset(),
			Priority = result.Priority,
			PostedAt = result.PostedAt.ToDateTimeOffset(),
			ArchivedAt = result.ArchivedAt?.ToDateTimeOffset(),
			Version = result.Version,
		};

	private static JobNodeAncestorResponse Map(JobNodeAncestorResult result) =>
		new() { Id = result.Id.Value, Description = result.Description, Kind = result.Kind };

	private static JobNodeSummaryResponse Map(JobNodeSummaryResult result) =>
		new() {
			Id = result.Id.Value,
			ParentId = result.ParentId?.Value,
			Kind = result.Kind,
			Description = result.Description,
			OwnerUserId = result.OwnerUserId?.Value,
			Priority = result.Priority,
			ArchivedAt = result.ArchivedAt?.ToDateTimeOffset(),
			HasChildren = result.HasChildren,
			HasLeafWork = result.HasLeafWork,
		};

	private static ReadinessResponse Map(ReadinessResult result) =>
		new() { IsReady = result.IsReady, Blockers = [.. result.Blockers.Select(Map)] };

	private static UnsatisfiedPrerequisiteResponse Map(UnsatisfiedPrerequisite result) =>
		new() { RequiredJobId = result.RequiredJobId.Value, DeclaredOnJobId = result.DeclaredOnJobId.Value };

	private static CostDetailsResponse Map(CostDetailsResult result) =>
		new() {
			NodeId = result.NodeId.Value,
			ExactCost = result.ExactCost.Amount,
			DisplayedCost = result.DisplayedCost.Amount,
			Trace = [.. result.Trace.Select(Map)],
			TzdbVersion = result.TzdbVersion,
		};

	private static CostSegmentTraceResponse Map(CostSegmentTrace trace) =>
		new() {
			SegmentStart = trace.Segment.Start.ToDateTimeOffset(),
			SegmentEnd = trace.Segment.End.ToDateTimeOffset(),
			IsWorkingTime = trace.IsWorkingTime,
			ActiveSessionIds = [.. trace.ActiveSessionIds.Select(id => id.Value)],
			SessionId = trace.SessionId.Value,
			NodeId = trace.NodeId.Value,
			SegmentTicks = trace.AllocatedDuration.SegmentTicks,
			ConcurrencyDivisor = trace.AllocatedDuration.ConcurrencyDivisor,
			AmountPerHour = trace.ResolvedRate.Rate.AmountPerHour,
			RateSource = trace.ResolvedRate.Source,
			UnroundedContribution = trace.UnroundedContribution.Amount,
		};

	private static HierarchyTotalsResponse Map(HierarchyTotalsResult result) =>
		new() {
			NodeId = result.NodeId.Value,
			Nodes = [
				.. result.ExactCosts.Select(entry => new HierarchyNodeCostResponse {
					NodeId = entry.Key.Value, ExactCost = entry.Value.Amount, DisplayedCost = result.DisplayedCosts[entry.Key].Amount,
				}),
			],
			TzdbVersion = result.TzdbVersion,
		};

	private static JobSubtreeResponse Map(JobSubtreeResult result) =>
		new() {
			RootId = result.RootId.Value,
			RootTotal = result.RootTotal?.Amount,
			TzdbVersion = result.TzdbVersion,
			Nodes = [.. result.Nodes.Select(Map)],
		};

	private static JobSubtreeNodeResponse Map(JobSubtreeNodeResult result) =>
		new() {
			Id = result.Id.Value,
			ParentId = result.ParentId?.Value,
			Kind = result.Kind,
			Depth = result.Depth,
			Description = result.Description,
			OwnerUserId = result.OwnerUserId?.Value,
			Priority = result.Priority,
			ArchivedAt = result.ArchivedAt?.ToDateTimeOffset(),
			HasChildren = result.HasChildren,
			HasLeafWork = result.HasLeafWork,
			IsReady = result.IsReady,
			HasUnexpandedChildren = result.HasUnexpandedChildren,
			MatchesFilter = result.MatchesFilter,
			SubtreeLft = result.SubtreeLft,
			SubtreeRgt = result.SubtreeRgt,
			Cost = result.Cost?.Amount,
		};

	private static PrerequisiteEdgeResponse Map(PrerequisiteEdge result) =>
		new() { RequiredJobId = result.RequiredJobId.Value, DependentJobId = result.DependentJobId.Value };

	private static LeafWorkResponse Map(LeafWorkResult result) =>
		new() {
			JobNodeId = result.JobNodeId.Value,
			Achievement = result.Achievement,
			PartialCriteria = result.PartialCriteria,
			FullCriteria = result.FullCriteria,
			ChangedAt = result.ChangedAt.ToDateTimeOffset(),
			Version = result.Version,
		};

	private static WorkSessionResponse Map(WorkSessionResult result) =>
		new() {
			Id = result.Id.Value,
			LeafWorkId = result.LeafWorkId.Value,
			WorkedByUserId = result.WorkedByUserId.Value,
			StartedAt = result.StartedAt.ToDateTimeOffset(),
			FinishedAt = result.FinishedAt?.ToDateTimeOffset(),
			ChangedAt = result.ChangedAt.ToDateTimeOffset(),
			Version = result.Version,
		};

	private static CompleteLeafResponse Map(CompleteLeafResult result) =>
		new() {
			JobNodeId = result.JobNodeId.Value,
			Achievement = result.Achievement,
			ChangedAt = result.ChangedAt.ToDateTimeOffset(),
			Version = result.Version,
			FinishedSessions = [.. result.FinishedSessions.Select(Map)],
			WriteUpChanged = result.WriteUpChanged,
			Node = result.Node is not null ? Map(result.Node) : null,
		};

	private static FinishSessionAndUpdateWriteUpResponse Map(FinishSessionAndUpdateWriteUpResult result) =>
		new() {
			Session = Map(result.Session),
			WriteUpChanged = result.WriteUpChanged,
			Node = result.Node is not null ? Map(result.Node) : null,
		};

	private static ReopenAndStartWorkResponse Map(ReopenAndStartWorkResult result) =>
		new() {
			JobNodeId = result.JobNodeId.Value,
			Achievement = result.Achievement,
			ChangedAt = result.ChangedAt.ToDateTimeOffset(),
			Version = result.Version,
			Session = Map(result.Session),
		};

	private static RatesResponse Map(RateSnapshotResult result) =>
		new() { UserCostRates = [.. result.UserCostRates.Select(Map)], NodeRateOverrides = [.. result.NodeRateOverrides.Select(Map)] };

	private static ScheduleResponse Map(ScheduleSnapshotResult result) =>
		new() { Versions = [.. result.Versions.Select(Map)], Exceptions = [.. result.Exceptions.Select(Map)] };

	private static HoldingAreaResponse Map(HoldingAreaSummaryResult result) =>
		new() { Id = result.Id.Value, Name = result.Name };

	private static RequestResponse Map(JobRequestResult result) =>
		new() {
			JobNodeId = result.JobNodeId.Value,
			Description = result.Description,
			SubmittedAt = result.SubmittedAt.ToDateTimeOffset(),
			AcknowledgedAt = result.AcknowledgedAt?.ToDateTimeOffset(),
			Version = result.Version,
		};

	private static RequestResponse Map(JobRequestSummaryResult result) =>
		new() {
			JobNodeId = result.JobNodeId.Value,
			Description = result.Description,
			SubmittedAt = result.SubmittedAt.ToDateTimeOffset(),
			AcknowledgedAt = null,
			Version = result.Version,
		};

	private static RequestDetailResponse Map(JobRequestDetailResult result) =>
		new() {
			JobNodeId = result.JobNodeId.Value,
			Description = result.Description,
			Status = result.Status,
			SubmittedAt = result.SubmittedAt.ToDateTimeOffset(),
			AcknowledgedAt = result.AcknowledgedAt?.ToDateTimeOffset(),
			Version = result.Version,
			Subtree = [.. result.Subtree.Select(Map)],
			Notes = [.. result.Notes.Select(Map)],
		};

	private static RequesterSubtreeNodeResponse Map(RequesterSubtreeNodeResult result) =>
		new() {
			JobNodeId = result.JobNodeId.Value,
			Description = result.Description,
			Status = result.Status,
			ParentId = result.ParentId?.Value,
			LastUpdatedAt = result.LastUpdatedAt.ToDateTimeOffset(),
		};

	private static RequestNoteResponse Map(JobRequestNoteResult result) =>
		new() {
			Id = result.Id.Value,
			AuthorUserId = result.AuthorUserId.Value,
			Content = result.Content,
			VisibleToRequester = result.VisibleToRequester,
			CreatedAt = result.CreatedAt.ToDateTimeOffset(),
		};

	private static UserCostRateResponse Map(UserCostRateResult result) =>
		new() {
			Id = result.Id.Value,
			UserId = result.UserId.Value,
			AmountPerHour = result.Rate.Rate.AmountPerHour,
			EffectiveStart = result.Rate.EffectiveStart.ToDateTimeOffset(),
			EffectiveEnd = result.Rate.EffectiveEnd?.ToDateTimeOffset(),
			ChangedAt = result.ChangedAt.ToDateTimeOffset(),
			Version = result.Version,
		};

	private static NodeRateOverrideResponse Map(NodeRateOverrideResult result) =>
		new() {
			Id = result.Id.Value,
			UserId = result.UserId.Value,
			NodeId = result.Override.NodeId.Value,
			AmountPerHour = result.Override.Rate.AmountPerHour,
			EffectiveStart = result.Override.EffectiveStart.ToDateTimeOffset(),
			EffectiveEnd = result.Override.EffectiveEnd?.ToDateTimeOffset(),
			ChangedAt = result.ChangedAt.ToDateTimeOffset(),
			Version = result.Version,
		};

	private static ScheduleVersionResponse Map(ScheduleVersionResult result) =>
		new() {
			Id = result.Id.Value,
			UserId = result.UserId.Value,
			IanaTimeZone = result.Schedule.Zone.Id,
			EffectiveStart = ToDateOnly(result.Schedule.EffectiveStart),
			EffectiveEnd = result.Schedule.EffectiveEnd.HasValue ? ToDateOnly(result.Schedule.EffectiveEnd.Value) : null,
			WeeklyIntervals = [
				.. result.Schedule.WeeklyIntervals.Select(interval =>
					new WeeklyIntervalResponse {
						Day = ToDayOfWeek(interval.Day), Start = ToTimeOnly(interval.Start), End = ToTimeOnly(interval.End),
					}),
			],
			ChangedAt = result.ChangedAt.ToDateTimeOffset(),
			Version = result.Version,
		};

	private static ScheduleExceptionResponse Map(ScheduleExceptionResult result) =>
		new() {
			Id = result.Id.Value,
			UserId = result.UserId.Value,
			Effect = result.Entry.Effect,
			Start = result.Entry.Interval.Start.ToDateTimeOffset(),
			End = result.Entry.Interval.End.ToDateTimeOffset(),
			RateOverrideAmountPerHour = result.Entry.RateOverride?.AmountPerHour,
			Reason = result.Reason,
			CreatedByUserId = result.CreatedBy.Value,
			ChangedAt = result.ChangedAt.ToDateTimeOffset(),
			Version = result.Version,
		};

	private static DateOnly ToDateOnly(LocalDate date) => new(date.Year, date.Month, date.Day);

	private static TimeOnly ToTimeOnly(LocalTime time) => new(time.Hour, time.Minute, time.Second);

	private static IsoDayOfWeek ToIsoDayOfWeek(DayOfWeek day) => day switch {
		DayOfWeek.Monday => IsoDayOfWeek.Monday,
		DayOfWeek.Tuesday => IsoDayOfWeek.Tuesday,
		DayOfWeek.Wednesday => IsoDayOfWeek.Wednesday,
		DayOfWeek.Thursday => IsoDayOfWeek.Thursday,
		DayOfWeek.Friday => IsoDayOfWeek.Friday,
		DayOfWeek.Saturday => IsoDayOfWeek.Saturday,
		DayOfWeek.Sunday => IsoDayOfWeek.Sunday,
		_ => throw new ArgumentOutOfRangeException(nameof(day), day, "A weekly interval must specify a real day."),
	};

	private static DayOfWeek ToDayOfWeek(IsoDayOfWeek day) => day switch {
		IsoDayOfWeek.Monday => DayOfWeek.Monday,
		IsoDayOfWeek.Tuesday => DayOfWeek.Tuesday,
		IsoDayOfWeek.Wednesday => DayOfWeek.Wednesday,
		IsoDayOfWeek.Thursday => DayOfWeek.Thursday,
		IsoDayOfWeek.Friday => DayOfWeek.Friday,
		IsoDayOfWeek.Saturday => DayOfWeek.Saturday,
		IsoDayOfWeek.Sunday => DayOfWeek.Sunday,
		_ => throw new ArgumentOutOfRangeException(nameof(day), day, "A weekly interval must specify a real day."),
	};

	// Minimal API endpoints get no built-in `[ValidateAntiForgeryToken]` equivalent -- that filter
	// exists only for MVC/Razor Pages -- so state-changing `/api/*` writes validate explicitly via
	// this filter (plan §8.1 fix 2.1) rather than relying on Minimal APIs' automatic antiforgery
	// metadata, which only attaches to `[FromForm]`-bound parameters, not our `[FromBody]` JSON.
	private sealed class AntiforgeryValidationFilter(IAntiforgery antiforgery) : IEndpointFilter
	{
		public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
		{
			// A bearer-authenticated request (ADR 0029) carries no ambient browser credential, so
			// it is not subject to the cross-site request forgery threat antiforgery tokens exist
			// to mitigate -- requiring one here would add friction without closing a real threat.
			if (PersonalAccessTokenAuthenticationDefaults.IsBearerRequest(context.HttpContext)) {
				return await next(context);
			}

			try {
				await antiforgery.ValidateRequestAsync(context.HttpContext);
			}
			catch (AntiforgeryValidationException) {
				return Problem(
					StatusCodes.Status400BadRequest,
					"Invalid request",
					"The request failed CSRF validation.",
					ValidationProblemType);
			}

			return await next(context);
		}
	}

	/// <summary>
	///     Bounded per-request telemetry (plan §4.4): operation name, correlation id, status family, and
	///     duration only -- never the request/response body, so a rate or cost value returned by a
	///     handler can never reach this log line by construction. The correlation id generated here is
	///     stashed in <see cref="HttpContext.Items" /> so <see cref="ExecuteAsync" /> can reuse the same
	///     value for the <see cref="CommandContext" /> passed into the library, tying one HTTP request to
	///     its audit-trail correlation id.
	/// </summary>
	internal sealed partial class ApiTelemetryFilter(ILogger<ApiTelemetryFilter> logger) : IEndpointFilter
	{
		internal const string CorrelationIdItemKey = "JobTrackApi.CorrelationId";

		public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
		{
			var correlationId = Guid.NewGuid();
			context.HttpContext.Items[CorrelationIdItemKey] = correlationId;
			var operation = context.HttpContext.GetEndpoint()?.DisplayName ?? "unknown";
			var stopwatch = Stopwatch.StartNew();

			var result = await next(context);

			stopwatch.Stop();
			var (statusCode, failureCategory) = DescribeResult(result);
			LogApiRequest(logger, operation, correlationId, statusCode, stopwatch.ElapsedMilliseconds, failureCategory);

			return result;
		}

		[LoggerMessage(
			Level = LogLevel.Information,
			Message =
				"api_request operation={Operation} correlation_id={CorrelationId} status_code={StatusCode} duration_ms={DurationMs} failure_category={FailureCategory}")]
		private static partial void LogApiRequest(
			ILogger logger, string operation, Guid correlationId, int statusCode, long durationMs, string failureCategory);

		private static (int StatusCode, string FailureCategory) DescribeResult(object? result) => result switch {
			ProblemHttpResult problem => (problem.ProblemDetails.Status ?? StatusCodes.Status500InternalServerError,
				problem.ProblemDetails.Type ?? "unknown"),
			IStatusCodeHttpResult statusResult => (statusResult.StatusCode ?? StatusCodes.Status200OK, "success"),
			_ => (StatusCodes.Status200OK, "success"),
		};
	}

	internal sealed class AntiforgeryTokenResponse
	{
		public required string Token { get; init; }
	}

	/// <summary>
	///     Response envelope for every bounded, ordered collection endpoint (remediation plan §3.1).
	///     <see cref="OrderedBy" /> documents the deterministic sort a client can rely on across pages;
	///     <see cref="HasMore" /> tells the client whether requesting <see cref="Offset" /> + <see cref="PageSize" />
	///     next is worthwhile, without a separate count query.
	/// </summary>
	internal sealed class PagedResponse<T>
	{
		public required T[] Items { get; init; }

		public required int Offset { get; init; }

		public required int PageSize { get; init; }

		public required bool HasMore { get; init; }

		public required string OrderedBy { get; init; }
	}

	internal sealed class JobNodeDetailResponse
	{
		public required JobNodeResponse Node { get; init; }

		public required JobNodeAncestorResponse[] Ancestors { get; init; }
	}

	internal sealed class JobNodeResponse
	{
		public required long Id { get; init; }

		public long? ParentId { get; init; }

		/// <summary>Contextual root/branch/leaf label derived from parent and child structure, not stored.</summary>
		public required NodeKind Kind { get; init; }

		/// <summary>Whether this node has at least one direct child.</summary>
		public required bool HasChildren { get; init; }

		/// <summary>Whether this node has an attached leaf-work row.</summary>
		public required bool HasLeafWork { get; init; }

		public required string Description { get; init; }

		public string? WriteUp { get; init; }

		public required long PostedByUserId { get; init; }

		public required long? OwnerUserId { get; init; }

		public decimal? ExpectedDurationHours { get; init; }

		public decimal? ExpectedCost { get; init; }

		public DateTimeOffset? NeededStart { get; init; }

		public DateTimeOffset? NeededFinish { get; init; }

		public required Priority Priority { get; init; }

		public required DateTimeOffset PostedAt { get; init; }

		public DateTimeOffset? ArchivedAt { get; init; }

		public required long Version { get; init; }
	}

	internal sealed class JobNodeAncestorResponse
	{
		public required long Id { get; init; }

		public required string Description { get; init; }

		/// <summary>Contextual root/branch/leaf label derived from parent and child structure, not stored.</summary>
		public required NodeKind Kind { get; init; }
	}

	internal sealed class JobNodeSummaryResponse
	{
		public required long Id { get; init; }

		public long? ParentId { get; init; }

		/// <summary>Contextual root/branch/leaf label derived from parent and child structure, not stored.</summary>
		public required NodeKind Kind { get; init; }

		public required string Description { get; init; }

		public required long? OwnerUserId { get; init; }

		public required Priority Priority { get; init; }

		public DateTimeOffset? ArchivedAt { get; init; }

		/// <summary>Whether this node has at least one direct child.</summary>
		public required bool HasChildren { get; init; }

		/// <summary>Whether this node has an attached leaf-work row.</summary>
		public required bool HasLeafWork { get; init; }
	}

	internal sealed class ReadinessResponse
	{
		public required bool IsReady { get; init; }

		public required UnsatisfiedPrerequisiteResponse[] Blockers { get; init; }
	}

	internal sealed class UnsatisfiedPrerequisiteResponse
	{
		public required long RequiredJobId { get; init; }

		public required long DeclaredOnJobId { get; init; }
	}

	internal sealed class CostDetailsResponse
	{
		public required long NodeId { get; init; }

		public required decimal ExactCost { get; init; }

		public required decimal DisplayedCost { get; init; }

		public required CostSegmentTraceResponse[] Trace { get; init; }

		public required string TzdbVersion { get; init; }
	}

	internal sealed class CostSegmentTraceResponse
	{
		public required DateTimeOffset SegmentStart { get; init; }

		public required DateTimeOffset SegmentEnd { get; init; }

		public required bool IsWorkingTime { get; init; }

		public required long[] ActiveSessionIds { get; init; }

		public required long SessionId { get; init; }

		public required long NodeId { get; init; }

		public required long SegmentTicks { get; init; }

		public required int ConcurrencyDivisor { get; init; }

		public required decimal AmountPerHour { get; init; }

		public required RateSource RateSource { get; init; }

		public required decimal UnroundedContribution { get; init; }
	}

	internal sealed class HierarchyTotalsResponse
	{
		public required long NodeId { get; init; }

		public required HierarchyNodeCostResponse[] Nodes { get; init; }

		public required string TzdbVersion { get; init; }
	}

	internal sealed class HierarchyNodeCostResponse
	{
		public required long NodeId { get; init; }

		public required decimal ExactCost { get; init; }

		public required decimal DisplayedCost { get; init; }
	}

	internal sealed class PrerequisiteEdgeResponse
	{
		public required long RequiredJobId { get; init; }

		public required long DependentJobId { get; init; }
	}

	internal sealed class JobSubtreeResponse
	{
		public required long RootId { get; init; }

		/// <summary>Null when the actor may not view this subtree's cost (ADR 0040) -- never a whole-request denial.</summary>
		public decimal? RootTotal { get; init; }

		/// <summary>Null exactly when <see cref="RootTotal" /> is.</summary>
		public string? TzdbVersion { get; init; }

		public required JobSubtreeNodeResponse[] Nodes { get; init; }
	}

	internal sealed class JobSubtreeNodeResponse
	{
		public required long Id { get; init; }

		public long? ParentId { get; init; }

		/// <summary>Contextual root/branch/leaf label derived from parent and child structure, not stored.</summary>
		public required NodeKind Kind { get; init; }

		/// <summary>Depth below the requested subtree root; the root itself is 0.</summary>
		public required int Depth { get; init; }

		public required string Description { get; init; }

		public long? OwnerUserId { get; init; }

		public required Priority Priority { get; init; }

		public DateTimeOffset? ArchivedAt { get; init; }

		/// <summary>Whether this node has at least one direct child.</summary>
		public required bool HasChildren { get; init; }

		/// <summary>Whether this node has an attached leaf-work row.</summary>
		public required bool HasLeafWork { get; init; }

		/// <summary>
		///     Whether every prerequisite declared on this node or on any ancestor is satisfied (spec §6,
		///     ADR 0043). Aggregates over ancestors, never over descendants: a branch stays ready when a
		///     descendant of it is blocked.
		/// </summary>
		public required bool IsReady { get; init; }

		/// <summary>Whether this node has children beyond what this fetch expanded (ADR 0039) -- drill in for the rest.</summary>
		public required bool HasUnexpandedChildren { get; init; }

		/// <summary>Whether this node itself matched the requested ownership/archive filter (ADR 0039 decision 5).</summary>
		public required bool MatchesFilter { get; init; }

		/// <summary>Ordinal pre-order position within this fetch, rebased to 0 at the subtree root (ADR 0039 decision 3).</summary>
		public required int SubtreeLft { get; init; }

		/// <summary>Ordinal post-order position paired with <see cref="SubtreeLft" />.</summary>
		public required int SubtreeRgt { get; init; }

		/// <summary>Null when the actor may not view this subtree's cost (ADR 0040) -- never a whole-request denial.</summary>
		public decimal? Cost { get; init; }
	}

	internal sealed class AddPrerequisiteBody
	{
		public required long RequiredJobId { get; init; }
	}

	internal sealed class LeafWorkResponse
	{
		public required long JobNodeId { get; init; }

		public required Achievement Achievement { get; init; }

		public string? PartialCriteria { get; init; }

		public string? FullCriteria { get; init; }

		public required DateTimeOffset ChangedAt { get; init; }

		public required long Version { get; init; }
	}

	internal sealed class SetAchievementBody
	{
		public required Achievement NewAchievement { get; init; }

		[Required] public required string Reason { get; init; }

		public required long Version { get; init; }
	}

	internal sealed class WorkSessionResponse
	{
		public required long Id { get; init; }

		public required long LeafWorkId { get; init; }

		public required long WorkedByUserId { get; init; }

		public required DateTimeOffset StartedAt { get; init; }

		public DateTimeOffset? FinishedAt { get; init; }

		public required DateTimeOffset ChangedAt { get; init; }

		public required long Version { get; init; }
	}

	internal sealed class StartSessionBody
	{
		public required long WorkedByUserId { get; init; }

		public DateTimeOffset? StartedAt { get; init; }
	}

	internal sealed class FinishSessionBody
	{
		public required long Version { get; init; }

		public DateTimeOffset? FinishedAt { get; init; }
	}

	/// <summary>
	///     Nested write-up change (remediation plan §2.1) -- omitted entirely on the containing body
	///     means "no write-up change"; present with <see cref="WriteUp" /> itself <see langword="null" />
	///     means "clear the write-up".
	/// </summary>
	internal sealed class WriteUpChangeBody
	{
		public required long NodeVersion { get; init; }

		public string? WriteUp { get; init; }
	}

	internal sealed class FinishSessionAndUpdateWriteUpBody
	{
		public required long Version { get; init; }

		public DateTimeOffset? FinishedAt { get; init; }

		public WriteUpChangeBody? WriteUpChange { get; init; }
	}

	internal sealed class FinishSessionAndUpdateWriteUpResponse
	{
		public required WorkSessionResponse Session { get; init; }

		public required bool WriteUpChanged { get; init; }

		public JobNodeResponse? Node { get; init; }
	}

	internal sealed class CorrectSessionBody
	{
		public required DateTimeOffset StartedAt { get; init; }

		public DateTimeOffset? FinishedAt { get; init; }

		[Required] public required string Reason { get; init; }

		public required long Version { get; init; }
	}

	internal sealed class ExpectedActiveSessionBody
	{
		public required long Id { get; init; }

		public required long Version { get; init; }
	}

	internal sealed class CompleteLeafBody
	{
		public required long Version { get; init; }

		public required ExpectedActiveSessionBody[] ExpectedActiveSessions { get; init; }

		public DateTimeOffset? FinishedAt { get; init; }

		public string? CompletionNote { get; init; }

		/// <summary>
		///     The achievement to record (ADR 0047) -- <see langword="null" /> (the wire default) means
		///     <see cref="Achievement.Success" />, preserving every existing client's behavior.
		/// </summary>
		public Achievement? FinalAchievement { get; init; }

		/// <summary>An optional write-up change applied in the same commit as this completion (remediation plan §2.1).</summary>
		public WriteUpChangeBody? WriteUpChange { get; init; }
	}

	internal sealed class CompleteLeafResponse
	{
		public required long JobNodeId { get; init; }

		public required Achievement Achievement { get; init; }

		public required DateTimeOffset ChangedAt { get; init; }

		public required long Version { get; init; }

		public required WorkSessionResponse[] FinishedSessions { get; init; }

		public required bool WriteUpChanged { get; init; }

		public JobNodeResponse? Node { get; init; }
	}

	internal sealed class ReopenAndStartWorkBody
	{
		public required long Version { get; init; }

		[Required] public required string Reason { get; init; }

		public required long WorkedByUserId { get; init; }

		public DateTimeOffset? StartedAt { get; init; }
	}

	internal sealed class ReopenAndStartWorkResponse
	{
		public required long JobNodeId { get; init; }

		public required Achievement Achievement { get; init; }

		public required DateTimeOffset ChangedAt { get; init; }

		public required long Version { get; init; }

		public required WorkSessionResponse Session { get; init; }
	}

	internal sealed class RatesResponse
	{
		public required UserCostRateResponse[] UserCostRates { get; init; }

		public required NodeRateOverrideResponse[] NodeRateOverrides { get; init; }
	}

	internal sealed class UserCostRateResponse
	{
		public required long Id { get; init; }

		public required long UserId { get; init; }

		public required decimal AmountPerHour { get; init; }

		public required DateTimeOffset EffectiveStart { get; init; }

		public DateTimeOffset? EffectiveEnd { get; init; }

		public required DateTimeOffset ChangedAt { get; init; }

		public required long Version { get; init; }
	}

	internal sealed class NodeRateOverrideResponse
	{
		public required long Id { get; init; }

		public required long UserId { get; init; }

		public required long NodeId { get; init; }

		public required decimal AmountPerHour { get; init; }

		public required DateTimeOffset EffectiveStart { get; init; }

		public DateTimeOffset? EffectiveEnd { get; init; }

		public required DateTimeOffset ChangedAt { get; init; }

		public required long Version { get; init; }
	}

	internal sealed class ScheduleResponse
	{
		public required ScheduleVersionResponse[] Versions { get; init; }

		public required ScheduleExceptionResponse[] Exceptions { get; init; }
	}

	internal sealed class ScheduleVersionResponse
	{
		public required long Id { get; init; }

		public required long UserId { get; init; }

		public required string IanaTimeZone { get; init; }

		public required DateOnly EffectiveStart { get; init; }

		public DateOnly? EffectiveEnd { get; init; }

		public required WeeklyIntervalResponse[] WeeklyIntervals { get; init; }

		public required DateTimeOffset ChangedAt { get; init; }

		public required long Version { get; init; }
	}

	internal sealed class WeeklyIntervalResponse
	{
		public required DayOfWeek Day { get; init; }

		public required TimeOnly Start { get; init; }

		public required TimeOnly End { get; init; }
	}

	internal sealed class ScheduleExceptionResponse
	{
		public required long Id { get; init; }

		public required long UserId { get; init; }

		public required ScheduleExceptionEffect Effect { get; init; }

		public required DateTimeOffset Start { get; init; }

		public required DateTimeOffset End { get; init; }

		public decimal? RateOverrideAmountPerHour { get; init; }

		public required string Reason { get; init; }

		public required long CreatedByUserId { get; init; }

		public required DateTimeOffset ChangedAt { get; init; }

		public required long Version { get; init; }
	}

	internal sealed class HoldingAreaResponse
	{
		public required long Id { get; init; }

		public required string Name { get; init; }
	}

	internal sealed class RequestResponse
	{
		public required long JobNodeId { get; init; }

		public required string Description { get; init; }

		public required DateTimeOffset SubmittedAt { get; init; }

		public DateTimeOffset? AcknowledgedAt { get; init; }

		public required long Version { get; init; }
	}

	internal sealed class SubmitRequestBody
	{
		[Required] public required string Description { get; init; }

		public required long HoldingAreaId { get; init; }
	}

	internal sealed class RequestDetailResponse
	{
		public required long JobNodeId { get; init; }

		public required string Description { get; init; }

		public required RequesterStatus Status { get; init; }

		public required DateTimeOffset SubmittedAt { get; init; }

		public DateTimeOffset? AcknowledgedAt { get; init; }

		public required long Version { get; init; }

		public required RequesterSubtreeNodeResponse[] Subtree { get; init; }

		public required RequestNoteResponse[] Notes { get; init; }
	}

	internal sealed class RequesterSubtreeNodeResponse
	{
		public required long JobNodeId { get; init; }

		public required string Description { get; init; }

		public required RequesterStatus Status { get; init; }

		public long? ParentId { get; init; }

		public required DateTimeOffset LastUpdatedAt { get; init; }
	}

	internal sealed class RequestNoteResponse
	{
		public required long Id { get; init; }

		public required long AuthorUserId { get; init; }

		public required string Content { get; init; }

		public required bool VisibleToRequester { get; init; }

		public required DateTimeOffset CreatedAt { get; init; }
	}

	internal sealed class AddRequestNoteBody
	{
		[Required] public required string Content { get; init; }

		public bool VisibleToRequester { get; init; }
	}

	internal sealed class AcknowledgeRequestBody
	{
		public required long Version { get; init; }
	}

	internal sealed class AddUserCostRateBody
	{
		public required decimal AmountPerHour { get; init; }

		public required DateTimeOffset EffectiveStart { get; init; }

		public DateTimeOffset? EffectiveEnd { get; init; }
	}

	internal sealed class AddNodeRateOverrideBody
	{
		public required long NodeId { get; init; }

		public required decimal AmountPerHour { get; init; }

		public required DateTimeOffset EffectiveStart { get; init; }

		public DateTimeOffset? EffectiveEnd { get; init; }
	}

	internal sealed class CorrectUserCostRateBody
	{
		public required decimal AmountPerHour { get; init; }

		public required DateTimeOffset EffectiveStart { get; init; }

		public DateTimeOffset? EffectiveEnd { get; init; }

		[Required] public required string Reason { get; init; }

		public required long Version { get; init; }
	}

	internal sealed class CorrectNodeRateOverrideBody
	{
		public required long NodeId { get; init; }

		public required decimal AmountPerHour { get; init; }

		public required DateTimeOffset EffectiveStart { get; init; }

		public DateTimeOffset? EffectiveEnd { get; init; }

		[Required] public required string Reason { get; init; }

		public required long Version { get; init; }
	}

	internal sealed class AddScheduleVersionBody
	{
		[Required] public required string IanaTimeZone { get; init; }

		public required DateOnly EffectiveStart { get; init; }

		public DateOnly? EffectiveEnd { get; init; }

		[Required] public required WeeklyIntervalBody[] WeeklyIntervals { get; init; }
	}

	internal sealed class WeeklyIntervalBody
	{
		public required DayOfWeek Day { get; init; }

		public required TimeOnly Start { get; init; }

		public required TimeOnly End { get; init; }
	}

	internal sealed class AddScheduleExceptionBody
	{
		public required ScheduleExceptionEffect Effect { get; init; }

		public required DateTimeOffset Start { get; init; }

		public required DateTimeOffset End { get; init; }

		public decimal? RateOverrideAmountPerHour { get; init; }

		[Required] public required string Reason { get; init; }
	}

	internal sealed class CorrectScheduleVersionBody
	{
		[Required] public required string IanaTimeZone { get; init; }

		public required DateOnly EffectiveStart { get; init; }

		public DateOnly? EffectiveEnd { get; init; }

		[Required] public required WeeklyIntervalBody[] WeeklyIntervals { get; init; }

		[Required] public required string Reason { get; init; }

		public required long Version { get; init; }
	}

	internal sealed class CorrectScheduleExceptionBody
	{
		public required ScheduleExceptionEffect Effect { get; init; }

		public required DateTimeOffset Start { get; init; }

		public required DateTimeOffset End { get; init; }

		public decimal? RateOverrideAmountPerHour { get; init; }

		[Required] public required string Reason { get; init; }

		public required long Version { get; init; }
	}
}
