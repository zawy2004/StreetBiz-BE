using FluentAssertions;
using StreetBiz.Application.Features.WardCompliance;

namespace StreetBiz.Application.Tests;

/// <summary>WARD-06: a request for more information, like a rejection, always carries the officer's reason.</summary>
public sealed class WardEnrollmentDecisionValidationTests
{
    private static DecideWardEnrollmentCommand Command(string decision, string reason) =>
        new(7, new WardEnrollmentDecision(decision, reason, "SUBMITTED"));

    [Theory]
    [InlineData("MORE_INFO", "", false)]
    [InlineData("MORE_INFO", "   ", false)]
    [InlineData("REJECT", "", false)]
    [InlineData("MORE_INFO", "Bổ sung ảnh CCCD mặt sau", true)]
    [InlineData("APPROVE", "Đạt yêu cầu", true)]
    [InlineData("SOMETHING_ELSE", "Lý do", false)]
    public void Every_decision_needs_a_known_verb_and_a_reason(string decision, string reason, bool valid) =>
        new DecideWardEnrollmentCommandValidator().Validate(Command(decision, reason)).IsValid.Should().Be(valid);

    [Fact]
    public void The_reason_is_limited_to_500_characters()
    {
        var validator = new DecideWardEnrollmentCommandValidator();

        validator.Validate(Command("MORE_INFO", new string('x', 500))).IsValid.Should().BeTrue();
        validator.Validate(Command("MORE_INFO", new string('x', 501))).IsValid.Should().BeFalse();
    }
}
