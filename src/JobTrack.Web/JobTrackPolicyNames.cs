namespace JobTrack.Web;

internal static class JobTrackPolicyNames
{
	public const string AnyEmployee = nameof(AnyEmployee);
	public const string JobWorkflow = nameof(JobWorkflow);
	public const string ScheduleAdministration = nameof(ScheduleAdministration);
	public const string RateAdministration = nameof(RateAdministration);
	public const string RateRead = nameof(RateRead);
	public const string RateWrite = nameof(RateWrite);
	public const string AuditSearch = nameof(AuditSearch);
	public const string RequesterAccess = nameof(RequesterAccess);
	public const string RequestDetailAccess = nameof(RequestDetailAccess);
	public const string AnyAuthenticatedUser = nameof(AnyAuthenticatedUser);
}
