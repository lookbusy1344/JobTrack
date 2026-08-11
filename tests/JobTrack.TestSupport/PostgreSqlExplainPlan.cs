namespace JobTrack.TestSupport;

using System.Text.Json;
using Npgsql;

/// <summary>
///     Runs <c>EXPLAIN (FORMAT JSON, ANALYZE)</c> and exposes the plan tree for
///     the §6.7 database gate's query-plan-shape assertions (impl plan §6.5,
///     §5.4's plan requirements column) -- "assert plan shape and latency ...
///     without brittle exact-cost assertions" (performance-budgets.md §0).
/// </summary>
public static class PostgreSqlExplainPlan
{
	public static async Task<JsonElement> GetPlanAsync(NpgsqlConnection connection, string query, params (string Name, object Value)[] parameters)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = $"EXPLAIN (FORMAT JSON, ANALYZE) {query}";
		foreach (var (name, value) in parameters) {
			var parameter = command.CreateParameter();
			parameter.ParameterName = name;
			parameter.Value = value;
			command.Parameters.Add(parameter);
		}

		var raw = (string)(await command.ExecuteScalarAsync())!;
		using var document = JsonDocument.Parse(raw);

		// EXPLAIN (FORMAT JSON) returns a one-element array containing one
		// object with a "Plan" key; cloning detaches the element from the
		// disposed JsonDocument so callers can keep using it afterward.
		return document.RootElement[0].GetProperty("Plan").Clone();
	}

	/// <summary>True if any node in the plan tree has the given Node Type (e.g. "Seq Scan", "Index Scan").</summary>
	public static bool ContainsNodeType(JsonElement plan, string nodeType) =>
		plan.GetProperty("Node Type").GetString() == nodeType ||
		(plan.TryGetProperty("Plans", out var children) &&
		 children.EnumerateArray().Any(child => ContainsNodeType(child, nodeType)));

	/// <summary>True if any node in the plan tree is a sequential scan of the given relation.</summary>
	public static bool ContainsSequentialScanOf(JsonElement plan, string relationName) =>
		(plan.GetProperty("Node Type").GetString() == "Seq Scan" &&
		 plan.TryGetProperty("Relation Name", out var relation) &&
		 relation.GetString() == relationName) ||
		(plan.TryGetProperty("Plans", out var children) &&
		 children.EnumerateArray().Any(child => ContainsSequentialScanOf(child, relationName)));

	/// <summary>
	///     True if any node in the plan tree (an <c>Index Scan</c> or <c>Bitmap Index Scan</c>, both of
	///     which carry an <c>"Index Name"</c> property) used the given index. Stronger than
	///     <see
	///         cref="ContainsSequentialScanOf" />
	///     : a plan can avoid a sequential scan while still using the
	///     wrong index and filtering most of its candidate rows in memory -- this pins down which index
	///     actually served the query.
	/// </summary>
	public static bool ContainsIndexScanUsing(JsonElement plan, string indexName) =>
		(plan.TryGetProperty("Index Name", out var index) && index.GetString() == indexName) ||
		(plan.TryGetProperty("Plans", out var children) &&
		 children.EnumerateArray().Any(child => ContainsIndexScanUsing(child, indexName)));

	/// <summary>True if any sort node in the plan spilled to disk rather than completing in memory.</summary>
	public static bool ContainsDiskSort(JsonElement plan) =>
		(plan.GetProperty("Node Type").GetString() == "Sort" &&
		 plan.TryGetProperty("Sort Space Type", out var sortSpaceType) &&
		 sortSpaceType.GetString() == "Disk") ||
		(plan.TryGetProperty("Plans", out var children) &&
		 children.EnumerateArray().Any(ContainsDiskSort));
}
