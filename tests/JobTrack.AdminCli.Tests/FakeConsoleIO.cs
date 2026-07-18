namespace JobTrack.AdminCli.Tests;

/// <summary>
///     A scripted <see cref="IConsoleIO" /> for command-logic tests: pre-loaded answers for
///     <see cref="ReadLine" />/<see cref="ReadPassword" /> calls (consumed in order, one queue per
///     prompt), and every <see cref="WriteLine" />/<see cref="WriteError" /> call recorded for
///     assertions (e.g. "the password never appears in any output").
/// </summary>
internal sealed class FakeConsoleIO : IConsoleIO
{
	private readonly Queue<string> _lineAnswers;
	private readonly Queue<string> _passwordAnswers;

	public FakeConsoleIO(IEnumerable<string> lineAnswers, IEnumerable<string> passwordAnswers)
	{
		_lineAnswers = new(lineAnswers);
		_passwordAnswers = new(passwordAnswers);
	}

	public List<string> Lines { get; } = [];

	public List<string> Errors { get; } = [];

	public List<string> Prompts { get; } = [];

	public void WriteLine(string message) => Lines.Add(message);

	public void WriteError(string message) => Errors.Add(message);

	public string ReadLine(string prompt)
	{
		Prompts.Add(prompt);
		return _lineAnswers.Count > 0 ? _lineAnswers.Dequeue() : string.Empty;
	}

	public string ReadPassword(string prompt)
	{
		Prompts.Add(prompt);
		return _passwordAnswers.Count > 0 ? _passwordAnswers.Dequeue() : string.Empty;
	}
}
