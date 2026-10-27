using FluentValidation;
using MediatR;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Features.WardSlots;

namespace StreetBiz.Application.Features.Tax;

// The ward's side of household tax. The ward is asked to keep the list of households in its area
// current and to pass cases on to the tax office; it does not collect tax or set anyone's revenue.
// So it sees each household's standing (active or not, tax code given or not, notice filed or not)
// and only sees revenue figures for households that agreed to share them.

#region Shared

internal sealed record WardTaxSnapshot(
    int WardId,
    int TaxYear,
    DateOnly Today,
    TaxPolicyRow Policy,
    IReadOnlyList<WardHouseholdFacts> Households,
    IReadOnlyDictionary<long, YearRevenueRow> Revenue,
    IReadOnlySet<long> Filed,
    IReadOnlyList<TaxReferralRow> Referrals)
{
    public static async Task<WardTaxSnapshot> LoadAsync(
        ITaxRepository repository, int wardId, int? year, DateOnly today, CancellationToken cancellationToken)
    {
        var taxYear = year ?? today.Year;
        if (taxYear > today.Year)
        {
            throw new DomainRuleException(TaxMessages.YearNotStarted);
        }

        var policy = await repository.GetPolicyAsync(taxYear, cancellationToken)
            ?? throw new DomainRuleException(TaxMessages.NoPolicy);
        var households = await repository.ListWardHouseholdsAsync(wardId, today, cancellationToken);
        var vendorIds = households.Select(household => household.VendorId).ToList();
        var revenue = (await repository.GetYearRevenueAsync(vendorIds, taxYear, today, cancellationToken))
            .ToDictionary(row => row.VendorId);
        var filed = await repository.ListFiledVendorsAsync(vendorIds, taxYear, TaxPeriodTypes.Year, 0, cancellationToken);
        var referrals = await repository.ListReferralsAsync(wardId, null, cancellationToken);
        return new WardTaxSnapshot(wardId, taxYear, today, policy, households, revenue, filed, referrals);
    }

    public decimal RevenueOf(long vendorId) => Revenue.TryGetValue(vendorId, out var row) ? row.Total : 0m;

    /// <summary>Counts only: no household is named, and a household's group is derived from its own figures.</summary>
    public WardTaxDashboardDto Dashboard()
    {
        int g1 = 0, g2 = 0, g3 = 0, g4 = 0, none = 0, near = 0, over = 0;
        foreach (var household in Households)
        {
            var total = RevenueOf(household.VendorId);
            if (total <= 0)
            {
                none++;
                continue;
            }

            switch (TaxGroups.Of(total, Policy))
            {
                case TaxGroups.One: g1++; break;
                case TaxGroups.Two: g2++; break;
                case TaxGroups.Three: g3++; break;
                default: g4++; break;
            }

            switch (ThresholdLevels.Of(total, Policy))
            {
                case ThresholdLevels.Near: near++; break;
                case ThresholdLevels.Over: over++; break;
            }
        }

        var statuses = Households.Select(HouseholdStatuses.Of).ToList();
        var withCode = Households.Count(household => !string.IsNullOrWhiteSpace(household.TaxCode));
        var filed = Households.Count(household => Filed.Contains(household.VendorId));
        return new WardTaxDashboardDto(
            TaxYear,
            Today,
            TaxPolicyDto.From(Policy, TaxYear),
            Households.Count,
            statuses.Count(status => status == HouseholdStatuses.Active),
            statuses.Count(status => status == HouseholdStatuses.Suspended),
            statuses.Count(status => status == HouseholdStatuses.Ended),
            statuses.Count(status => status == HouseholdStatuses.NotStarted),
            withCode,
            Households.Count - withCode,
            Households.Count(household => household.ShareRevenueWithWard),
            new TaxGroupCountsDto(g1, g2, g3, g4, none),
            near,
            over,
            filed,
            Households.Count - filed,
            TaxCalendar.YearNotice(TaxYear, Policy).Due,
            Referrals.Count(referral => referral.Status == TaxReferralStatuses.Pending),
            Referrals.Count(referral => referral.Status == TaxReferralStatuses.Reported),
            TaxMessages.Disclaimer);
    }

    public IReadOnlyList<WardTaxHouseholdDto> HouseholdDtos() =>
        [.. Households
            .Select(household =>
            {
                var shares = household.ShareRevenueWithWard;
                decimal? revenue = shares ? RevenueOf(household.VendorId) : null;
                return new WardTaxHouseholdDto(
                    household.VendorId,
                    household.DisplayName,
                    household.OwnerName,
                    household.Phone,
                    household.Address,
                    household.BusinessLine,
                    household.VendorType,
                    HouseholdStatuses.Of(household),
                    household.SlotCodes,
                    household.PermitStatus,
                    household.ApprovedAt,
                    household.LastInspectionAt,
                    household.LastOrderAt,
                    !string.IsNullOrWhiteSpace(household.TaxCode),
                    TaxCodes.Mask(household.TaxCode),
                    household.EInvoiceRegistered,
                    Filed.Contains(household.VendorId),
                    shares,
                    revenue,
                    revenue is { } value ? TaxGroups.Of(value, Policy) : null);
            })
            .OrderBy(household => household.DisplayName, StringComparer.CurrentCultureIgnoreCase)];
}

