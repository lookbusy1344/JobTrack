namespace JobTrack.ExternalApiClient;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
///     First-party CLI client proof (plan §4.5, ADR 0029/0030): talks to JobTrack purely over the
///     published HTTP/OpenAPI contract using a bearer personal access token. Deliberately has no
///     project reference to any <c>JobTrack.*</c> library assembly -- every type here is this client's
///     own plain-JSON model, not a shared domain/application type, proving the external API is usable
///     without the reusable .NET library.
/// </summary>
public sealed class JobTrackApiClient : IDisposable
{
	private static readonly JsonSerializerOptions SerializerOptions = new() {
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		Converters = { new JsonStringEnumConverter() },
	};

	private readonly HttpClient _httpClient;

	public JobTrackApiClient(Uri baseAddress, string bearerToken)
		: this(new HttpClient { BaseAddress = baseAddress }, bearerToken)
	{
	}

	/// <summary>Accepts a pre-configured <see cref="HttpClient" /> (e.g. a test server's in-memory client).</summary>
	public JobTrackApiClient(HttpClient httpClient, string bearerToken)
	{
		ArgumentNullException.ThrowIfNull(httpClient);
		ArgumentException.ThrowIfNullOrWhiteSpace(bearerToken);

		_httpClient = httpClient;
		_httpClient.DefaultRequestHeaders.Authorization = new("Bearer", bearerToken);
	}

	public void Dispose() => _httpClient.Dispose();

	/// <summary>Read workflow: a node's detail and root-first ancestor breadcrumb.</summary>
	public async Task<JobNodeDetail> GetJobNodeAsync(long nodeId, CancellationToken cancellationToken = default)
	{
		using var response = await _httpClient.GetAsync(new Uri($"/api/jobs/{nodeId}", UriKind.Relative), cancellationToken).ConfigureAwait(false);
		await ThrowIfUnsuccessfulAsync(response, cancellationToken).ConfigureAwait(false);

		return (await response.Content.ReadFromJsonAsync<JobNodeDetail>(SerializerOptions, cancellationToken).ConfigureAwait(false))!;
	}

	/// <summary>
	///     Read workflow: a node's direct children, one page at a time (remediation plan §3.1 -- the
	///     server never returns an unbounded array for a growable collection). Returns the first page
	///     only; a caller needing every child pages through <see cref="PagedResult{T}.HasMore" /> itself.
	/// </summary>
	public async Task<PagedResult<JobNodeSummary>> GetJobChildrenAsync(long nodeId, CancellationToken cancellationToken = default)
	{
		using var response = await _httpClient.GetAsync(new Uri($"/api/jobs/{nodeId}/children", UriKind.Relative), cancellationToken)
			.ConfigureAwait(false);
		await ThrowIfUnsuccessfulAsync(response, cancellationToken).ConfigureAwait(false);

		return (await response.Content.ReadFromJsonAsync<PagedResult<JobNodeSummary>>(SerializerOptions, cancellationToken).ConfigureAwait(false))!;
	}

	/// <summary>
	///     Read workflow: a bounded multi-level subtree rooted at a node (ADR 0039). The cost roll-up
	///     (<see cref="JobSubtree.RootTotal" />, each node's <see cref="JobSubtreeNode.Cost" />) is
	///     included only when the caller may view it (ADR 0040) -- <see langword="null" /> otherwise,
	///     never a failed request.
	/// </summary>
	public async Task<JobSubtree> GetJobSubtreeAsync(long nodeId, int? depth = null, CancellationToken cancellationToken = default)
	{
		var query = depth is { } d ? $"?depth={d}" : string.Empty;
		using var response = await _httpClient.GetAsync(
			new Uri($"/api/jobs/{nodeId}/subtree{query}", UriKind.Relative), cancellationToken).ConfigureAwait(false);
		await ThrowIfUnsuccessfulAsync(response, cancellationToken).ConfigureAwait(false);

		return (await response.Content.ReadFromJsonAsync<JobSubtree>(SerializerOptions, cancellationToken).ConfigureAwait(false))!;
	}

