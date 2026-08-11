namespace JobTrack.Application;

using Abstractions;

internal static class ApplicationEnumValidation
{
	public static void ThrowIfInvalid(Priority priority, string paramName)
	{
		switch (priority) {
			case Priority.Low:
			case Priority.Medium:
			case Priority.High:
			case Priority.Urgent:
				return;
			case Priority.Unspecified:
			default:
				throw new ArgumentOutOfRangeException(paramName, priority, "Priority must be one of the persisted priority levels.");
		}
	}

	public static void ThrowIfInvalid(Achievement achievement, string paramName)
	{
		switch (achievement) {
			case Achievement.Waiting:
			case Achievement.InProgress:
			case Achievement.Success:
			case Achievement.Cancelled:
			case Achievement.Unsuccessful:
				return;
			case Achievement.None:
			default:
				throw new ArgumentOutOfRangeException(paramName, achievement, "Achievement must be one of the persisted achievement states.");
		}
	}

	public static void ThrowIfInvalid(EmployeeRole role, string paramName)
	{
		switch (role) {
			case EmployeeRole.Administrator:
			case EmployeeRole.JobManager:
			case EmployeeRole.Worker:
			case EmployeeRole.RateManager:
			case EmployeeRole.CostViewer:
			case EmployeeRole.Auditor:
			case EmployeeRole.Requester:
				return;
			case EmployeeRole.None:
			default:
				throw new ArgumentOutOfRangeException(paramName, role, "Employee role must be one of the persisted role assignments.");
		}
	}
}
