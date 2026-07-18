// Every Application-layer command/query class (JobCommands, JobQueries, InstallationCommands,
// EmployeeCommands, WorkCommands, ScheduleCommands, RateCommands, CostQueries, AuditQueries)
// routes through the one process-wide static JobTrackOperation.Source (System.Diagnostics.ActivitySource,
// keyed by JobTrackDiagnostics.ActivitySourceName). xUnit's default per-class parallelism let two
// unrelated test classes call into that shared ActivitySource concurrently -- e.g. WorkCommandsTests
// exercising WorkCommands while JobCommandsTests registered its own ActivityListener and asserted
// on the activities it captured -- and the concurrent traffic intermittently caused the listener to
// miss its own test's activity (JobCommandsTests.A_command_emits_one_bounded_activity_without_request_payload
// failing with no matching "jobs.add-child" activity found, despite the production code running
// correctly). Disabling test parallelization for this whole assembly serializes every test against
// that shared static, removing the race at its source rather than papering over it with retries.

[assembly: CollectionBehavior(DisableTestParallelization = true)]