	/// <summary>
	///     Mutation workflow: start a work session. Calling this again for an already-active
	///     worker/leaf pair is how a UI "resume" action is expressed, and is exactly the retry this
	///     method surfaces as a <see cref="JobTrackApiConflictException" /> rather than a crash (ADR
	///     0030's idempotency review: the library's own active-session invariant makes the retry safe).
	/// </summary>
	public async Task<WorkSession> StartSessionAsync(long leafNodeId, long workedByUserId, CancellationToken cancellationToken = default)
	{
		using var response = await _httpClient.PostAsJsonAsync(
			new Uri($"/api/jobs/{leafNodeId}/sessions", UriKind.Relative),
			new { workedByUserId },
			SerializerOptions,
			cancellationToken).ConfigureAwait(false);
		await ThrowIfUnsuccessfulAsync(response, cancellationToken).ConfigureAwait(false);

		return (await response.Content.ReadFromJsonAsync<WorkSession>(SerializerOptions, cancellationToken).ConfigureAwait(false))!;
	}

	/// <summary>
	///     Mutation workflow: claim an unassigned node from the pickup pool, setting its direct owner to
	///     the calling token's owner. An already-owned node -- including a lost race against a concurrent
	///     claimant -- surfaces as a <see cref="JobTrackApiConflictException" />, the same idempotency
	///     posture as <see cref="StartSessionAsync" />.
	/// </summary>
	public async Task<JobNode> PickUpJobNodeAsync(long nodeId, CancellationToken cancellationToken = default)
	{
		using var response = await _httpClient.PostAsync(
			new Uri($"/api/jobs/{nodeId}/pickup", UriKind.Relative), null, cancellationToken).ConfigureAwait(false);
		await ThrowIfUnsuccessfulAsync(response, cancellationToken).ConfigureAwait(false);

		return (await response.Content.ReadFromJsonAsync<JobNode>(SerializerOptions, cancellationToken).ConfigureAwait(false))!;
	}

	/// <summary>Read workflow: the active holding areas the token's owning requester may currently submit into (ADR 0033).</summary>
	public async Task<HoldingArea[]> GetEligibleHoldingAreasAsync(CancellationToken cancellationToken = default)
	{
		using var response = await _httpClient.GetAsync(new Uri("/api/request-holding-areas", UriKind.Relative), cancellationToken)
			.ConfigureAwait(false);
		await ThrowIfUnsuccessfulAsync(response, cancellationToken).ConfigureAwait(false);

		return (await response.Content.ReadFromJsonAsync<HoldingArea[]>(SerializerOptions, cancellationToken).ConfigureAwait(false))!;
	}

	/// <summary>Mutation workflow: submit a new request into a holding area the token's owning requester is eligible for (ADR 0033).</summary>
	public async Task<Request> SubmitRequestAsync(string description, long holdingAreaId, CancellationToken cancellationToken = default)
	{
		using var response = await _httpClient.PostAsJsonAsync(
			new Uri("/api/requests", UriKind.Relative),
			new { description, holdingAreaId },
			SerializerOptions,
			cancellationToken).ConfigureAwait(false);
		await ThrowIfUnsuccessfulAsync(response, cancellationToken).ConfigureAwait(false);

		return (await response.Content.ReadFromJsonAsync<Request>(SerializerOptions, cancellationToken).ConfigureAwait(false))!;
	}

	/// <summary>Read workflow: the token's owning requester's own submitted requests, most recent first (ADR 0033).</summary>
	public async Task<Request[]> GetMyRequestsAsync(CancellationToken cancellationToken = default)
	{
		using var response = await _httpClient.GetAsync(new Uri("/api/requests", UriKind.Relative), cancellationToken).ConfigureAwait(false);
		await ThrowIfUnsuccessfulAsync(response, cancellationToken).ConfigureAwait(false);

		return (await response.Content.ReadFromJsonAsync<Request[]>(SerializerOptions, cancellationToken).ConfigureAwait(false))!;
	}

