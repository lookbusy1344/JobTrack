namespace JobTrack.Web.EndToEndTests;

using System.Diagnostics;
using System.Net;
using AwesomeAssertions;

/// <summary>
///     Real-Kestrel, real-process production-security evidence (security review remediation §2.6):
///     oversized-body rejection independent of a declared <c>Content-Length</c>, request-timeout
///     cutoff, forwarded-proto trust boundary behavior, and HSTS/HTTPS redirection. Closes the gap
///     <c>docs/operations/web-host-security.md</c> names as unprovable through
///     <c>WebApplicationFactory</c>'s in-process <c>TestServer</c>.
/// </summary>
public sealed class TrustedProxyProductionSecuritySmokeTests : IClassFixture<TrustedProxyProductionHostFixture>
{
	private readonly TrustedProxyProductionHostFixture fixture;

	public TrustedProxyProductionSecuritySmokeTests(TrustedProxyProductionHostFixture fixture) => this.fixture = fixture;

	[Fact]
	public async Task Https_responses_include_the_hsts_header()
	{
		using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true };
		using var client = new HttpClient(handler);
		using var request = new HttpRequestMessage(HttpMethod.Get, $"{fixture.HttpsBaseAddress}/Account/Login");
		// HstsMiddleware's default ExcludedHosts skips "localhost"/"127.0.0.1"/"::1" -- a deliberate
		// ASP.NET Core convention so local development isn't HSTS-enforced, not a product gap. This
		// overrides only the Host header (never the socket address the client actually connects to,
		// which stays 127.0.0.1) so the middleware sees a production-like hostname instead.
		request.Headers.Host = "jobtrack.internal.test";

		var response = await client.SendAsync(request);