#endregion

#region Reads

public sealed record GetWardTaxDashboardQuery(int? Year) : IRequest<WardTaxDashboardDto>;

public sealed class GetWardTaxDashboardQueryValidator : AbstractValidator<GetWardTaxDashboardQuery>
{
    public GetWardTaxDashboardQueryValidator() =>
        RuleFor(x => x.Year).InclusiveBetween(2026, 2100).When(x => x.Year is not null);
}

public sealed class GetWardTaxDashboardQueryHandler(
    IWardActorContext actorContext,
    ITaxRepository repository,
    TimeProvider clock) : IRequestHandler<GetWardTaxDashboardQuery, WardTaxDashboardDto>
{
    public async Task<WardTaxDashboardDto> Handle(GetWardTaxDashboardQuery request, CancellationToken cancellationToken)
    {
        var actor = await actorContext.RequireAsync(cancellationToken);
        var snapshot = await WardTaxSnapshot.LoadAsync(
            repository, actor.WardId, request.Year, BusinessCalendar.Today(clock), cancellationToken);
        return snapshot.Dashboard();
    }
}

public sealed record ListWardTaxHouseholdsQuery(int? Year) : IRequest<WardTaxHouseholdsDto>;

public sealed class ListWardTaxHouseholdsQueryValidator : AbstractValidator<ListWardTaxHouseholdsQuery>
{
    public ListWardTaxHouseholdsQueryValidator() =>
        RuleFor(x => x.Year).InclusiveBetween(2026, 2100).When(x => x.Year is not null);
}

public sealed class ListWardTaxHouseholdsQueryHandler(
    IWardActorContext actorContext,
    ITaxRepository repository,
    TimeProvider clock) : IRequestHandler<ListWardTaxHouseholdsQuery, WardTaxHouseholdsDto>
{
    public async Task<WardTaxHouseholdsDto> Handle(ListWardTaxHouseholdsQuery request, CancellationToken cancellationToken)
    {
        var actor = await actorContext.RequireAsync(cancellationToken);
        var snapshot = await WardTaxSnapshot.LoadAsync(
            repository, actor.WardId, request.Year, BusinessCalendar.Today(clock), cancellationToken);
        return new WardTaxHouseholdsDto(snapshot.TaxYear, snapshot.HouseholdDtos());
    }
}

public sealed record ListTaxReferralsQuery(string? Status) : IRequest<IReadOnlyList<TaxReferralDto>>;