	/// <summary>
	///     Read workflow: one permitted request's requester-safe detail -- status, read-only subtree, and
	///     visible notes (ADR 0034). Reachable by the request's own requester or by staff triaging it.
	/// </summary>
	public async Task<RequestDetail> GetRequestDetailAsync(long jobNodeId, CancellationToken cancellationToken = default)
	{
		using var response = await _httpClient.GetAsync(new Uri($"/api/requests/{jobNodeId}", UriKind.Relative), cancellationToken)
			.ConfigureAwait(false);
		await ThrowIfUnsuccessfulAsync(response, cancellationToken).ConfigureAwait(false);

		return (await response.Content.ReadFromJsonAsync<RequestDetail>(SerializerOptions, cancellationToken).ConfigureAwait(false))!;
	}

	/// <summary>
	///     Mutation workflow: add a note or clarification to a request's thread, posted by staff or by the
	///     request's own requester (ADR 0034). Visibility is honored only for a staff-authored note; a
	///     requester-authored note is always visible to the requester regardless of the value passed here.
	/// </summary>
	public async Task<RequestNote> AddRequestNoteAsync(
		long jobNodeId, string content, bool visibleToRequester, CancellationToken cancellationToken = default)
	{
		using var response = await _httpClient.PostAsJsonAsync(
			new Uri($"/api/requests/{jobNodeId}/comments", UriKind.Relative),
			new { content, visibleToRequester },
			SerializerOptions,
			cancellationToken).ConfigureAwait(false);
		await ThrowIfUnsuccessfulAsync(response, cancellationToken).ConfigureAwait(false);

		return (await response.Content.ReadFromJsonAsync<RequestNote>(SerializerOptions, cancellationToken).ConfigureAwait(false))!;
	}

	/// <summary>
	///     Staff mutation workflow: acknowledges a request, setting the explicit Accepted signal a
	///     requester sees (ADR 0034). Not reachable by the request's own requester.
	/// </summary>
	public async Task<Request> AcknowledgeRequestAsync(long jobNodeId, long version, CancellationToken cancellationToken = default)
	{
		using var response = await _httpClient.PostAsJsonAsync(
			new Uri($"/api/requests/{jobNodeId}/acknowledge", UriKind.Relative),
			new { version },
			SerializerOptions,
			cancellationToken).ConfigureAwait(false);
		await ThrowIfUnsuccessfulAsync(response, cancellationToken).ConfigureAwait(false);

		return (await response.Content.ReadFromJsonAsync<Request>(SerializerOptions, cancellationToken).ConfigureAwait(false))!;
	}

	private static async Task ThrowIfUnsuccessfulAsync(HttpResponseMessage response, CancellationToken cancellationToken)
	{
		if (response.IsSuccessStatusCode) {
			return;
		}

		// Every rejection, including a bearer challenge failure (missing, empty, malformed, expired,
		// revoked, or disabled-account token), carries an RFC 7807 body (remediation plan §3.3) --
		// the fallback below stays only as defense-in-depth against a body-stripping intermediary,
		// not because the API itself omits one.
		var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
		var detail = TryParseProblemDetail(body) ?? response.ReasonPhrase ?? "Request failed.";

		throw response.StatusCode switch {
			HttpStatusCode.Unauthorized => new JobTrackApiUnauthorizedException(detail),
			HttpStatusCode.Forbidden => new JobTrackApiForbiddenException(detail),
			HttpStatusCode.NotFound => new JobTrackApiNotFoundException(detail),
			HttpStatusCode.Conflict => new JobTrackApiConflictException(detail),
			_ => new JobTrackApiException(response.StatusCode, detail),
		};
	}

	private static string? TryParseProblemDetail(string body)
	{
		if (string.IsNullOrWhiteSpace(body)) {
			return null;
		}

		try {
			return JsonSerializer.Deserialize<ProblemDetailsPayload>(body, SerializerOptions)?.Detail;
		}
		catch (JsonException) {
			return null;
		}
	}
}

