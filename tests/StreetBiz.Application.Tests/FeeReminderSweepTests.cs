using FluentAssertions;
using Moq;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Features.Finance.FeeReminders;

namespace StreetBiz.Application.Tests;

public sealed class FeeReminderSweepTests
{
    [Fact]
    public async Task Sweep_command_delegates_the_given_day_to_the_repository_unchanged()
    {
        var finance = new Mock<IFinanceRepository>();
        var requestedDay = new DateOnly(2026, 10, 2);
        finance.Setup(x => x.RunFeeReminderSweepAsync(requestedDay, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FeeReminderSweepResult(2, 3));

        var result = await new RunFeeReminderSweepCommandHandler(finance.Object)
            .Handle(new RunFeeReminderSweepCommand(requestedDay), CancellationToken.None);

        result.OverdueCount.Should().Be(2);
        result.ReminderCount.Should().Be(3);
        finance.Verify(x => x.RunFeeReminderSweepAsync(requestedDay, It.IsAny<CancellationToken>()));
    }
}
