namespace JobTrack.Web;

using System.Security.Claims;
using Abstractions;
using Identity;
using Microsoft.AspNetCore.Identity;

internal static class UserManagerExtensions
{
	public static async Task<AppUserId?> GetAppUserIdAsync(
		this UserManager<JobTrackIdentityUser> userManager, ClaimsPrincipal principal)
	{
		var user = await userManager.GetUserAsync(principal);
		return user?.AppUserId;
	}
}
