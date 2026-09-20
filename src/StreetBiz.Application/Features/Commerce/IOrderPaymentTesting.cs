namespace StreetBiz.Application.Features.Commerce;

public interface IOrderPaymentTesting
{
    Task Fail(long orderId, CancellationToken ct);
    Task Refund(long orderId, CancellationToken ct);
}
