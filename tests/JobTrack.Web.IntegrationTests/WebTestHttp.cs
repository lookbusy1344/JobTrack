namespace JobTrack.Web.IntegrationTests;

using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

internal static partial class WebTestHttp
{
	public const string AntiforgeryHeaderName = "X-CSRF-TOKEN";
	public const string KnownPassword = "Correct-Horse-Battery-42!";

	public static async Task<HttpResponseMessage> GetAuthenticatedAsync(this HttpClient client, string path, string authCookie)
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, path);
		request.Headers.Add("Cookie", authCookie);
		return await client.SendAsync(request);
	}

	public static async Task<HttpResponseMessage> FollowRedirectAsync(
		this HttpClient client, HttpResponseMessage response, string authCookie)
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, response.Headers.Location);
		var responseCookies = WebTestHttp.ExtractSetCookiePairs(response);
		request.Headers.Add("Cookie", string.Join("; ", new[] { authCookie }.Concat(responseCookies)));
		return await client.SendAsync(request);
	}

	public static async Task<HttpResponseMessage> GetWithBearerAsync(this HttpClient client, string path, string token)
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, path);
		request.Headers.Authorization = new("Bearer", token);
		return await client.SendAsync(request);
	}

	public static async Task<HttpResponseMessage> PostJsonWithoutAntiforgeryAsync(
		this HttpClient client, string path, string authCookie, string jsonBody)
	{
		using var request = new HttpRequestMessage(HttpMethod.Post, path);
		request.Headers.Add("Cookie", authCookie);
		request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
		return await client.SendAsync(request);
	}

	public static async Task<HttpResponseMessage> PostJsonAsync(
		this HttpClient client, string path, string authCookie, string antiforgeryCookie, string antiforgeryToken, string jsonBody)
	{
		using var request = new HttpRequestMessage(HttpMethod.Post, path);
		request.Headers.Add("Cookie", $"{authCookie}; {antiforgeryCookie}");
		request.Headers.Add(AntiforgeryHeaderName, antiforgeryToken);
		request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
		return await client.SendAsync(request);
	}

	public static async Task<(string CookieHeader, string Token)> GetAntiforgeryTokenAsync(
		this HttpClient client, string authCookie)
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, "/api/antiforgery-token");
		request.Headers.Add("Cookie", authCookie);
		var response = await client.SendAsync(request);
		var body = await response.Content.ReadAsStringAsync();
		var antiforgeryCookie = WebTestHttp.FindSetCookie(response, "Antiforgery")
			?? throw new InvalidOperationException("No antiforgery cookie in token response.");
		var token = JsonDocument.Parse(body).RootElement.GetProperty("token").GetString()
			?? throw new InvalidOperationException("No antiforgery token in token response.");

		return (WebTestHttp.ExtractCookiePair(antiforgeryCookie), token);
	}

	public static async Task<HttpResponseMessage> PostLoginAsync(
		this HttpClient client, string userName, string password)
	{
		var (antiforgeryCookie, token) = await client.GetLoginFormAsync();

		using var request = new HttpRequestMessage(HttpMethod.Post, "/Account/Login");
		request.Headers.Add("Cookie", antiforgeryCookie);
		request.Content = new FormUrlEncodedContent(new Dictionary<string, string> {
			["Input.UserName"] = userName,
			["Input.Password"] = password,
			["__RequestVerificationToken"] = token,
		});

		return await client.SendAsync(request);
	}

	public static async Task<string> SignInAsync(this HttpClient client, string userName, string password)
	{
		var response = await client.PostLoginAsync(userName, password);
		var authCookie = WebTestHttp.FindSetCookie(response, "Identity.Application")
			?? throw new InvalidOperationException("Sign-in did not set the authentication cookie.");

		return WebTestHttp.ExtractCookiePair(authCookie);
	}

	public static Task<string> SignInAsync(this HttpClient client, string userName) =>
		client.SignInAsync(userName, KnownPassword);

	public static async Task<(string CookieHeader, string Token)> GetLoginFormAsync(this HttpClient client)
	{
		var response = await client.GetAsync("/Account/Login");
		var body = await response.Content.ReadAsStringAsync();
		var antiforgeryCookie = WebTestHttp.FindSetCookie(response, "Antiforgery")
			?? throw new InvalidOperationException("No antiforgery cookie in login page response.");
		var token = ExtractAntiforgeryToken(body);

		return (WebTestHttp.ExtractCookiePair(antiforgeryCookie), token);
	}

	public static async Task<(string CookieHeader, string Token)> ExtractFormAsync(
		HttpResponseMessage response, string previousAntiforgeryCookie)
	{
		var body = await response.Content.ReadAsStringAsync();
		var setCookieHeader = FindSetCookie(response, "Antiforgery");
		var cookie = setCookieHeader is not null ? ExtractCookiePair(setCookieHeader) : previousAntiforgeryCookie;
		return (cookie, ExtractAntiforgeryToken(body));
	}

	public static string ExtractAntiforgeryToken(string body) =>
		AntiforgeryTokenPattern().Match(body) is { Success: true } match
			? match.Groups["token"].Value
			: throw new InvalidOperationException("No antiforgery token in response body.");

	public static string? FindSetCookie(HttpResponseMessage response, string nameContains) =>
		response.Headers.TryGetValues("Set-Cookie", out var values)
			? values.FirstOrDefault(value => value.Contains(nameContains, StringComparison.OrdinalIgnoreCase))
			: null;

	public static string ExtractCookiePair(string setCookieHeader) => setCookieHeader.Split(';')[0];

	public static IEnumerable<string> ExtractSetCookiePairs(HttpResponseMessage response) =>
		response.Headers.TryGetValues("Set-Cookie", out var values) ? values.Select(ExtractCookiePair) : [];

	[GeneratedRegex("name=\"__RequestVerificationToken\"[^>]*value=\"(?<token>[^\"]+)\"")]
	private static partial Regex AntiforgeryTokenPattern();
}
