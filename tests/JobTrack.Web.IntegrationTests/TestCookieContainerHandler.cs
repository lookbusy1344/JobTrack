namespace JobTrack.Web.IntegrationTests;

using System.Net;

internal sealed class TestCookieContainerHandler(HttpMessageHandler innerHandler) : DelegatingHandler(innerHandler)
{
	private readonly CookieContainer cookies = new();

	internal IReadOnlyList<TestCookieSnapshot> SuspendCookiesContaining(Uri uri, string nameFragment)
	{
		var matches = cookies.GetCookies(uri)
							 .Where(cookie => cookie.Name.Contains(nameFragment, StringComparison.OrdinalIgnoreCase))
							 .Select(static cookie => new TestCookieSnapshot(cookie.Name, cookie.Value))
							 .ToArray();

		foreach (var cookie in cookies.GetCookies(uri)
									  .Where(cookie => cookie.Name.Contains(nameFragment, StringComparison.OrdinalIgnoreCase))) {
			cookie.Expired = true;
		}

		return matches;
	}

	protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
	{
		var requestUri = request.RequestUri ?? throw new InvalidOperationException("The request URI is required.");
		var cookieHeader = cookies.GetCookieHeader(requestUri);
		if (cookieHeader.Length > 0) {
			request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
		}

		var response = await base.SendAsync(request, cancellationToken);
		if (response.Headers.TryGetValues("Set-Cookie", out var setCookieHeaders)) {
			foreach (var setCookieHeader in setCookieHeaders) {
				cookies.SetCookies(requestUri, setCookieHeader);
			}
		}

		return response;
	}
}

internal readonly record struct TestCookieSnapshot(
	string Name,
	string Value);
