namespace JobTrack.Web.IntegrationTests;

using Abstractions;
using AwesomeAssertions;

/// <summary>
///     <see cref="PriorityLabelModel" />'s text form: abbreviated (table home) by default,
///     full name when a form/detail area sets <see cref="PriorityLabelModel.Full" />.
/// </summary>
public sealed class PriorityLabelModelTests
{
	[Theory]
	[InlineData(Priority.Low, "Low")]
	[InlineData(Priority.Medium, "Med")]
	[InlineData(Priority.High, "High")]
	[InlineData(Priority.Urgent, "Urgt")]
	public void Text_abbreviates_by_default(Priority priority, string expected)
	{
		var model = new PriorityLabelModel { Priority = priority };

		model.Text.Should().Be(expected);
	}

	[Theory]
	[InlineData(Priority.Low, "Low")]
	[InlineData(Priority.Medium, "Medium")]
	[InlineData(Priority.High, "High")]
	[InlineData(Priority.Urgent, "Urgent")]
	public void Text_spells_out_the_full_name_when_Full_is_set(Priority priority, string expected)
	{
		var model = new PriorityLabelModel { Priority = priority, Full = true };

		model.Text.Should().Be(expected);
	}
}
