namespace JobTrack.Domain.Tests;

using Abstractions;
using AwesomeAssertions;
using Domain.Hierarchy;
using Domain.Rates;
using Domain.Schedules;

public sealed class DefaultValueTests
{
	public static TheoryData<Type, Enum> EnumDefaults => new() {
		{ typeof(Achievement), Achievement.None },
		{ typeof(EmployeeRole), EmployeeRole.None },
		{ typeof(NodeKind), NodeKind.None },
		{ typeof(Priority), Priority.Unspecified },
		{ typeof(RequesterStatus), RequesterStatus.None },
		{ typeof(ScheduleExceptionEffect), ScheduleExceptionEffect.None },
		{ typeof(RateSource), RateSource.None },
	};

	[Theory]
	[MemberData(nameof(EnumDefaults))]
	public void Enum_defaults_have_an_explicit_zero_value(Type enumType, Enum expected)
	{
		var actual = (Enum)Activator.CreateInstance(enumType)!;

		actual.Should().Be(expected);
	}

	[Theory]
	[InlineData(typeof(AppUserId))]
	[InlineData(typeof(AuditEventId))]
	[InlineData(typeof(DepartmentId))]
	[InlineData(typeof(JobNodeId))]
	[InlineData(typeof(JobRequestNoteId))]
	[InlineData(typeof(NodeRateOverrideId))]
	[InlineData(typeof(PersonalAccessTokenId))]
	[InlineData(typeof(RequestHoldingAreaId))]
	[InlineData(typeof(ScheduleExceptionId))]
	[InlineData(typeof(ScheduleVersionId))]
	[InlineData(typeof(UserCostRateId))]
	[InlineData(typeof(WorkSessionId))]
	public void Identifier_defaults_are_explicitly_unassigned(Type type)
	{
		var value = Activator.CreateInstance(type)!;
		var isUnspecified = (bool)type.GetProperty("IsUnspecified")!.GetValue(value)!;

		isUnspecified.Should().BeTrue();
	}

	[Fact]
	public void Default_achievement_is_not_completed() => AchievementTransitions.IsCompletedState(default).Should().BeFalse();
}
