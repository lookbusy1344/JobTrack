namespace JobTrack.Abstractions;

using System.ComponentModel.DataAnnotations;
using System.Globalization;

/// <summary>
///     A <see cref="ValidationAttribute" /> bounding a string property by Unicode code point count
///     (<see cref="TextLength.CodePointCount" />), not <see cref="string.Length" /> UTF-16 code
///     units -- the correctness gap <see cref="MaxLengthAttribute" /> has for surrogate-pair text
///     (most emoji). Use this in place of <see cref="MaxLengthAttribute" /> on any user-facing text
///     field with a documented maximum length.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter)]
public sealed class MaxCodePointLengthAttribute(int maximumLength)
	: ValidationAttribute(() => "The field {0} must be a string with a maximum length of '{1}' Unicode code points.")
{
	/// <summary>The maximum accepted number of Unicode code points.</summary>
	public int MaximumLength { get; } = maximumLength;

	/// <inheritdoc />
	public override bool IsValid(object? value) => value is not string text || TextLength.CodePointCount(text) <= MaximumLength;

	/// <inheritdoc />
	public override string FormatErrorMessage(string name) => string.Format(CultureInfo.CurrentCulture, ErrorMessageString, name, MaximumLength);
}
