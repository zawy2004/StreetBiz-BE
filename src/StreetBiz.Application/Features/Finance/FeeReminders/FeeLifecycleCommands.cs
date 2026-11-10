using MediatR;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.Features.Finance.GenerateFeeSchedule;

namespace StreetBiz.Application.Features.Finance.FeeReminders;

/// <summary>
/// SYS-03 reconciliation, run by the fee sweep before reminders go out. Keeps every contract's
/// schedule in step with the contract itself, whichever workflow changed the contract:
/// <list type="bullet">
/// <item>a contract in force that nothing bills (approved without raising
/// <see cref="Common.Events.RentalContractApprovedEvent"/>) gets its schedule generated, with the
/// approving officer as the actor;</item>
/// <item>a contract that ended early (slot returned, permit revoked) has the instalments of periods
/// that never began closed, so the vendor is not chased for them.</item>
/// </list>
/// </summary>
public sealed record ReconcileFeeSchedulesCommand(DateOnly Today) : IRequest<FeeScheduleReconcileResult>;

public sealed class ReconcileFeeSchedulesCommandHandler(IFinanceRepository finance, ISender sender)
    : IRequestHandler<ReconcileFeeSchedulesCommand, FeeScheduleReconcileResult>
{
    public async Task<FeeScheduleReconcileResult> Handle(
        ReconcileFeeSchedulesCommand request, CancellationToken cancellationToken)
    {
        var generated = 0;
        var failed = 0;
        foreach (var contract in await finance.ListUnbilledContractsAsync(request.Today, cancellationToken))
        {
            try
            {
                await sender.Send(new GenerateFeeScheduleCommand(contract.ContractId, contract.ApprovedBy), cancellationToken);
                generated++;
            }
            catch (Exception exception) when (exception is DomainRuleException or ConflictException or NotFoundException)
            {
                // A zone with no price, say: that contract waits for the ward; the others go on.
                failed++;
            }
        }

        var cancelled = await finance.CancelInstalmentsAfterEarlyEndAsync(cancellationToken);
        return new FeeScheduleReconcileResult(generated, cancelled, failed);
    }
}

/// <summary>
/// FEE-01/FEE-04: settles or closes fee and penalty payments left PENDING (a MoMo page abandoned,
/// a sandbox payment never confirmed). A payment older than <see cref="StalePaymentPolicy.CheckAfter"/>
/// is checked with its provider: a reported result is applied exactly like a callback. A payment
/// no provider can be asked about is closed then; one MoMo still reports in flight is closed after
/// <see cref="StalePaymentPolicy.CloseAfter"/>. Closing is safe, since a late success is still applied.
/// </summary>
public sealed record SettleStalePaymentsCommand : IRequest<StalePaymentSweepResult>;

public sealed class SettleStalePaymentsCommandHandler(
    IFinanceRepository finance,
    IPaymentGateway paymentGateway,
    TimeProvider clock,
    IPaymentTransactionRepository? transactions = null,
    StreetBiz.Application.Features.PayoutAccounts.PayeeCheckouts? payeeCheckouts = null,
    ICommerceRepository? commerce = null) : IRequestHandler<SettleStalePaymentsCommand, StalePaymentSweepResult>
{
    public async Task<StalePaymentSweepResult> Handle(SettleStalePaymentsCommand request, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var settled = 0;
        var closed = 0;
        foreach (var payment in await finance.ListStalePaymentsAsync(now - StalePaymentPolicy.CheckAfter, cancellationToken))
        {
            if (payment.ProviderReference is { } reference)
            {
                PaymentGatewayCallback? state;
                try
                {
                    // PAYOS: asked on the payee's own channel, with its own keys.
                    var stored = payment.PayeeAccountId is not null && transactions is not null && payeeCheckouts is not null
                        ? await transactions.GetAsync(payment.TransactionId, cancellationToken)
                        : null;
                    state = stored is not null
                        ? await payeeCheckouts!.QueryAsync(stored, cancellationToken)
                        : await paymentGateway.QueryPaymentAsync(payment.Provider, reference, cancellationToken);
                }
                catch (Exception exception) when (exception is HttpRequestException
                    || (exception is TaskCanceledException && !cancellationToken.IsCancellationRequested))
                {
                    continue; // The provider is unreachable or slow: ask again on the next sweep.
                }

                if (state?.Status is not null)
                {
                    await finance.ApplyPaymentCallbackAsync(
                        new PaymentCallbackData(
                            payment.Provider,
                            state.ProviderReference ?? reference,
                            payment.IdempotencyKey,
                            state.Amount,
                            state.Status,
                            state.RawPayload ?? "{}",
                            state.SignatureValid,
                            state.ProviderEventId)
                        {
                            PayeeAccountId = payment.PayeeAccountId,
                        },
                        cancellationToken);
                    settled++;
                    continue;
                }

                // The provider answered but the order is still in flight: its page may yet be paid.
                if (state is not null && payment.CreatedAt > now - StalePaymentPolicy.CloseAfter)
                {
                    continue;
                }
            }

            if (await finance.ClosePendingPaymentAsync(payment.TransactionId, cancellationToken))
            {
                closed++;
            }
        }

        settled += await SettleStaleOrderPaymentsAsync(now, cancellationToken);
        return new StalePaymentSweepResult(settled, closed);
    }

    /// <summary>
    /// Orders paid to a household's own payOS channel whose webhook never arrived: asked with that
    /// household's keys and applied like a callback once payOS reports them final. Never closed here;
    /// an unpaid order keeps its own lifecycle.
    /// </summary>
    private async Task<int> SettleStaleOrderPaymentsAsync(DateTime now, CancellationToken cancellationToken)
    {
        if (transactions is null || payeeCheckouts is null || commerce is null)
        {
            return 0;
        }

        var settled = 0;
        foreach (var payment in await transactions.ListStalePayeeOrderPaymentsAsync(
            now - StalePaymentPolicy.CheckAfter, cancellationToken))
        {
            PaymentGatewayCallback? state;
            try
            {
                state = await payeeCheckouts.QueryAsync(payment, cancellationToken);
            }
            catch (Exception exception) when (exception is HttpRequestException
                || (exception is TaskCanceledException && !cancellationToken.IsCancellationRequested))
            {
                continue;
            }

            if (state?.Status is null)
            {
                continue;
            }

            await commerce.ApplyPaymentCallbackAsync(
                new PaymentCallbackData(
                    payment.Provider,
                    state.ProviderReference ?? payment.ProviderReference,
                    payment.IdempotencyKey,
                    state.Amount,
                    state.Status,
                    state.RawPayload ?? "{}",
                    state.SignatureValid,
                    state.ProviderEventId)
                {
                    PayeeAccountId = payment.PayeeAccountId,
                },
                cancellationToken);
            settled++;
        }

        return settled;
    }
}
