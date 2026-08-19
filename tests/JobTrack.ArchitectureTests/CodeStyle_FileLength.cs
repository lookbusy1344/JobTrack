namespace JobTrack.ArchitectureTests;

using AwesomeAssertions;
using TestSupport;

/// <summary>
///     Hard physical-line ceilings for authored C# and Razor files. Comments and blank lines count:
///     they still contribute to the amount of source an engineer must navigate. Production and sample
///     C# carry the base ceiling; Razor a tighter one because its declarative markup is denser; test
///     specifications a looser one, since one big linear fixture is often clearer whole than split.
///     There is no exception mechanism — an overlong file is divided along a cohesive type, capability,
///     or scenario boundary.
/// </summary>
public sealed class CodeStyle_FileLength
{
	[Fact]
	public void Repository_CSharp_and_Razor_files_do_not_exceed_the_line_count_ceiling()
	{
		var solutionRoot = RepositoryPaths.SolutionRoot();
		var violations = RepositorySourceFiles.CSharpAndRazor()
			.Select(file => FileLengthGuard.FindViolation(solutionRoot, file, File.ReadLines(file).Count()))
			.Where(static violation => violation is not null)
			.ToArray();

		violations.Should().BeEmpty(
			"overlong source files should be divided along cohesive type, capability, or scenario boundaries:{0}{1}",
			Environment.NewLine,
			string.Join(Environment.NewLine, violations));
	}

	[Theory]
	[InlineData("src/Example.cs", FileLengthGuard.MaxProductionCSharpLineCount)]
	[InlineData("samples/Example.cs", FileLengthGuard.MaxProductionCSharpLineCount)]
	[InlineData("src/JobTrack.Web/Pages/Example.cshtml", FileLengthGuard.MaxRazorLineCount)]
	[InlineData("tests/Example.cs", FileLengthGuard.MaxTestCSharpLineCount)]
	public void File_at_its_ceiling_is_not_a_violation(string relativePath, int lineCount) =>
		FileLengthGuard.FindViolation("/repo", Path.Combine("/repo", relativePath), lineCount).Should().BeNull();

	[Theory]
	[InlineData("src/Example.cs", FileLengthGuard.MaxProductionCSharpLineCount)]
	[InlineData("samples/Example.cs", FileLengthGuard.MaxProductionCSharpLineCount)]
	[InlineData("src/JobTrack.Web/Pages/Example.cshtml", FileLengthGuard.MaxRazorLineCount)]
	[InlineData("tests/Example.cs", FileLengthGuard.MaxTestCSharpLineCount)]
	public void File_over_its_ceiling_is_a_violation(string relativePath, int maximum)
	{
		var violation = FileLengthGuard.FindViolation("/repo", Path.Combine("/repo", relativePath), maximum + 1);

		violation.Should().Contain(relativePath.Replace(Path.DirectorySeparatorChar, '/'))
			.And.Contain($"{maximum + 1} lines")
			.And.Contain($"maximum is {maximum}");
	}

	[Fact]
	public void Test_CSharp_file_carries_the_looser_test_ceiling() =>
		FileLengthGuard.FindViolation("/repo", "/repo/tests/Example.cs", FileLengthGuard.MaxTestCSharpLineCount)
			.Should().BeNull();
}

internal static class FileLengthGuard
{
	public const int MaxProductionCSharpLineCount = 1_000;
	public const int MaxRazorLineCount = 500;
	public const int MaxTestCSharpLineCount = 2_000;

	public static string? FindViolation(string solutionRoot, string fileName, int lineCount)
	{
		var relativePath = Path.GetRelativePath(solutionRoot, fileName)
			.Replace(Path.DirectorySeparatorChar, '/')
			.Replace(Path.AltDirectorySeparatorChar, '/');
		var maximum = MaximumFor(relativePath);

		if (!maximum.HasValue || lineCount <= maximum.Value) {
			return null;
		}

		return $"{relativePath}: {lineCount} lines; maximum is {maximum.Value}";
	}

	private static int? MaximumFor(string relativePath)
	{
		var topLevelDirectory = relativePath.Split('/', 2)[0];
		if (topLevelDirectory == "tests") {
			return MaxTestCSharpLineCount;
		}

		if (topLevelDirectory is not ("src" or "samples")) {
			throw new ArgumentException("Source file must be under src, tests, or samples.", nameof(relativePath));
		}

		return relativePath.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase)
			? MaxRazorLineCount
			: MaxProductionCSharpLineCount;
	}
}
