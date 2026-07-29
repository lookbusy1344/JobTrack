namespace JobTrack.Web;

using Abstractions;
using Domain.Costing;

/// <summary>Formats the paired monetary and allocated-time values of an actual labour cost.</summary>
internal static class CostDisplay
{
	internal static string Format(Money cost, AllocatedDuration allocatedDuration) =>
		$"{cost} / {allocatedDuration}";
}
