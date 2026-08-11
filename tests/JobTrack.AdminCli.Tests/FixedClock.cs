namespace JobTrack.AdminCli.Tests;

using NodaTime;

/// <summary>
///     An <see cref="IClock" /> frozen at one instant, for asserting on behaviour that depends on
///     "now" — <see cref="AddScheduleCommand" /> defaulting a schedule version's effective start to
///     today in the schedule's own zone.
///     Hand-rolled to match this project's other test doubles (<see cref="FakeConsoleIO" />,
///     <see cref="FakeInstallationCommands" />) rather than taking a dependency on NodaTime.Testing
///     for a single-member interface.
/// </summary>
internal sealed class FixedClock(Instant now) : IClock
{
	public Instant GetCurrentInstant() => now;
}