public sealed class JobNodeDetail
{
	public required JobNode Node { get; init; }

	public required JobNodeAncestor[] Ancestors { get; init; }
}

/// <summary>A node's full detail, as returned by <c>GET /api/jobs/{nodeId}</c>.</summary>
public sealed class JobNode
{
	public required long Id { get; init; }

	public long? ParentId { get; init; }

	/// <summary>Contextual root/branch/leaf label derived from parent and child structure, not stored.</summary>
	public required string Kind { get; init; }

	/// <summary>Whether this node has at least one direct child.</summary>
	public required bool HasChildren { get; init; }

	/// <summary>Whether this node has an attached leaf-work row.</summary>
	public required bool HasLeafWork { get; init; }

	public required string Description { get; init; }

	public long? OwnerUserId { get; init; }

	public required string Priority { get; init; }
}

/// <summary>
///     One row of a node listing, as returned by <c>GET /api/jobs/{nodeId}/children</c> or
///     <c>/api/jobs/search</c> -- lighter than <see cref="JobNode" />, with structural capability flags
///     for UI decisions.
/// </summary>
public sealed class JobNodeSummary
{
	public required long Id { get; init; }

	public long? ParentId { get; init; }

	/// <summary>Contextual root/branch/leaf label derived from parent and child structure, not stored.</summary>
	public required string Kind { get; init; }

	public required string Description { get; init; }

	public long? OwnerUserId { get; init; }

	public required string Priority { get; init; }

	public required bool HasChildren { get; init; }

	public required bool HasLeafWork { get; init; }
}

/// <summary>A bounded multi-level subtree, as returned by <c>GET /api/jobs/{nodeId}/subtree</c> (ADR 0039).</summary>
public sealed class JobSubtree
{
	public required long RootId { get; init; }

	/// <summary>Null when the caller may not view this subtree's cost (ADR 0040) -- never a failed request.</summary>
	public decimal? RootTotal { get; init; }

	public string? TzdbVersion { get; init; }

	public required JobSubtreeNode[] Nodes { get; init; }
}

/// <summary>One node of a <see cref="JobSubtree" /> (ADR 0039).</summary>
public sealed class JobSubtreeNode
{
	public required long Id { get; init; }

	public long? ParentId { get; init; }

	/// <summary>Contextual root/branch/leaf label derived from parent and child structure, not stored.</summary>
	public required string Kind { get; init; }

	/// <summary>Depth below the requested subtree root; the root itself is 0.</summary>
	public required int Depth { get; init; }

	public required string Description { get; init; }

	public long? OwnerUserId { get; init; }

	public required string Priority { get; init; }

	public required bool HasChildren { get; init; }

	public required bool HasLeafWork { get; init; }

	/// <summary>Whether this node has children beyond what this fetch expanded (ADR 0039) -- drill in for the rest.</summary>
	public required bool HasUnexpandedChildren { get; init; }

	/// <summary>Whether this node itself matched the requested ownership/archive filter.</summary>
	public required bool MatchesFilter { get; init; }

	/// <summary>Ordinal pre-order position within this fetch, rebased to 0 at the subtree root.</summary>
	public required int SubtreeLft { get; init; }

	/// <summary>Ordinal post-order position paired with <see cref="SubtreeLft" />.</summary>
	public required int SubtreeRgt { get; init; }

	/// <summary>Null when the caller may not view this subtree's cost (ADR 0040) -- never a failed request.</summary>
	public decimal? Cost { get; init; }
}

/// <summary>One page of a bounded, ordered collection response (remediation plan §3.1).</summary>
public sealed class PagedResult<T>
{
	public required IReadOnlyList<T> Items { get; init; }

	public required int Offset { get; init; }

	public required int PageSize { get; init; }

	public required bool HasMore { get; init; }

	public required string OrderedBy { get; init; }
}

