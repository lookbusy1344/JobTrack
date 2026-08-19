namespace JobTrack.Web;

using System.Diagnostics;
using System.Text.Json.Serialization;
using Abstractions;
using Application;
using Identity;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using NodaTime.TimeZones;

/// <summary>
///     External HTTP API composition root (remediation plan §2.5): endpoint handlers, response/body
///     types, and mapping code live in per-resource partial files (<c>JobTrackApi.Rates.cs</c>,
///     <c>JobTrackApi.Jobs.cs</c>, <c>JobTrackApi.Sessions.cs</c>, <c>JobTrackApi.Cost.cs</c>,
///     <c>JobTrackApi.Schedules.cs</c>, <c>JobTrackApi.Requests.cs</c>); this file keeps only the
///     shared composition root (<see cref="MapJobTrackApi" />), cross-cutting authentication/error
///     handling, and the shared response envelope types every partial uses.
/// </summary>
internal static partial class JobTrackApi
{
	/// <summary>
	///     Also read by <c>Program.cs</c>'s rate-limit middleware, which must run before
	///     <c>UseAuthorization()</c> -- an endpoint filter runs only after authorization succeeds, so
	///     it cannot enforce a limit against requests authorization would otherwise reject, unlike
	///     this path-prefix middleware check (ADR 0066 Stage 5).
	/// </summary>
	internal const string ApiPathPrefix = "/api";

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

	/// <summary>
	///     Maps <c>/openapi</c> discovery and the shared <c>/api</c> route group, then delegates every
	///     resource's own endpoints to the same partial file its handlers live in
	///     (<c>JobTrackApi.Rates.cs</c>, <c>.Jobs.cs</c>, <c>.Sessions.cs</c>, <c>.Cost.cs</c>,
	///     <c>.Schedules.cs</c>, <c>.Requests.cs</c>) so the declarative catalogue stays reviewable one
	///     resource at a time rather than as a single flat list.
	/// </summary>
	public static void MapJobTrackApi(this WebApplication app)
	{
		// Unauthenticated route/schema discovery lowers reconnaissance cost against an
		// employee-only, single-organisation system for no operational benefit (security review
		// remediation §2.5) -- gated behind the same policy every other authenticated endpoint uses,
		// reachable by either the cookie scheme or a bearer PAT.
		_ = app.MapOpenApi($"/openapi/{OpenApiDocumentName}.json").RequireAuthorization(JobTrackPolicyNames.AnyEmployee);

		var api = app.MapGroup(ApiPathPrefix)
					 .WithTags("JobTrack API")
					 .AddEndpointFilter<ApiTelemetryFilter>()
					 .AddEndpointFilter<RequiresPasswordChangeEndpointFilter>()
					 .ProducesProblem(StatusCodes.Status429TooManyRequests)
					 .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
					 .ProducesProblem(StatusCodes.Status403Forbidden);

		_ = api.MapGet("/antiforgery-token", GetAntiforgeryToken)
			   .RequireAuthorization(JobTrackPolicyNames.AnyAuthenticatedUser)
			   .WithName("GetAntiforgeryToken")
			   .WithSummary($"Get a CSRF token to send back in the '{AntiforgeryHeaderName}' header on state-changing API requests.")
			   .Produces<AntiforgeryTokenResponse>()
			   .ProducesProblem(StatusCodes.Status401Unauthorized);

		api.MapRateEndpoints();
		api.MapJobEndpoints();
		api.MapSessionEndpoints();
		api.MapCostEndpoints();
		api.MapScheduleEndpoints();
		api.MapRequestEndpoints();
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

	private static RouteHandlerBuilder WithStandardWriteContract(
		this RouteHandlerBuilder endpoint, string policy, string name, string summary) =>
		endpoint
			.RequireAuthorization(policy)
			.AddEndpointFilter<AntiforgeryValidationFilter>()
			.WithName(name)
			.WithSummary(summary)
			.ProducesProblem(StatusCodes.Status400BadRequest)
			.ProducesProblem(StatusCodes.Status401Unauthorized)
			.ProducesProblem(StatusCodes.Status403Forbidden)
			.ProducesProblem(StatusCodes.Status404NotFound)
			.ProducesProblem(StatusCodes.Status409Conflict)
			.ProducesProblem(StatusCodes.Status413PayloadTooLarge);

	private static bool IsApiRequest(HttpRequest request) =>
		request.Path.StartsWithSegments(ApiPathPrefix, StringComparison.OrdinalIgnoreCase);

	private static Ok<AntiforgeryTokenResponse> GetAntiforgeryToken(HttpContext httpContext, IAntiforgery antiforgery)
	{
		var tokens = antiforgery.GetAndStoreTokens(httpContext);
		return TypedResults.Ok(new AntiforgeryTokenResponse {
			Token = tokens.RequestToken!,
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
			return await action(new() {
				Actor = actor.AppUserId,
				CorrelationId = correlationId,
			});
		}
		catch (AuthorizationDeniedException) {
			return Problem(
				StatusCodes.Status403Forbidden, "Forbidden", "You do not have permission to perform this action.", ForbiddenProblemType);
		}
		catch (EntityNotFoundException) {
			return Problem(StatusCodes.Status404NotFound, "Not found", "The requested resource does not exist.", NotFoundProblemType);
		}
		catch (ConcurrencyConflictException ex) {
			// The response deliberately says no more than "reload and retry" -- but a repeated or
			// unexpected conflict can indicate a stale-version bug rather than routine concurrent
			// editing, and the underlying message (which port, which row) is otherwise lost.
			LogConcurrencyConflict(
				httpContext.RequestServices.GetRequiredService<ILogger<ApiTelemetryFilter>>(), correlationId, ex);
			return Problem(
				StatusCodes.Status409Conflict,
				"Concurrency conflict",
				"The resource has changed since it was last read. Reload and retry.",
				ConcurrencyProblemType);
		}
		catch (InvariantViolationException ex) {
			// The response deliberately says no more than "a constraint refused this" -- but the
			// operator needs the constraint id and, for the catch-all categories, the provider's own
			// error underneath it, or a refused write is indistinguishable from any other 409 in the
			// log stream (ADR 0068).
			LogInvariantViolation(
				httpContext.RequestServices.GetRequiredService<ILogger<ApiTelemetryFilter>>(), correlationId, ex.ConstraintId, ex);
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
		catch (MissingRateException ex) {
			// The response deliberately names no node or session -- but a missing rate is a rate-table
			// configuration gap, not caller error, and the underlying message (which node, which
			// instant) is otherwise lost.
			LogMissingRate(httpContext.RequestServices.GetRequiredService<ILogger<ApiTelemetryFilter>>(), correlationId, ex);
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

	[LoggerMessage(
		Level = LogLevel.Warning,
		Message = "api_invariant_violation correlation_id={CorrelationId} constraint={ConstraintId}")]
	private static partial void LogInvariantViolation(ILogger logger, Guid correlationId, string constraintId, Exception exception);

	[LoggerMessage(
		Level = LogLevel.Warning,
		Message = "api_concurrency_conflict correlation_id={CorrelationId}")]
	private static partial void LogConcurrencyConflict(ILogger logger, Guid correlationId, Exception exception);

	[LoggerMessage(
		Level = LogLevel.Warning,
		Message = "api_missing_rate correlation_id={CorrelationId}")]
	private static partial void LogMissingRate(ILogger logger, Guid correlationId, Exception exception);

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
}