public sealed class ListTaxReferralsQueryValidator : AbstractValidator<ListTaxReferralsQuery>
{
    public ListTaxReferralsQueryValidator() =>
        RuleFor(x => x.Status).Must(TaxReferralStatuses.IsValid).When(x => !string.IsNullOrEmpty(x.Status))
            .WithMessage("Trạng thái phải là PENDING, REPORTED hoặc DISMISSED.");
}

public sealed class ListTaxReferralsQueryHandler(
    IWardActorContext actorContext,
    ITaxRepository repository) : IRequestHandler<ListTaxReferralsQuery, IReadOnlyList<TaxReferralDto>>
{
    public async Task<IReadOnlyList<TaxReferralDto>> Handle(ListTaxReferralsQuery request, CancellationToken cancellationToken)
    {
        var actor = await actorContext.RequireAsync(cancellationToken);
        var rows = await repository.ListReferralsAsync(
            actor.WardId, string.IsNullOrEmpty(request.Status) ? null : request.Status, cancellationToken);
        return [.. rows.Select(TaxReferralDto.From)];
    }
}

#endregion

#region Referrals

/// <summary>How far back a referral is looked for. Older records are history, not news for the tax office.</summary>
public static class TaxReferralWindow
{
    public const int LookbackDays = 90;
}

public sealed record RefreshTaxReferralsCommand : IRequest<TaxReferralRefreshDto>;

public sealed class RefreshTaxReferralsCommandHandler(
    IWardActorContext actorContext,
    ITaxRepository repository,
    TimeProvider clock) : IRequestHandler<RefreshTaxReferralsCommand, TaxReferralRefreshDto>
{
    public async Task<TaxReferralRefreshDto> Handle(RefreshTaxReferralsCommand request, CancellationToken cancellationToken)
    {
        var actor = await actorContext.RequireAsync(cancellationToken);
        var result = await repository.RefreshReferralsAsync(
            actor.WardId, clock.GetUtcNow().UtcDateTime, TaxReferralWindow.LookbackDays, cancellationToken);
        var pending = (await repository.ListReferralsAsync(actor.WardId, TaxReferralStatuses.Pending, cancellationToken)).Count;
        return new TaxReferralRefreshDto(result.Created, result.AutoClosed, pending);
    }
}

public sealed record ReportTaxReferralCommand(long ReferralId, ReportTaxReferralRequest Request) : IRequest<TaxReferralDto>;

public sealed class ReportTaxReferralCommandValidator : AbstractValidator<ReportTaxReferralCommand>
{
    public ReportTaxReferralCommandValidator()
    {
        RuleFor(x => x.ReferralId).GreaterThan(0);
        RuleFor(x => x.Request.Reference).MaximumLength(100);
        RuleFor(x => x.Request.Note).MaximumLength(500);
    }
}

public sealed class ReportTaxReferralCommandHandler(
    IWardActorContext actorContext,
    ITaxRepository repository,
    TimeProvider clock) : IRequestHandler<ReportTaxReferralCommand, TaxReferralDto>
{
    public async Task<TaxReferralDto> Handle(ReportTaxReferralCommand request, CancellationToken cancellationToken)
    {
        var actor = await actorContext.RequireAsync(cancellationToken);
        var row = await repository.HandleReferralAsync(
            actor.WardId, request.ReferralId, actor.UserId, TaxReferralStatuses.Reported,
            string.IsNullOrWhiteSpace(request.Request.Reference) ? null : request.Request.Reference.Trim(),
            string.IsNullOrWhiteSpace(request.Request.Note) ? null : request.Request.Note.Trim(),
            clock.GetUtcNow().UtcDateTime, cancellationToken);
        return TaxReferralDto.From(row ?? throw new ConflictException(TaxMessages.ReferralNotPending));
    }
}

public sealed record DismissTaxReferralCommand(long ReferralId, DismissTaxReferralRequest Request) : IRequest<TaxReferralDto>;

