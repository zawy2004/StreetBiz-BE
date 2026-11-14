using FluentValidation;
using MediatR;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Security;

namespace StreetBiz.Application.Features.Commerce;

/// <summary>REV-03: the reasons a storefront or dish can be reported for. Codes are what clients send.</summary>
public static class ContentReportReasons
{
    public const string Misleading = "MISLEADING";
    public const string Inappropriate = "INAPPROPRIATE";
    public const string UnsafeOrIllegal = "UNSAFE_OR_ILLEGAL";
    public const string Counterfeit = "COUNTERFEIT";
    public const string Other = "OTHER";

    public const int DetailMaxLength = 400;
    public const int OtherDetailMinLength = 10;

    private static readonly IReadOnlyDictionary<string, string> Labels = new Dictionary<string, string>
    {
        [Misleading] = "Thông tin hoặc hình ảnh sai sự thật, gây hiểu lầm",
        [Inappropriate] = "Nội dung phản cảm, xúc phạm",
        [UnsafeOrIllegal] = "Món hàng không an toàn hoặc bị cấm",
        [Counterfeit] = "Hàng giả, gian lận",
        [Other] = "Lý do khác",
    };

    public static bool IsValid(string? code) => code is not null && Labels.ContainsKey(code);

    public static IReadOnlyCollection<string> All => Labels.Keys.ToArray();

    /// <summary>The text the moderator reads in the review queue: the reason's label, then the reporter's own words.</summary>
    public static string Compose(string code, string? detail)
    {
        var label = Labels[code];
        return string.IsNullOrWhiteSpace(detail) ? label : $"{label}: {detail.Trim()}";
    }
}

public static class ContentReportLimits
{
    public const int MaxPerHour = 10;
    public static readonly TimeSpan Window = TimeSpan.FromHours(1);
}

public sealed record ContentReportDraft(
    string ContentType,
    long ContentId,
    long ReporterUserId,
    string Reason);

public enum ContentReportOutcome
{
    Created,
    ContentNotFound,
    OwnContent,
    Duplicate,
    RateLimited,
}

public sealed record ContentReportResult(
    ContentReportOutcome Outcome,
    long? ReportId = null,
    int RetryAfterSeconds = 0);

public sealed record ContentReportReceiptDto(long ReportId, string Status);

public static class ContentReportMessages
{
    public const string NotFound = "Không tìm thấy nội dung này hoặc nội dung đã bị ẩn.";
    public const string OwnContent = "Bạn không thể báo cáo nội dung của chính mình.";
    public const string Duplicate = "Bạn đã báo cáo nội dung này và báo cáo đang được xem xét.";
    public const string RateLimited = "Bạn đã gửi quá nhiều báo cáo. Vui lòng thử lại sau.";
}

/// <summary>Who may file a content report: an active Customer or Vendor (a Guest must sign in first, AS-03).</summary>
public interface IContentReporterContext
{
    Task<long> RequireAsync(CancellationToken cancellationToken);
}

public sealed class ContentReporterContext(
    ICurrentUser currentUser,
    IUserAccountRepository users) : IContentReporterContext
{
    public async Task<long> RequireAsync(CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId
            ?? throw new AuthenticationException(AppMessages.SessionExpired);
        var user = await users.GetByIdAsync(userId, cancellationToken);
        if (user is null
            || user.RoleCode is not (RoleCodes.Customer or RoleCodes.Vendor)
            || currentUser.RoleCode != user.RoleCode
            || user.AccountStatus != AccountStatuses.Active)
        {
            throw new ForbiddenException(AppMessages.Forbidden);
        }

        return userId;
    }
}

public sealed record ReportContentCommand(string ContentType, long ContentId, string ReasonCode, string? Detail)
    : IRequest<ContentReportReceiptDto>;

public sealed class ReportContentCommandValidator : AbstractValidator<ReportContentCommand>
{
    public ReportContentCommandValidator()
    {
        RuleFor(x => x.ContentType)
            .Must(type => type is ReportedContentTypes.Storefront or ReportedContentTypes.MenuItem)
            .WithMessage("Only a storefront or a dish can be reported here.");
        RuleFor(x => x.ContentId).GreaterThan(0);
        RuleFor(x => x.ReasonCode)
            .Must(ContentReportReasons.IsValid)
            .WithMessage("Hãy chọn lý do báo cáo.");
        RuleFor(x => x.Detail)
            .Must(detail => detail is null || detail.Trim().Length <= ContentReportReasons.DetailMaxLength)
            .WithMessage($"Chi tiết tối đa {ContentReportReasons.DetailMaxLength} ký tự.");
        RuleFor(x => x.Detail)
            .Must(detail => detail is not null && detail.Trim().Length >= ContentReportReasons.OtherDetailMinLength)
            .When(x => x.ReasonCode == ContentReportReasons.Other)
            .WithMessage($"Hãy mô tả lý do, tối thiểu {ContentReportReasons.OtherDetailMinLength} ký tự.");
    }
}

public sealed class ReportContentCommandHandler(
    IContentReporterContext reporter,
    IContentReportRepository reports) : IRequestHandler<ReportContentCommand, ContentReportReceiptDto>
{
    public async Task<ContentReportReceiptDto> Handle(
        ReportContentCommand request,
        CancellationToken cancellationToken)
    {
        var reporterUserId = await reporter.RequireAsync(cancellationToken);
        var result = await reports.CreateAsync(
            new ContentReportDraft(
                request.ContentType,
                request.ContentId,
                reporterUserId,
                ContentReportReasons.Compose(request.ReasonCode, request.Detail)),
            cancellationToken);

        return result.Outcome switch
        {
            ContentReportOutcome.Created => new ContentReportReceiptDto(result.ReportId!.Value, ReportedContentStatuses.Pending),
            ContentReportOutcome.ContentNotFound => throw new NotFoundException(ContentReportMessages.NotFound),
            ContentReportOutcome.OwnContent => throw new DomainRuleException(ContentReportMessages.OwnContent),
            ContentReportOutcome.Duplicate => throw new ConflictException(ContentReportMessages.Duplicate),
            _ => throw new TooManyRequestsException(ContentReportMessages.RateLimited, Math.Max(1, result.RetryAfterSeconds)),
        };
    }
}
