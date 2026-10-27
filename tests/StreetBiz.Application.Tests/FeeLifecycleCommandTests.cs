using FluentAssertions;
using MediatR;
using Moq;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.DTOs.Finance;
using StreetBiz.Application.Features.Finance.FeeReminders;
using StreetBiz.Application.Features.Finance.GenerateFeeSchedule;

namespace StreetBiz.Application.Tests;

/// <summary>SYS-03 reconciliation and the FEE-01/FEE-04 stale-payment sweep, both run by the hourly fee job.</summary>
public sealed class FeeLifecycleCommandTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 10, 27);

    private readonly Mock<IFinanceRepository> finance = new();
    private readonly Mock<ISender> sender = new();
    private readonly Mock<IPaymentGateway> gateway = new();
    private readonly FakeTime clock = new(Now);

    // ---- ReconcileFeeSchedulesCommand ----------------------------------------------------

    private ReconcileFeeSchedulesCommandHandler Reconciler() => new(finance.Object, sender.Object);

    private static FeeScheduleDto Schedule(long contractId) =>
        new(1, contractId, "NVL-01", 1, 0m, Now.UtcDateTime, []);

    [Fact]
    public async Task Generates_a_schedule_for_each_unbilled_contract_with_the_approving_officer_as_actor()
    {
        finance.Setup(f => f.ListUnbilledContractsAsync(Today, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new UnbilledContractRow(11, 2), new UnbilledContractRow(12, 3)]);
        sender.Setup(s => s.Send(It.IsAny<GenerateFeeScheduleCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((GenerateFeeScheduleCommand c, CancellationToken _) => Schedule(c.ContractId));
        finance.Setup(f => f.CancelInstalmentsAfterEarlyEndAsync(It.IsAny<CancellationToken>())).ReturnsAsync(4);

        var result = await Reconciler().Handle(new ReconcileFeeSchedulesCommand(Today), default);

        result.Should().Be(new FeeScheduleReconcileResult(2, 4, 0));
        sender.Verify(s => s.Send(new GenerateFeeScheduleCommand(11, 2), It.IsAny<CancellationToken>()), Times.Once);
        sender.Verify(s => s.Send(new GenerateFeeScheduleCommand(12, 3), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [MemberData(nameof(WaitingForTheWard))]
    public async Task A_contract_that_cannot_be_billed_yet_waits_and_does_not_stop_the_others(Exception problem)
    {
        finance.Setup(f => f.ListUnbilledContractsAsync(Today, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new UnbilledContractRow(11, 2), new UnbilledContractRow(12, 3)]);
        sender.Setup(s => s.Send(new GenerateFeeScheduleCommand(11, 2), It.IsAny<CancellationToken>())).ThrowsAsync(problem);
        sender.Setup(s => s.Send(new GenerateFeeScheduleCommand(12, 3), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Schedule(12));

        var result = await Reconciler().Handle(new ReconcileFeeSchedulesCommand(Today), default);

        result.GeneratedCount.Should().Be(1);
        result.FailedCount.Should().Be(1);
    }

    public static IEnumerable<object[]> WaitingForTheWard() =>
    [
        [new DomainRuleException("The zone has no price.")],
        [new ConflictException("Already billed.")],
        [new NotFoundException("Contract vanished.")],
    ];

    [Fact]
    public async Task An_unexpected_error_is_not_swallowed()
    {
        finance.Setup(f => f.ListUnbilledContractsAsync(Today, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new UnbilledContractRow(11, 2)]);
        sender.Setup(s => s.Send(It.IsAny<GenerateFeeScheduleCommand>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("database is down"));

        var act = () => Reconciler().Handle(new ReconcileFeeSchedulesCommand(Today), default);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task With_nothing_to_bill_it_still_closes_the_instalments_of_contracts_that_ended_early()
    {
        finance.Setup(f => f.ListUnbilledContractsAsync(Today, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        finance.Setup(f => f.CancelInstalmentsAfterEarlyEndAsync(It.IsAny<CancellationToken>())).ReturnsAsync(3);

        var result = await Reconciler().Handle(new ReconcileFeeSchedulesCommand(Today), default);

        result.Should().Be(new FeeScheduleReconcileResult(0, 3, 0));
        sender.VerifyNoOtherCalls();
    }

    // ---- SettleStalePaymentsCommand -------------------------------------------------------

    private SettleStalePaymentsCommandHandler Settler() => new(finance.Object, gateway.Object, clock);

    private static StalePaymentRow Stale(long id, string? reference, TimeSpan age) =>
        new(id, "MOMO", reference, $"idem-{id}", Now.UtcDateTime - age);

    private void Stales(params StalePaymentRow[] rows) =>
        finance.Setup(f => f.ListStalePaymentsAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(rows);

    [Fact]
    public async Task Looks_for_payments_left_open_longer_than_the_check_delay()
    {
        Stales();

        await Settler().Handle(new SettleStalePaymentsCommand(), default);

        finance.Verify(
            f => f.ListStalePaymentsAsync(Now.UtcDateTime - StalePaymentPolicy.CheckAfter, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task A_payment_the_provider_reports_final_is_applied_like_a_callback()
    {
        Stales(Stale(1, "momo-ref-1", TimeSpan.FromHours(1)));
        gateway.Setup(g => g.QueryPaymentAsync("MOMO", "momo-ref-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PaymentGatewayCallback("momo-ref-1b", null, 990_000m, "SUCCESS", true, "{\"resultCode\":0}"));

        var result = await Settler().Handle(new SettleStalePaymentsCommand(), default);

        result.Should().Be(new StalePaymentSweepResult(1, 0));
        finance.Verify(f => f.ApplyPaymentCallbackAsync(
            new PaymentCallbackData("MOMO", "momo-ref-1b", "idem-1", 990_000m, "SUCCESS", "{\"resultCode\":0}", true),
            It.IsAny<CancellationToken>()), Times.Once);
        finance.Verify(f => f.ClosePendingPaymentAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_missing_raw_payload_is_stored_as_an_empty_json_object_and_the_old_reference_is_kept()
    {
        Stales(Stale(1, "momo-ref-1", TimeSpan.FromHours(1)));
        gateway.Setup(g => g.QueryPaymentAsync("MOMO", "momo-ref-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PaymentGatewayCallback(null, null, 500_000m, "FAILED", true));

        await Settler().Handle(new SettleStalePaymentsCommand(), default);

        finance.Verify(f => f.ApplyPaymentCallbackAsync(
            It.Is<PaymentCallbackData>(c => c.ProviderReference == "momo-ref-1" && c.RawPayload == "{}" && c.Status == "FAILED"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_payment_no_provider_can_be_asked_about_is_closed()
    {
        Stales(Stale(1, null, TimeSpan.FromHours(1)), Stale(2, "ref-2", TimeSpan.FromHours(1)));
        gateway.Setup(g => g.QueryPaymentAsync("MOMO", "ref-2", It.IsAny<CancellationToken>()))
            .ReturnsAsync((PaymentGatewayCallback?)null);
        finance.Setup(f => f.ClosePendingPaymentAsync(It.IsAny<long>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var result = await Settler().Handle(new SettleStalePaymentsCommand(), default);

        result.Should().Be(new StalePaymentSweepResult(0, 2));
        gateway.Verify(g => g.QueryPaymentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_payment_still_in_flight_at_the_provider_is_left_alone_until_it_is_a_day_old()
    {
        Stales(
            Stale(1, "young", TimeSpan.FromHours(2)),
            Stale(2, "old", TimeSpan.FromHours(25)));
        gateway.Setup(g => g.QueryPaymentAsync("MOMO", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PaymentGatewayCallback("x", null, null, null, true));
        finance.Setup(f => f.ClosePendingPaymentAsync(2, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var result = await Settler().Handle(new SettleStalePaymentsCommand(), default);

        result.Should().Be(new StalePaymentSweepResult(0, 1));
        finance.Verify(f => f.ClosePendingPaymentAsync(1, It.IsAny<CancellationToken>()), Times.Never);
        finance.Verify(f => f.ClosePendingPaymentAsync(2, It.IsAny<CancellationToken>()), Times.Once);
        finance.Verify(f => f.ApplyPaymentCallbackAsync(It.IsAny<PaymentCallbackData>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task An_unreachable_provider_is_asked_again_on_the_next_sweep()
    {
        Stales(Stale(1, "ref-1", TimeSpan.FromHours(30)), Stale(2, "ref-2", TimeSpan.FromHours(1)));
        gateway.Setup(g => g.QueryPaymentAsync("MOMO", "ref-1", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("MoMo is down"));
        gateway.Setup(g => g.QueryPaymentAsync("MOMO", "ref-2", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TaskCanceledException("timeout"));

        var result = await Settler().Handle(new SettleStalePaymentsCommand(), default);

        result.Should().Be(new StalePaymentSweepResult(0, 0));
        finance.Verify(f => f.ClosePendingPaymentAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
        finance.Verify(f => f.ApplyPaymentCallbackAsync(It.IsAny<PaymentCallbackData>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_cancelled_sweep_is_not_mistaken_for_a_slow_provider()
    {
        Stales(Stale(1, "ref-1", TimeSpan.FromHours(1)));
        using var cts = new CancellationTokenSource();
        gateway.Setup(g => g.QueryPaymentAsync("MOMO", "ref-1", It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                cts.Cancel();
                throw new TaskCanceledException("shutting down", null, cts.Token);
            });

        var act = () => Settler().Handle(new SettleStalePaymentsCommand(), cts.Token);

        await act.Should().ThrowAsync<TaskCanceledException>();
    }

    [Fact]
    public async Task A_payment_somebody_settled_meanwhile_is_not_counted_as_closed()
    {
        Stales(Stale(1, null, TimeSpan.FromHours(1)));
        finance.Setup(f => f.ClosePendingPaymentAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var result = await Settler().Handle(new SettleStalePaymentsCommand(), default);

        result.Should().Be(new StalePaymentSweepResult(0, 0));
    }

    private sealed class FakeTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