public sealed class DismissTaxReferralCommandValidator : AbstractValidator<DismissTaxReferralCommand>
{
    public DismissTaxReferralCommandValidator()
    {
        RuleFor(x => x.ReferralId).GreaterThan(0);
        RuleFor(x => x.Request.Reason).NotEmpty().WithMessage("Nhập lý do không cần báo cơ quan thuế.").MaximumLength(500);
    }
}

public sealed class DismissTaxReferralCommandHandler(
    IWardActorContext actorContext,
    ITaxRepository repository,
    TimeProvider clock) : IRequestHandler<DismissTaxReferralCommand, TaxReferralDto>
{
    public async Task<TaxReferralDto> Handle(DismissTaxReferralCommand request, CancellationToken cancellationToken)
    {
        var actor = await actorContext.RequireAsync(cancellationToken);
        var row = await repository.HandleReferralAsync(
            actor.WardId, request.ReferralId, actor.UserId, TaxReferralStatuses.Dismissed, null,
            request.Request.Reason.Trim(), clock.GetUtcNow().UtcDateTime, cancellationToken);
        return TaxReferralDto.From(row ?? throw new ConflictException(TaxMessages.ReferralNotPending));
    }
}

#endregion

#region Announcements

public sealed record ListTaxAnnouncementsQuery : IRequest<IReadOnlyList<TaxAnnouncementDto>>;

public sealed class ListTaxAnnouncementsQueryHandler(
    IWardActorContext actorContext,
    ITaxRepository repository) : IRequestHandler<ListTaxAnnouncementsQuery, IReadOnlyList<TaxAnnouncementDto>>
{
    public async Task<IReadOnlyList<TaxAnnouncementDto>> Handle(
        ListTaxAnnouncementsQuery request, CancellationToken cancellationToken)
    {
        var actor = await actorContext.RequireAsync(cancellationToken);
        return [.. (await repository.ListAnnouncementsAsync(actor.WardId, cancellationToken)).Select(TaxAnnouncementDto.From)];
    }
}

public sealed record SendTaxAnnouncementCommand(SendTaxAnnouncementRequest Request) : IRequest<TaxAnnouncementDto>;

public sealed class SendTaxAnnouncementCommandValidator : AbstractValidator<SendTaxAnnouncementCommand>
{
    public SendTaxAnnouncementCommandValidator()
    {
        RuleFor(x => x.Request.Segment).Must(TaxAnnouncementSegments.IsValid)
            .WithMessage("Nhóm nhận phải là ALL, NOT_FILED hoặc NO_TAX_CODE.");
        RuleFor(x => x.Request.TaxYear).InclusiveBetween(2026, 2100).When(x => x.Request.TaxYear is not null);
        RuleFor(x => x.Request.Title).NotEmpty().WithMessage("Nhập tiêu đề.").MaximumLength(150);
        RuleFor(x => x.Request.Body).NotEmpty().WithMessage("Nhập nội dung.").MaximumLength(800);
    }
}

public sealed class SendTaxAnnouncementCommandHandler(
    IWardActorContext actorContext,
    ITaxRepository repository,
    TimeProvider clock) : IRequestHandler<SendTaxAnnouncementCommand, TaxAnnouncementDto>
{
    public async Task<TaxAnnouncementDto> Handle(SendTaxAnnouncementCommand request, CancellationToken cancellationToken)
    {
        var actor = await actorContext.RequireAsync(cancellationToken);
        var now = clock.GetUtcNow().UtcDateTime;
        var body = request.Request;
        var year = body.TaxYear ?? BusinessCalendar.Today(clock).Year;

        var recipients = await repository.ListRecipientUserIdsAsync(actor.WardId, body.Segment, year, cancellationToken);
        if (recipients.Count == 0)
        {
            throw new DomainRuleException(TaxMessages.NoRecipients);
        }

        var row = await repository.CreateAnnouncementAsync(
            actor.WardId, actor.UserId, body.Segment, year, body.Title.Trim(), body.Body.Trim(), recipients, now,
            cancellationToken);
        return TaxAnnouncementDto.From(row);
    }
}

