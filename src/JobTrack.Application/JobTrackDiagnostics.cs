namespace JobTrack.Application;

/// <summary>Stable diagnostic identifiers emitted by the configured JobTrack client.</summary>
public static class JobTrackDiagnostics
{
	/// <summary>The <see cref="System.Diagnostics.ActivitySource" /> name for library operations.</summary>
	public const string ActivitySourceName = "JobTrack.Application";

	/// <summary>Structured-telemetry tag names emitted by JobTrack activities.</summary>
	internal static class Tags
	{
		/// <summary>The acting <see cref="Abstractions.AppUserId" />.</summary>
		public const string ActorId = "jobtrack.actor_id";

		/// <summary>The caller-supplied correlation identifier.</summary>
		public const string CorrelationId = "jobtrack.correlation_id";

		/// <summary>A target employee identifier.</summary>
		public const string TargetUserId = "jobtrack.target.user_id";

		/// <summary>A target job-node identifier.</summary>
		public const string TargetNodeId = "jobtrack.target.node_id";
	}
}
