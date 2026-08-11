namespace JobTrack.Application.Tests;

using Microsoft.Extensions.Logging;

/// <summary>Captures every formatted log message for assertion, in call order. Test-only.</summary>
internal sealed class CapturingLogger<T> : ILogger<T>
{
	public List<CapturedLogEntry> Entries { get; } = [];

	public List<string> Messages { get; } = [];

	public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

	public bool IsEnabled(LogLevel logLevel) => true;

	public void Log<TState>(
		LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
	{
		var message = formatter(state, exception);
		var properties = state is IEnumerable<KeyValuePair<string, object?>> structuredState
			? structuredState.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal)
			: new(StringComparer.Ordinal);
		Entries.Add(new() { Level = logLevel, EventId = eventId, Message = message, Properties = properties });
		Messages.Add(message);
	}
}

internal sealed class CapturedLogEntry
{
	public required LogLevel Level { get; init; }

	public required EventId EventId { get; init; }

	public required string Message { get; init; }

	public required IReadOnlyDictionary<string, object?> Properties { get; init; }
}