#endregion

#region Export

public sealed record ExportWardTaxQuery(int? Year) : IRequest<FinanceFile>;

public sealed class ExportWardTaxQueryValidator : AbstractValidator<ExportWardTaxQuery>
{
    public ExportWardTaxQueryValidator() =>
        RuleFor(x => x.Year).InclusiveBetween(2026, 2100).When(x => x.Year is not null);
}

public sealed class ExportWardTaxQueryHandler(
    IWardActorContext actorContext,
    ITaxRepository repository,
    ITaxDocumentRenderer renderer,
    TimeProvider clock) : IRequestHandler<ExportWardTaxQuery, FinanceFile>
{
    public async Task<FinanceFile> Handle(ExportWardTaxQuery request, CancellationToken cancellationToken)
    {
        var actor = await actorContext.RequireAsync(cancellationToken);
        var today = BusinessCalendar.Today(clock);
        var snapshot = await WardTaxSnapshot.LoadAsync(repository, actor.WardId, request.Year, today, cancellationToken);
        var wardName = await repository.GetWardNameAsync(actor.WardId, cancellationToken) ?? $"Phường #{actor.WardId}";
        var dashboard = snapshot.Dashboard();

        IReadOnlyList<(string Label, string Value)> summary =
        [
            ("Năm thuế", snapshot.TaxYear.ToString()),
            ("Ngưỡng doanh thu không chịu thuế", TaxFormat.Money(snapshot.Policy.RevenueThreshold)),
            ("Căn cứ", snapshot.Policy.LegalBasis),
            ("Tổng số hộ kinh doanh (đã duyệt đăng ký)", dashboard.HouseholdCount.ToString()),
            ("Đang hoạt động", dashboard.ActiveCount.ToString()),
            ("Tạm đình chỉ", dashboard.SuspendedCount.ToString()),
            ("Đã ngừng (hết hợp đồng)", dashboard.EndedCount.ToString()),
            ("Chưa thuê ô", dashboard.NotStartedCount.ToString()),
            ("Đã khai mã số thuế", dashboard.WithTaxCode.ToString()),
            ("Chưa khai mã số thuế", dashboard.WithoutTaxCode.ToString()),
            ($"Đã thông báo doanh thu năm {snapshot.TaxYear}", dashboard.NoticeFiled.ToString()),
            ($"Chưa thông báo (hạn {TaxFormat.Day(dashboard.NoticeDueDate)})", dashboard.NoticeNotFiled.ToString()),
            ("Hộ chia sẻ doanh thu với phường", dashboard.SharingRevenue.ToString()),
            ("Mục đề nghị cơ quan thuế đang chờ xử lý", dashboard.PendingReferrals.ToString()),
            ("Ghi chú", "Doanh thu chỉ hiện với hộ đồng ý chia sẻ. Mã số thuế được che, chỉ hiện 4 số cuối."),
        ];

        var file = renderer.RenderWardTaxXlsx(new WardTaxExport(
            snapshot.TaxYear,
            wardName,
            clock.GetUtcNow().UtcDateTime,
            summary,
            [.. snapshot.HouseholdDtos().Select(household => new WardTaxExportHousehold(
                household.DisplayName, household.OwnerName, household.Phone, household.Address, household.BusinessLine,
                household.Status, string.Join(", ", household.SlotCodes), household.ApprovedAt, household.LastInspectionAt,
                household.TaxCodeMasked, household.NoticeFiled, household.Revenue, household.Group))],
            [.. snapshot.Referrals.Select(referral => new WardTaxExportReferral(
                TaxReferralReasons.Label(referral.ReasonCode), referral.Summary, referral.VendorName, referral.DetectedAt,
                referral.Status, referral.HandledAt, referral.AuthorityReference, referral.HandlingNote))]));

        return new FinanceFile(
            $"thue-ho-kinh-doanh-{snapshot.TaxYear}.xlsx",
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            file);
    }
}

#endregion
