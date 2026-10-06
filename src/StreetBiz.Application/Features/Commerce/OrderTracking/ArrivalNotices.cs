using FluentValidation;
using MediatR;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.DTOs.Commerce;

namespace StreetBiz.Application.Features.Commerce.OrderTracking;

/// <summary>
/// "Tôi đang đến": the customer tells the stall they are on the way, so the food is finished as
/// they arrive rather than going cold, the way curbside pickup apps do it. Pickup-only orders have
/// no courier to report progress; the customer is the courier.
///
/// Only while the order is still to be collected. Repeated taps inside
/// <see cref="ResendAfter"/> return the notice already sent instead of pinging the stall again.
/// </summary>
public sealed record AnnounceArrivalCommand(long OrderId, int? EtaMinutes) : IRequest<ArrivalNoticeDto>;

public sealed class AnnounceArrivalCommandValidator : AbstractValidator<AnnounceArrivalCommand>
{
    public AnnounceArrivalCommandValidator()
    {
        RuleFor(x => x.OrderId).GreaterThan(0);
        RuleFor(x => x.EtaMinutes).InclusiveBetween(1, 180).When(x => x.EtaMinutes is not null);
    }
}

public sealed class AnnounceArrivalCommandHandler(
    ICustomerContext customerContext,
    IOrderTrackingRepository tracking,
    IDateTimeProvider clock) : IRequestHandler<AnnounceArrivalCommand, ArrivalNoticeDto>
{
    public static readonly TimeSpan ResendAfter = TimeSpan.FromMinutes(2);

    public async Task<ArrivalNoticeDto> Handle(AnnounceArrivalCommand request, CancellationToken cancellationToken)
    {
        var customerUserId = await customerContext.RequireCustomerUserIdAsync(cancellationToken);
        var order = await tracking.GetArrivalTargetAsync(customerUserId, request.OrderId, cancellationToken)
            ?? throw new NotFoundException(CommerceMessages.OrderNotFound);
        if (!IsCollectable(order.OrderStatus))
        {
            throw new DomainRuleException(ArrivalMessages.NotCollectable);
        }

        var now = clock.UtcNow;
        if (order.LastNotifiedAt is { } last && now - last < ResendAfter)
        {
            return new ArrivalNoticeDto(order.OrderId, order.OrderStatus, DateTime.SpecifyKind(last, DateTimeKind.Utc), AlreadySent: true);
        }

        await tracking.RecordArrivalAsync(
            order.OrderId,
            order.VendorUserId,
            $"Khách đang đến lấy đơn #{order.OrderCode}",
            ArrivalMessages.Body(request.EtaMinutes),
            now,
            cancellationToken);
        return new ArrivalNoticeDto(order.OrderId, order.OrderStatus, DateTime.SpecifyKind(now, DateTimeKind.Utc), AlreadySent: false);
    }

    public static bool IsCollectable(string status) =>
        status is OrderStatuses.Placed or OrderStatuses.Accepted or OrderStatuses.Preparing
            or OrderStatuses.ReadyForPickup;
}

/// <summary>The seller's board: customers on their way, for orders still to hand over.</summary>
public sealed record ListVendorArrivalsQuery : IRequest<IReadOnlyList<OrderArrivalDto>>;

public sealed class ListVendorArrivalsQueryHandler(
    IVendorContext vendorContext,
    IOrderTrackingRepository tracking,
    IDateTimeProvider clock) : IRequestHandler<ListVendorArrivalsQuery, IReadOnlyList<OrderArrivalDto>>
{
    /// <summary>An "on my way" older than this has either arrived or changed plans.</summary>
    public static readonly TimeSpan Relevance = TimeSpan.FromMinutes(90);

    public async Task<IReadOnlyList<OrderArrivalDto>> Handle(ListVendorArrivalsQuery request, CancellationToken cancellationToken)
    {
        var vendorId = await vendorContext.RequireVendorIdAsync(cancellationToken);
        var rows = await tracking.ListVendorArrivalsAsync(vendorId, clock.UtcNow - Relevance, cancellationToken);
        return rows
            .Select(row => new OrderArrivalDto(
                row.OrderId, row.OrderCode, DateTime.SpecifyKind(row.NotifiedAt, DateTimeKind.Utc), row.Message))
            .ToList();
    }
}

public static class ArrivalMessages
{
    public const string NotCollectable = "Đơn này không còn chờ lấy, không cần báo quán.";

    // The title already says "Khách đang đến"; the body only adds when, so the seller's ticket
    // (which shows both) does not say it twice.
    public static string Body(int? etaMinutes) => etaMinutes is { } eta
        ? $"Khoảng {eta} phút nữa khách tới quầy."
        : "Khách sẽ tới quầy trong ít phút.";
}
