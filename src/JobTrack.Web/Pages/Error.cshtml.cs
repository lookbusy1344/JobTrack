namespace JobTrack.Web.Pages;

using System.Diagnostics;
using Abstractions;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

[ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
[IgnoreAntiforgeryToken]
public class ErrorModel : PageModel
{
	public string? RequestId { get; set; }

	public bool ShowRequestId => !string.IsNullOrEmpty(RequestId);

	/// <summary>
	///     A transient database failure (deadlock/serialization/busy, 2.2) reached the shared exception
	///     handler: the page shows a retry message rather than the generic error, since the write can
	///     succeed if repeated and nothing was wrong with the request.
	/// </summary>
	public bool IsTransientFailure { get; private set; }

	public void OnGet()
	{
		RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier;

		var handlerError = HttpContext.Features.Get<IExceptionHandlerFeature>()?.Error;
		IsTransientFailure = handlerError is TransientPersistenceException or { InnerException: TransientPersistenceException };
	}
}
