namespace JobTrack.AdminCli;

using System.Text;

/// <summary>Real-terminal <see cref="IConsoleIO" /> implementation.</summary>
public sealed class SystemConsoleIO : IConsoleIO
{
	public void WriteLine(string message) => Console.WriteLine(message);

	public void WriteError(string message) => Console.Error.WriteLine(message);

	public string ReadLine(string prompt)
	{
		Console.Write(prompt);
		return Console.ReadLine() ?? string.Empty;
	}

	public string ReadPassword(string prompt)
	{
		Console.Write(prompt);
		var builder = new StringBuilder();

		while (true) {
			var key = Console.ReadKey(true);
			if (key.Key == ConsoleKey.Enter) {
				Console.WriteLine();
				return builder.ToString();
			}

			if (key.Key == ConsoleKey.Backspace) {
				if (builder.Length > 0) {
					builder.Length--;
				}

				continue;
			}

			if (!char.IsControl(key.KeyChar)) {
				_ = builder.Append(key.KeyChar);
			}
		}
	}
}
