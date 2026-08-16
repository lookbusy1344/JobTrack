namespace JobTrack.ArchitectureTests;

using TestSupport;

internal static class RepositorySourceFiles
{
	public static IEnumerable<string> CSharpAndRazor()
	{
		var solutionRoot = RepositoryPaths.SolutionRoot();
		foreach (var top in (string[])["src", "tests", "samples"]) {
			var directory = Path.Combine(solutionRoot, top);
			foreach (var file in Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
										  .Concat(Directory.EnumerateFiles(directory, "*.cshtml", SearchOption.AllDirectories))
										  .Where(static file => !IsGeneratedOutput(file))) {
				yield return file;
			}
		}
	}

	private static bool IsGeneratedOutput(string file)
	{
		var segments = file.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		return segments.Contains("bin") || segments.Contains("obj");
	}
}
