namespace JobTrack.Database;

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

/// <summary>
///     Loads the ordered set of schema-version scripts for one provider from
///     <c>database/{provider}/schema-versions/</c> (ADR 0011 naming convention:
///     <c>NNNN_short-description.sql</c>, zero-padded, strictly increasing).
/// </summary>
public static partial class SchemaVersionScriptLoader
{
	[GeneratedRegex(@"^(?<version>\d{4})_(?<description>[a-z0-9\-]+)\.sql$")]
	private static partial Regex FileNamePattern();

	public static IReadOnlyList<SchemaVersionScript> Load(string scriptsRootDirectory)
	{
		var scripts = new List<SchemaVersionScript>();

		foreach (var filePath in Directory.EnumerateFiles(scriptsRootDirectory, "*.sql")) {
			var fileName = Path.GetFileName(filePath);
			var match = FileNamePattern().Match(fileName);

			if (!match.Success) {
				throw new SchemaDeploymentException(
					$"Schema-version script file name '{fileName}' does not match the required 'NNNN_short-description.sql' pattern.");
			}

			var version = int.Parse(match.Groups["version"].Value, CultureInfo.InvariantCulture);
			var sql = File.ReadAllText(filePath);

			scripts.Add(new() {
				Version = version,
				Description = match.Groups["description"].Value,
				FilePath = filePath,
				Sql = sql,
				Checksum = ComputeChecksum(sql),
			});
		}

		return [.. scripts.OrderBy(script => script.Version)];
	}

	private static string ComputeChecksum(string sql)
	{
		var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sql));
		return Convert.ToHexStringLower(hash);
	}
}