		response.Headers.Contains("Strict-Transport-Security").Should().BeTrue(
			"HSTS only activates outside Development (Program.cs), and this fixture runs in Production");
	}

	[Fact]
	public async Task A_forwarded_proto_from_a_trusted_proxy_is_honored_and_skips_https_redirection()
	{
		using var handler = new HttpClientHandler { AllowAutoRedirect = false };
		using var client = new HttpClient(handler);
		using var request = new HttpRequestMessage(HttpMethod.Get, $"{fixture.HttpBaseAddress}/Account/Login");
		request.Headers.Add("X-Forwarded-Proto", "https");
		request.Headers.Add("X-Forwarded-For", "203.0.113.5");

		var response = await client.SendAsync(request);

		response.StatusCode.Should().Be(HttpStatusCode.OK,
			"the loopback connection is a trusted proxy, so the forwarded X-Forwarded-Proto is honored and no HTTPS redirect is issued");
	}

	[Fact]
	public async Task An_oversized_chunked_request_body_without_a_content_length_header_is_rejected()
	{
		using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true };
		using var client = new HttpClient(handler);
		using var request = new HttpRequestMessage(HttpMethod.Post, $"{fixture.HttpsBaseAddress}/Account/Login");
		request.Headers.TransferEncodingChunked = true;
		request.Content = new OversizedFormContent();

		var act = async () => await client.SendAsync(request);
		var result = await act.Should().NotThrowAsync(
			"Kestrel/ASP.NET Core should complete the exchange with a rejection response, not merely reset the connection");

		// Unlike the Content-Length-aware middleware check (Program.cs), which returns 413, Kestrel's
		// own MaxRequestBodySize guard rejects a body exceeding the limit mid-read as a 400 Bad
		// Request -- this is the real Kestrel behavior this fixture exists to prove, not the
		// documented middleware's status code.
		result.Which.StatusCode.Should().Be(HttpStatusCode.BadRequest,
			"Kestrel's own MaxRequestBodySize guard (Program.cs) must reject an oversized body even without a declared Content-Length");
	}

	[Fact]
	public async Task A_slow_trickling_request_body_is_cut_off_well_before_a_minute()
	{
		// A blind fixed client-side delay before the second write is not valid evidence here: if
		// the server aborts the connection early, a write that only happens *after* a fixed sleep
		// completes would never observe that abort until the sleep itself elapses, making the
		// measured duration always equal the sleep regardless of server behavior. Instead this
		// content trickles one byte per second (1 byte/s, far under Kestrel's configured 240
		// bytes/s MinRequestBodyDataRate) so a write fails as soon as the server actually closes the
		// connection, and the elapsed time genuinely reflects when that happened.
		using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true };
		using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(90) };
		using var request = new HttpRequestMessage(HttpMethod.Post, $"{fixture.HttpsBaseAddress}/Account/Login");
		request.Headers.TransferEncodingChunked = true;
		request.Content = new SlowTricklingFormContent(TimeSpan.FromSeconds(75));

		var stopwatch = Stopwatch.StartNew();
		try {
			var response = await client.SendAsync(request);
			stopwatch.Stop();
			response.IsSuccessStatusCode.Should().BeFalse("a request whose body trickles in far below the minimum data rate must not succeed");
		}
		catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException) {
			stopwatch.Stop();
			// The connection being aborted client-side is itself the expected evidence of server-side cutoff.
		}

		stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(20),
			"Kestrel's MinRequestBodyDataRate (240 bytes/s after a 5s grace period, Program.cs) should cut a 1-byte/s connection off well before the 75-second maximum trickle duration");
	}

	private sealed class OversizedFormContent : HttpContent
	{
		// One byte over Program.cs's 64KB MaxRequestBodyBytes.
		private const int OversizedByteCount = (64 * 1024) + 1;

		public OversizedFormContent() => Headers.ContentType = new("application/x-www-form-urlencoded");

		protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
		{
			var buffer = new byte[8192];
			Array.Fill(buffer, (byte)'a');
			var written = 0;
			while (written < OversizedByteCount) {
				var chunk = Math.Min(buffer.Length, OversizedByteCount - written);
				await stream.WriteAsync(buffer.AsMemory(0, chunk));
				written += chunk;
			}
		}

		protected override bool TryComputeLength(out long length)
		{
			length = 0;
			return false;
		}
	}

	private sealed class SlowTricklingFormContent : HttpContent
	{
		private static readonly TimeSpan ByteInterval = TimeSpan.FromSeconds(1);

		private readonly TimeSpan maxDuration;

		public SlowTricklingFormContent(TimeSpan maxDuration)
		{
			this.maxDuration = maxDuration;
			Headers.ContentType = new("application/x-www-form-urlencoded");
		}

		// One byte per second: far under Kestrel's configured 240 bytes/s MinRequestBodyDataRate, so
		// a write should start failing shortly after the grace period elapses if the server enforces
		// it -- unlike a single fixed sleep, each write can observe an abort as soon as it happens.
		protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
		{
			var deadline = DateTime.UtcNow + maxDuration;
			var oneByte = "a"u8.ToArray();

			while (DateTime.UtcNow < deadline) {
				await stream.WriteAsync(oneByte);
				await stream.FlushAsync();
				await Task.Delay(ByteInterval);
			}
		}

		protected override bool TryComputeLength(out long length)
		{
			length = 0;
			return false;
		}
	}
}

/// <summary>
///     The untrusted-proxy counterpart to <see cref="TrustedProxyProductionSecuritySmokeTests" />'s
///     forwarded-proto test: proves the opposite behavior when the connecting address is not a
///     configured trusted proxy.
/// </summary>
public sealed class UntrustedProxyProductionSecuritySmokeTests : IClassFixture<UntrustedProxyProductionHostFixture>
{
	private readonly UntrustedProxyProductionHostFixture fixture;

	public UntrustedProxyProductionSecuritySmokeTests(UntrustedProxyProductionHostFixture fixture) => this.fixture = fixture;

	[Fact]
	public async Task A_forwarded_proto_from_an_untrusted_proxy_is_ignored_and_https_redirection_still_applies()
	{
		using var handler = new HttpClientHandler { AllowAutoRedirect = false };
		using var client = new HttpClient(handler);
		using var request = new HttpRequestMessage(HttpMethod.Get, $"{fixture.HttpBaseAddress}/Account/Login");
		request.Headers.Add("X-Forwarded-Proto", "https");
		request.Headers.Add("X-Forwarded-For", "203.0.113.5");

		var response = await client.SendAsync(request);

		((int)response.StatusCode).Should().BeInRange(300, 399,
			"the loopback connection is not in this fixture's KnownProxies, so the forwarded header is ignored and the plain-HTTP request is redirected to HTTPS");
	}
}