public sealed class JobNodeAncestor
{
	public required long Id { get; init; }

	public required string Description { get; init; }

	/// <summary>Contextual root/branch/leaf label derived from parent and child structure, not stored.</summary>
	public required string Kind { get; init; }
}

public sealed class WorkSession
{
	public required long Id { get; init; }

	public required long LeafWorkId { get; init; }

	public required long WorkedByUserId { get; init; }

	public required DateTimeOffset StartedAt { get; init; }

	public DateTimeOffset? FinishedAt { get; init; }

	public required long Version { get; init; }
}

/// <summary>One active holding area, as returned by <c>GET /api/request-holding-areas</c> (ADR 0033).</summary>
public sealed class HoldingArea
{
	public required long Id { get; init; }

	public required string Name { get; init; }
}

/// <summary>One requester request, as returned by <c>POST /api/requests</c> or <c>GET /api/requests</c> (ADR 0033).</summary>
public sealed class Request
{
	public required long JobNodeId { get; init; }

	public required string Description { get; init; }

	public required DateTimeOffset SubmittedAt { get; init; }

	public DateTimeOffset? AcknowledgedAt { get; init; }

	public required long Version { get; init; }
}

/// <summary>A request's requester-safe detail projection, as returned by <c>GET /api/requests/{jobNodeId}</c> (ADR 0034).</summary>
public sealed class RequestDetail
{
	public required long JobNodeId { get; init; }

	public required string Description { get; init; }

	public required string Status { get; init; }

	public required DateTimeOffset SubmittedAt { get; init; }

	public DateTimeOffset? AcknowledgedAt { get; init; }

	public required long Version { get; init; }

	public required RequesterSubtreeNode[] Subtree { get; init; }

	public required RequestNote[] Notes { get; init; }
}

/// <summary>One node in a request's read-only subtree, as returned within <see cref="RequestDetail" /> (ADR 0034).</summary>
public sealed class RequesterSubtreeNode
{
	public required long JobNodeId { get; init; }

	public required string Description { get; init; }

	public required string Status { get; init; }

	public long? ParentId { get; init; }

	public required DateTimeOffset LastUpdatedAt { get; init; }
}

/// <summary>
///     One note on a request's thread, as returned by <c>POST /api/requests/{jobNodeId}/comments</c> or within <see cref="RequestDetail" /> (ADR
///     0034).
/// </summary>
public sealed class RequestNote
{
	public required long Id { get; init; }

	public required long AuthorUserId { get; init; }

	public required string Content { get; init; }

	public required bool VisibleToRequester { get; init; }

	public required DateTimeOffset CreatedAt { get; init; }
}

internal sealed class ProblemDetailsPayload
{
	public string? Title { get; init; }

	public string? Detail { get; init; }

	public string? Type { get; init; }
}

/// <summary>Base type for every non-2xx response the API returns (RFC 7807 problem details).</summary>
public class JobTrackApiException(HttpStatusCode statusCode, string detail) : Exception(detail)
{
	public HttpStatusCode StatusCode { get; } = statusCode;
}

/// <summary>The bearer token is missing, expired, or revoked -- authenticate with a fresh token.</summary>
public sealed class JobTrackApiUnauthorizedException(string detail) : JobTrackApiException(HttpStatusCode.Unauthorized, detail);

/// <summary>The token authenticated, but its owner lacks permission for this operation.</summary>
public sealed class JobTrackApiForbiddenException(string detail) : JobTrackApiException(HttpStatusCode.Forbidden, detail);

/// <summary>The requested resource does not exist.</summary>
public sealed class JobTrackApiNotFoundException(string detail) : JobTrackApiException(HttpStatusCode.NotFound, detail);

/// <summary>
///     The request collided with an existing invariant (e.g. an already-active session) -- safe to
///     report to the operator rather than retry blindly, per ADR 0030's idempotency review.
/// </summary>
public sealed class JobTrackApiConflictException(string detail) : JobTrackApiException(HttpStatusCode.Conflict, detail);
