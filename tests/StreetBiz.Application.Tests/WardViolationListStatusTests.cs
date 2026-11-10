using FluentAssertions;
using StreetBiz.Application.Features.WardCompliance;

namespace StreetBiz.Application.Tests;

/// <summary>
/// GET /api/ward/violations?status= used to accept anything and ignore it, returning every
/// case. It now filters on the stage the list shows and refuses a stage that does not exist.
/// </summary>
public sealed class WardViolationListStatusTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("PENDING_SANCTION")]
    [InlineData("unpaid")]
    [InlineData("UNPAID, PAID,WAIVED,CANCELLED")]
    public void Known_stages_are_accepted(string? status)
    {
        new ListWardViolationsQueryValidator()
            .Validate(new ListWardViolationsQuery(status, 1))
            .IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("OPEN")]
    [InlineData("PENDING_SANCTION,DECIDED")]
    public void An_unknown_stage_is_refused_instead_of_ignored(string status)
    {
        new ListWardViolationsQueryValidator()
            .Validate(new ListWardViolationsQuery(status, 1))
            .IsValid.Should().BeFalse();
    }

    [Fact]
    public void Stages_are_trimmed_upper_cased_and_deduplicated()
    {
        WardViolationListStatuses.Parse(" unpaid ,PAID,,Unpaid")
            .Should().Equal("UNPAID", "PAID");
        WardViolationListStatuses.Parse(null).Should().BeEmpty();
    }
}
