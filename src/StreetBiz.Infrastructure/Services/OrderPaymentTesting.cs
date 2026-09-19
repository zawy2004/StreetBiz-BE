using System.Data;
using Microsoft.EntityFrameworkCore;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.Features.Commerce;
using StreetBiz.Infrastructure.Persistence;
using StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

namespace StreetBiz.Infrastructure.Services;

public sealed class OrderPaymentTesting(StreetBizDbContext db, ICustomerContext customers, TimeProvider clock) : IOrderPaymentTesting
{
    public Task Fail(long orderId, CancellationToken ct) => Mutate(orderId, false, ct);
    public Task Refund(long orderId, CancellationToken ct) => Mutate(orderId, true, ct);

    private async Task Mutate(long orderId, bool refund, CancellationToken ct)
    {
        var customer = await customers.RequireCustomerUserIdAsync(ct);
        await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var order = await db.Orders.SingleOrDefaultAsync(x => x.order_id == orderId && x.customer_user_id == customer, ct)
                ?? throw new NotFoundException("Không tìm thấy đơn hàng.");
            var payment = await db.PaymentTransactions.Where(x => x.order_id == orderId && x.payment_purpose == "ORDER")
                .OrderByDescending(x => x.transaction_id).FirstOrDefaultAsync(ct)
                ?? throw new NotFoundException("Không tìm thấy thanh toán.");
            var now = clock.GetUtcNow().UtcDateTime;
            if (refund)
            {
                if (payment.provider_reference?.StartsWith("SANDBOX-", StringComparison.Ordinal) != true || payment.transaction_status != "SUCCESS")
                    throw new ConflictException("Chỉ mô phỏng hoàn tiền cho giao dịch sandbox đã thanh toán.");
                var refunds = await db.RefundTransactions.Where(x => x.order_id == orderId &&
                    x.payment_transaction_id == payment.transaction_id && x.refund_status == "PENDING").ToListAsync(ct);
                foreach (var row in refunds)
                {
                    row.refund_status = "SUCCESS";
                    row.provider_refund_reference = $"SANDBOX-REFUND-{row.refund_id}";
                    row.completed_at = now;
                }
                if (refunds.Count == 0 && !await db.RefundTransactions.AnyAsync(x => x.order_id == orderId && x.refund_status == "SUCCESS", ct))
                    throw new ConflictException("Chưa có yêu cầu hoàn tiền được duyệt.");
            }
            else
            {
                if (order.order_status != "PENDING_PAYMENT" || payment.transaction_status == "SUCCESS" ||
                    (payment.provider_reference != null && !payment.provider_reference.StartsWith("SANDBOX-", StringComparison.Ordinal)))
                    throw new ConflictException("Đơn không còn chờ thanh toán sandbox.");
                payment.transaction_status = "FAILED";
                payment.provider_reference = $"SANDBOX-FAILED-{payment.transaction_id}";
                payment.callback_received_at = now;
            }
            db.AuditLogs.Add(new AuditLog { actor_user_id = customer, entity_type = "Order", entity_id = orderId,
                action = refund ? "ORD_SANDBOX_REFUND" : "ORD_SANDBOX_FAILED", created_at = now });
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        });
    }
}
