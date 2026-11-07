using FluentValidation;
using MediatR;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;

namespace StreetBiz.Application.Features.Tax;

// What a household does with its own tax picture: see where it stands, keep the revenue book,
// choose its profile and what the ward may see, estimate what it would owe, and confirm the
// notices it has filed. Every handler resolves the household from the caller (IVendorContext), so
// there is no id in a request that could name someone else.

#region Shared

/// <summary>A household's standing for one tax year: its revenue so far and what that means under the policy in force.</summary>
internal sealed record TaxStanding(
    int TaxYear,
    DateOnly Today,
    TaxPolicyRow Policy,
    VendorTaxProfileRow? Profile,
    TaxIndustryRateRow Industry,
    IReadOnlyList<RevenueDayRow> Days,
    decimal Online,
    decimal Recorded,
    DateOnly? FirstRevenue,
    string Group,
    string Level,
    RevenueProjection Projection)
{
    public decimal Total => Online + Recorded;
}

internal static class TaxStandings
{
    public const string DefaultIndustry = "FOOD_SERVICE";

    public static async Task<TaxStanding> LoadAsync(
        ITaxRepository repository, long vendorId, int? year, DateOnly today, CancellationToken cancellationToken)
    {
        var taxYear = year ?? today.Year;
        if (taxYear > today.Year)
        {
            throw new DomainRuleException(TaxMessages.YearNotStarted);
        }

        var policy = await repository.GetPolicyAsync(taxYear, cancellationToken)
            ?? throw new DomainRuleException(TaxMessages.NoPolicy);
        var profile = await repository.GetProfileAsync(vendorId, cancellationToken);

        var yearStart = new DateOnly(taxYear, 1, 1);
        var yearEnd = new DateOnly(taxYear, 12, 31);
        var days = await repository.GetDailyRevenueAsync(
            vendorId, yearStart, today < yearEnd ? today : yearEnd, cancellationToken);

        var online = days.Sum(day => day.Online);
        var recorded = days.Sum(day => day.Recorded);
        var first = days.Where(day => day.Total > 0).Select(day => (DateOnly?)day.Date).Min();
        var total = online + recorded;
        var projection = RevenueProjections.Project(total, first, today, taxYear, policy.RevenueThreshold);

        return new TaxStanding(
            taxYear, today, policy, profile, IndustryOf(policy, profile?.IndustryCode),
            days, online, recorded, first, TaxGroups.Of(total, policy), ThresholdLevels.Of(total, policy), projection);
    }

    /// <summary>The household's industry in the policy; an unknown code falls back to OTHER, then to the first rate.</summary>
    public static TaxIndustryRateRow IndustryOf(TaxPolicyRow policy, string? code) =>
        policy.Rates.FirstOrDefault(rate => rate.Code == (code ?? DefaultIndustry))
        ?? policy.Rates.FirstOrDefault(rate => rate.Code == "OTHER")
        ?? policy.Rates[0];

    public static TaxProfileDto ProfileDto(VendorTaxProfileRow? profile, TaxPolicyRow policy)
    {
        var industry = IndustryOf(policy, profile?.IndustryCode);
        return new TaxProfileDto(
            profile is not null, profile?.TaxCode, industry.Code, industry.Name,
            profile?.EInvoiceRegistered ?? false, profile?.ShareRevenueWithWard ?? false, profile?.ConsentUpdatedAt);
    }

    public static IReadOnlyList<TaxMonthRevenueDto> Months(IReadOnlyList<RevenueDayRow> days) =>
        [.. Enumerable.Range(1, 12).Select(month =>
        {
            var inMonth = days.Where(day => day.Date.Month == month).ToList();
            var online = inMonth.Sum(day => day.Online);
            var recorded = inMonth.Sum(day => day.Recorded);
            return new TaxMonthRevenueDto(month, online, recorded, online + recorded);
        })];

    public static DateOnly Today(TimeProvider clock) => BusinessCalendar.Today(clock);

    public static long UserId(ICurrentUser currentUser) =>
        currentUser.UserId ?? throw new AuthenticationException(AppMessages.SessionExpired);
}

internal static class TaxPeriodViews
{
    /// <summary>The year notice and the four quarters of a tax year, with their status for this household.</summary>
    public static IReadOnlyList<TaxPeriodDto> Build(
        TaxPolicyRow policy,
        int taxYear,
        DateOnly today,
        IReadOnlyList<RevenueDayRow> days,
        IReadOnlyList<TaxDeclarationRow> filings)
    {
        var result = new List<TaxPeriodDto>();
        foreach (var period in TaxCalendar.Periods(taxYear, policy))
        {
            var filing = filings.FirstOrDefault(row => row.PeriodType == period.Type && row.PeriodNo == period.No);
            var through = today < period.End ? today : period.End;
            var systemRevenue = days.Where(day => day.Date >= period.Start && day.Date <= through).Sum(day => day.Total);
            var required = filing is not null || IsRequired(period, today, days, policy);
            var status = TaxCalendar.Status(period, today, filing is not null, required);
            result.Add(new TaxPeriodDto(
                period.Type, period.No, period.Label, period.Start, period.End, period.Due, status, required,
                required && TaxCalendar.CanFile(period, today), systemRevenue,
                filing is null ? null : FilingDto(filing)));
        }

        return result;
    }

    /// <summary>
    /// The yearly notice is for everyone. A quarter is a declaration only once the year's revenue up
    /// to the end of that quarter (or to today, while it runs) is above the no-tax threshold.
    /// </summary>
    public static bool IsRequired(TaxPeriod period, DateOnly today, IReadOnlyList<RevenueDayRow> days, TaxPolicyRow policy)
    {
        if (period.Type == TaxPeriodTypes.Year)
        {
            return true;
        }

        if (period.Start > today)
        {
            return false;
        }

        var through = today < period.End ? today : period.End;
        var yearStart = new DateOnly(period.Start.Year, 1, 1);
        return days.Where(day => day.Date >= yearStart && day.Date <= through).Sum(day => day.Total)
            > policy.RevenueThreshold;
    }

    public static TaxFilingDto FilingDto(TaxDeclarationRow row) => new(
        row.RevenueDeclared, row.RevenueSystem, row.FiledOn, row.AuthorityReference, row.ReceiptUrl, row.Note,
        row.CreatedAt, row.UpdatedAt);

    /// <summary>The earliest unfiled period the household has to deal with, overdue ones first.</summary>
    public static TaxDeadlineDto? NextDeadline(IReadOnlyList<TaxPeriodDto> periods, DateOnly today) =>
        periods
            .Where(period => period.Required && period.Status != TaxPeriodStatuses.Filed)
            .OrderBy(period => period.DueDate)
            .Select(period => new TaxDeadlineDto(
                period.PeriodType, period.PeriodNo, period.Label, period.DueDate,
                period.DueDate.DayNumber - today.DayNumber, period.Status))
            .FirstOrDefault();
}

#endregion

#region Overview

public sealed record GetTaxOverviewQuery(int? Year) : IRequest<TaxOverviewDto>;

public sealed class GetTaxOverviewQueryValidator : AbstractValidator<GetTaxOverviewQuery>
{
    public GetTaxOverviewQueryValidator() =>
        RuleFor(x => x.Year).InclusiveBetween(2026, 2100).When(x => x.Year is not null);
}

public sealed class GetTaxOverviewQueryHandler(
    IVendorContext vendorContext,
    ITaxRepository repository,
    TimeProvider clock) : IRequestHandler<GetTaxOverviewQuery, TaxOverviewDto>
{
    public async Task<TaxOverviewDto> Handle(GetTaxOverviewQuery request, CancellationToken cancellationToken)
    {
        var vendorId = await vendorContext.RequireVendorIdAsync(cancellationToken);
        var today = TaxStandings.Today(clock);
        var standing = await TaxStandings.LoadAsync(repository, vendorId, request.Year, today, cancellationToken);
        var filings = await repository.ListDeclarationsAsync(vendorId, standing.TaxYear, cancellationToken);

        var policy = standing.Policy;
        var projection = standing.Projection;
        var projectedRevenue = projection.Enough ? projection.ProjectedRevenue : standing.Total;
        var hasTaxCode = !string.IsNullOrWhiteSpace(standing.Profile?.TaxCode);
        var eInvoice = standing.Profile?.EInvoiceRegistered ?? false;
        var periods = TaxPeriodViews.Build(policy, standing.TaxYear, today, standing.Days, filings);

        return new TaxOverviewDto(
            standing.TaxYear,
            today,
            today.Year > standing.TaxYear,
            TaxPolicyDto.From(policy, standing.TaxYear),
            new RevenueTotalsDto(standing.Online, standing.Recorded, standing.Total),
            standing.Group,
            TaxGroups.Describe(standing.Group, policy),
            standing.Level,
            Math.Round(standing.Total / policy.RevenueThreshold, 4),
            Math.Max(policy.RevenueThreshold - standing.Total, 0),
            new TaxProjectionDto(
                projection.Enough, projection.DaysOfData, projection.DailyRate, projection.ProjectedRevenue,
                TaxGroups.Of(projectedRevenue, policy), ThresholdLevels.Of(projectedRevenue, policy),
                projection.WillCross, projection.CrossingDate),
            [.. TaxObligations.For(standing.Group, policy, hasTaxCode, eInvoice).Select(Obligation)],
            standing.Group == TaxGroups.One
                ? [.. TaxObligations.OnceOver(TaxGroups.Two, policy, eInvoice).Select(Obligation)]
                : [],
            TaxPeriodViews.NextDeadline(periods, today),
            TaxStandings.ProfileDto(standing.Profile, policy),
            TaxStandings.Months(standing.Days),
            TaxMessages.Disclaimer);
    }

    private static TaxObligationDto Obligation(TaxObligation obligation) =>
        new(obligation.Code, obligation.Title, obligation.Detail, obligation.Kind, obligation.Done);
}

#endregion

#region Revenue book

public sealed record GetRevenueLedgerQuery(int Year, int Month) : IRequest<RevenueLedgerDto>;

public sealed class GetRevenueLedgerQueryValidator : AbstractValidator<GetRevenueLedgerQuery>
{
    public GetRevenueLedgerQueryValidator()
    {
        RuleFor(x => x.Year).InclusiveBetween(2026, 2100);
        RuleFor(x => x.Month).InclusiveBetween(1, 12);
    }
}

public sealed class GetRevenueLedgerQueryHandler(
    IVendorContext vendorContext,
    ITaxRepository repository,
    TimeProvider clock) : IRequestHandler<GetRevenueLedgerQuery, RevenueLedgerDto>
{
    public async Task<RevenueLedgerDto> Handle(GetRevenueLedgerQuery request, CancellationToken cancellationToken)
    {
        var vendorId = await vendorContext.RequireVendorIdAsync(cancellationToken);
        var today = TaxStandings.Today(clock);
        var from = new DateOnly(request.Year, request.Month, 1);
        if (from > today)
        {
            throw new DomainRuleException(TaxMessages.YearNotStarted);
        }

        var to = new DateOnly(request.Year, request.Month, DateTime.DaysInMonth(request.Year, request.Month));
        var days = await repository.GetDailyRevenueAsync(vendorId, from, to, cancellationToken);
        var entries = await repository.ListEntriesAsync(vendorId, from, to, cancellationToken);

        // A day shows if it had sales or any entry, voided ones included (struck through, not counted).
        var dates = days.Select(day => day.Date).Union(entries.Select(entry => entry.Date)).OrderByDescending(date => date);
        var rows = dates.Select(date =>
        {
            var day = days.FirstOrDefault(row => row.Date == date);
            return new RevenueDayDto(
                date,
                day?.Online ?? 0,
                day?.OnlineOrders ?? 0,
                day?.Recorded ?? 0,
                day?.Total ?? 0,
                [.. entries.Where(entry => entry.Date == date).OrderBy(entry => entry.EntryId).Select(RevenueEntryDto.From)]);
        }).ToList();

        var online = days.Sum(day => day.Online);
        var recorded = days.Sum(day => day.Recorded);
        return new RevenueLedgerDto(request.Year, request.Month, new RevenueTotalsDto(online, recorded, online + recorded), rows);
    }
}

public sealed record AddRevenueEntryCommand(AddRevenueEntryRequest Request) : IRequest<RevenueEntryDto>;

public sealed class AddRevenueEntryCommandValidator : AbstractValidator<AddRevenueEntryCommand>
{
    public AddRevenueEntryCommandValidator()
    {
        RuleFor(x => x.Request.Amount)
            .GreaterThan(0).WithMessage("Số tiền phải lớn hơn 0.")
            .LessThanOrEqualTo(10_000_000_000m).WithMessage("Số tiền một lần ghi tối đa 10 tỷ đồng.")
            .Must(amount => amount == decimal.Truncate(amount)).WithMessage("Số tiền tính bằng đồng, không có phần lẻ.");
        RuleFor(x => x.Request.Channel).Must(RevenueChannels.IsValid)
            .WithMessage("Hình thức thu phải là CASH, BANK_TRANSFER, EWALLET hoặc OTHER.");
        RuleFor(x => x.Request.Note).MaximumLength(300);
    }
}

public sealed class AddRevenueEntryCommandHandler(
    IVendorContext vendorContext,
    ITaxRepository repository,
    TimeProvider clock) : IRequestHandler<AddRevenueEntryCommand, RevenueEntryDto>
{
    private static readonly DateOnly FirstTaxDay = new(2026, 1, 1);

    public async Task<RevenueEntryDto> Handle(AddRevenueEntryCommand request, CancellationToken cancellationToken)
    {
        var vendorId = await vendorContext.RequireVendorIdAsync(cancellationToken);
        var today = TaxStandings.Today(clock);
        var date = request.Request.Date;
        if (date > today || date < FirstTaxDay || date < today.AddYears(-2))
        {
            throw new DomainRuleException(TaxMessages.EntryDateRange);
        }

        var entry = await repository.AddEntryAsync(
            vendorId, date, request.Request.Amount, request.Request.Channel,
            string.IsNullOrWhiteSpace(request.Request.Note) ? null : request.Request.Note.Trim(),
            clock.GetUtcNow().UtcDateTime, cancellationToken);
        return RevenueEntryDto.From(entry);
    }
}

public sealed record VoidRevenueEntryCommand(long EntryId, string? Reason) : IRequest<RevenueEntryDto>;

public sealed class VoidRevenueEntryCommandValidator : AbstractValidator<VoidRevenueEntryCommand>
{
    public VoidRevenueEntryCommandValidator()
    {
        RuleFor(x => x.EntryId).GreaterThan(0);
        RuleFor(x => x.Reason).MaximumLength(300);
    }
}

public sealed class VoidRevenueEntryCommandHandler(
    IVendorContext vendorContext,
    ICurrentUser currentUser,
    ITaxRepository repository,
    TimeProvider clock) : IRequestHandler<VoidRevenueEntryCommand, RevenueEntryDto>
{
    public async Task<RevenueEntryDto> Handle(VoidRevenueEntryCommand request, CancellationToken cancellationToken)
    {
        var vendorId = await vendorContext.RequireVendorIdAsync(cancellationToken);
        var entry = await repository.VoidEntryAsync(
            vendorId, TaxStandings.UserId(currentUser), request.EntryId,
            string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim(),
            clock.GetUtcNow().UtcDateTime, cancellationToken);
        return RevenueEntryDto.From(entry ?? throw new NotFoundException(TaxMessages.EntryNotFound));
    }
}

#endregion

#region Profile

public sealed record GetTaxProfileQuery : IRequest<TaxProfileDto>;

public sealed class GetTaxProfileQueryHandler(
    IVendorContext vendorContext,
    ITaxRepository repository,
    TimeProvider clock) : IRequestHandler<GetTaxProfileQuery, TaxProfileDto>
{
    public async Task<TaxProfileDto> Handle(GetTaxProfileQuery request, CancellationToken cancellationToken)
    {
        var vendorId = await vendorContext.RequireVendorIdAsync(cancellationToken);
        var policy = await repository.GetPolicyAsync(TaxStandings.Today(clock).Year, cancellationToken)
            ?? throw new DomainRuleException(TaxMessages.NoPolicy);
        return TaxStandings.ProfileDto(await repository.GetProfileAsync(vendorId, cancellationToken), policy);
    }
}

public sealed record UpdateTaxProfileCommand(UpdateTaxProfileRequest Request) : IRequest<TaxProfileDto>;

public sealed class UpdateTaxProfileCommandValidator : AbstractValidator<UpdateTaxProfileCommand>
{
    public UpdateTaxProfileCommandValidator()
    {
        RuleFor(x => x.Request.TaxCode)
            .Must(code => string.IsNullOrWhiteSpace(code) || TaxCodes.IsValid(code.Trim()))
            .WithMessage("Mã số thuế gồm 10 số, 10 số-3 số, 13 số hoặc 12 số (số định danh cá nhân).");
        RuleFor(x => x.Request.IndustryCode).NotEmpty().MaximumLength(30);
    }
}

public sealed class UpdateTaxProfileCommandHandler(
    IVendorContext vendorContext,
    ICurrentUser currentUser,
    ITaxRepository repository,
    TimeProvider clock) : IRequestHandler<UpdateTaxProfileCommand, TaxProfileDto>
{
    public async Task<TaxProfileDto> Handle(UpdateTaxProfileCommand request, CancellationToken cancellationToken)
    {
        var vendorId = await vendorContext.RequireVendorIdAsync(cancellationToken);
        var now = clock.GetUtcNow().UtcDateTime;
        var policy = await repository.GetPolicyAsync(BusinessCalendar.Today(clock).Year, cancellationToken)
            ?? throw new DomainRuleException(TaxMessages.NoPolicy);
        var industryCode = request.Request.IndustryCode.Trim();
        if (policy.Rates.All(rate => rate.Code != industryCode))
        {
            throw new DomainRuleException(TaxMessages.UnknownIndustry);
        }

        var profile = await repository.SaveProfileAsync(
            vendorId,
            TaxStandings.UserId(currentUser),
            string.IsNullOrWhiteSpace(request.Request.TaxCode) ? null : request.Request.TaxCode.Trim(),
            industryCode,
            request.Request.EInvoiceRegistered,
            request.Request.ShareRevenueWithWard,
            now,
            cancellationToken);
        return TaxStandings.ProfileDto(profile, policy);
    }
}

#endregion

#region Estimate

public sealed record GetTaxEstimateQuery(int? Year, string? Basis, decimal? Revenue, decimal? CostRatio)
    : IRequest<TaxEstimateDto>;

public sealed class GetTaxEstimateQueryValidator : AbstractValidator<GetTaxEstimateQuery>
{
    public GetTaxEstimateQueryValidator()
    {
        RuleFor(x => x.Year).InclusiveBetween(2026, 2100).When(x => x.Year is not null);
        RuleFor(x => x.Basis).Must(basis => basis is null or "ACTUAL" or "PROJECTED")
            .WithMessage("Cơ sở tính phải là ACTUAL hoặc PROJECTED.");
        RuleFor(x => x.Revenue).InclusiveBetween(0m, 1_000_000_000_000m).When(x => x.Revenue is not null);
        RuleFor(x => x.CostRatio).InclusiveBetween(0m, 0.99m).When(x => x.CostRatio is not null)
            .WithMessage("Tỷ lệ chi phí từ 0% đến 99%.");
    }
}

public sealed class GetTaxEstimateQueryHandler(
    IVendorContext vendorContext,
    ITaxRepository repository,
    TimeProvider clock) : IRequestHandler<GetTaxEstimateQuery, TaxEstimateDto>
{
    public async Task<TaxEstimateDto> Handle(GetTaxEstimateQuery request, CancellationToken cancellationToken)
    {
        var vendorId = await vendorContext.RequireVendorIdAsync(cancellationToken);
        var standing = await TaxStandings.LoadAsync(
            repository, vendorId, request.Year, TaxStandings.Today(clock), cancellationToken);

        string basis;
        decimal revenue;
        if (request.Revenue is { } custom)
        {
            basis = "CUSTOM";
            revenue = custom;
        }
        else if (request.Basis == "PROJECTED" && standing.Projection.Enough)
        {
            basis = "PROJECTED";
            revenue = standing.Projection.ProjectedRevenue;
        }
        else
        {
            basis = "ACTUAL";
            revenue = standing.Total;
        }

        var estimate = TaxEstimator.Estimate(revenue, standing.Policy, standing.Industry, request.CostRatio);
        return new TaxEstimateDto(
            standing.TaxYear, basis, estimate.Revenue, standing.Industry.Code, standing.Industry.Name, estimate.Group,
            TaxGroups.Describe(estimate.Group, standing.Policy), estimate.Taxable, estimate.VatRate, estimate.PitRate,
            estimate.Vat, estimate.PitBase, estimate.PitOnRevenue, estimate.TotalOnRevenue, estimate.EffectiveRate,
            estimate.CostRatio, estimate.Profit, estimate.PitProfitRate, estimate.PitOnProfit, estimate.TotalOnProfit,
            estimate.Cheaper, standing.Policy.PitDeductsThreshold, standing.Policy.RevenueThreshold,
            standing.Policy.LegalBasis, TaxMessages.Disclaimer);
    }
}

#endregion

#region Filings the household confirms

public sealed record ListTaxDeclarationsQuery(int? Year) : IRequest<TaxDeclarationsDto>;

public sealed class ListTaxDeclarationsQueryValidator : AbstractValidator<ListTaxDeclarationsQuery>
{
    public ListTaxDeclarationsQueryValidator() =>
        RuleFor(x => x.Year).InclusiveBetween(2026, 2100).When(x => x.Year is not null);
}

public sealed class ListTaxDeclarationsQueryHandler(
    IVendorContext vendorContext,
    ITaxRepository repository,
    TimeProvider clock) : IRequestHandler<ListTaxDeclarationsQuery, TaxDeclarationsDto>
{
    public async Task<TaxDeclarationsDto> Handle(ListTaxDeclarationsQuery request, CancellationToken cancellationToken)
    {
        var vendorId = await vendorContext.RequireVendorIdAsync(cancellationToken);
        var today = TaxStandings.Today(clock);
        var standing = await TaxStandings.LoadAsync(repository, vendorId, request.Year, today, cancellationToken);
        var filings = await repository.ListDeclarationsAsync(vendorId, standing.TaxYear, cancellationToken);
        return new TaxDeclarationsDto(
            standing.TaxYear, today, standing.Group,
            TaxPeriodViews.Build(standing.Policy, standing.TaxYear, today, standing.Days, filings),
            TaxMessages.Disclaimer);
    }
}

public sealed record ConfirmTaxFilingCommand(int Year, string PeriodType, int PeriodNo, ConfirmTaxFilingRequest Request)
    : IRequest<TaxPeriodDto>;

public sealed class ConfirmTaxFilingCommandValidator : AbstractValidator<ConfirmTaxFilingCommand>
{
    public ConfirmTaxFilingCommandValidator()
    {
        RuleFor(x => x.Year).InclusiveBetween(2026, 2100);
        RuleFor(x => x.PeriodType).Must(type => type is TaxPeriodTypes.Year or TaxPeriodTypes.Quarter)
            .WithMessage("Loại kỳ phải là YEAR hoặc QUARTER.");
        RuleFor(x => x.PeriodNo).InclusiveBetween(0, 4);
        RuleFor(x => x.Request.RevenueDeclared)
            .GreaterThanOrEqualTo(0).WithMessage("Doanh thu đã khai không được âm.")
            .LessThanOrEqualTo(1_000_000_000_000m)
            .Must(amount => amount == decimal.Truncate(amount)).WithMessage("Doanh thu tính bằng đồng, không có phần lẻ.");
        RuleFor(x => x.Request.AuthorityReference).MaximumLength(100);
        RuleFor(x => x.Request.Note).MaximumLength(500);
        RuleFor(x => x.Request.ReceiptUrl).MaximumLength(500);
    }
}

public sealed class ConfirmTaxFilingCommandHandler(
    IVendorContext vendorContext,
    ICurrentUser currentUser,
    ITaxRepository repository,
    TimeProvider clock) : IRequestHandler<ConfirmTaxFilingCommand, TaxPeriodDto>
{
    public async Task<TaxPeriodDto> Handle(ConfirmTaxFilingCommand request, CancellationToken cancellationToken)
    {
        var vendorId = await vendorContext.RequireVendorIdAsync(cancellationToken);
        var userId = TaxStandings.UserId(currentUser);
        var today = TaxStandings.Today(clock);
        var standing = await TaxStandings.LoadAsync(repository, vendorId, request.Year, today, cancellationToken);

        var period = TaxCalendar.Find(request.Year, request.PeriodType, request.PeriodNo, standing.Policy)
            ?? throw new NotFoundException(TaxMessages.DeclarationNotFound);
        if (!TaxCalendar.CanFile(period, today))
        {
            throw new DomainRuleException(TaxMessages.PeriodNotEnded);
        }

        if (!TaxPeriodViews.IsRequired(period, today, standing.Days, standing.Policy))
        {
            throw new DomainRuleException(TaxMessages.PeriodNotRequired);
        }

        var body = request.Request;
        if (body.FiledOn <= period.End || body.FiledOn > today)
        {
            throw new DomainRuleException(TaxMessages.FiledOnRange);
        }

        var receipt = string.IsNullOrWhiteSpace(body.ReceiptUrl) ? null : body.ReceiptUrl.Trim();
        if (receipt is not null
            && !(EvidenceFiles.TryParseUrl(receipt, out var ownerUserId, out _) && ownerUserId == userId))
        {
            throw new DomainRuleException(TaxMessages.ReceiptNotYours);
        }

        var through = today < period.End ? today : period.End;
        var systemRevenue = standing.Days
            .Where(day => day.Date >= period.Start && day.Date <= through)
            .Sum(day => day.Total);

        await repository.SaveDeclarationAsync(
            vendorId,
            userId,
            new SaveTaxDeclaration(
                request.Year, request.PeriodType, request.PeriodNo, body.RevenueDeclared, systemRevenue, body.FiledOn,
                string.IsNullOrWhiteSpace(body.AuthorityReference) ? null : body.AuthorityReference.Trim(),
                receipt,
                string.IsNullOrWhiteSpace(body.Note) ? null : body.Note.Trim()),
            clock.GetUtcNow().UtcDateTime,
            cancellationToken);

        var filings = await repository.ListDeclarationsAsync(vendorId, request.Year, cancellationToken);
        return TaxPeriodViews.Build(standing.Policy, request.Year, today, standing.Days, filings)
            .First(view => view.PeriodType == request.PeriodType && view.PeriodNo == request.PeriodNo);
    }
}

public sealed record DeleteTaxFilingCommand(int Year, string PeriodType, int PeriodNo) : IRequest<Unit>;

public sealed class DeleteTaxFilingCommandValidator : AbstractValidator<DeleteTaxFilingCommand>
{
    public DeleteTaxFilingCommandValidator()
    {
        RuleFor(x => x.Year).InclusiveBetween(2026, 2100);
        RuleFor(x => x.PeriodType).Must(type => type is TaxPeriodTypes.Year or TaxPeriodTypes.Quarter);
        RuleFor(x => x.PeriodNo).InclusiveBetween(0, 4);
    }
}

public sealed class DeleteTaxFilingCommandHandler(
    IVendorContext vendorContext,
    ICurrentUser currentUser,
    ITaxRepository repository,
    TimeProvider clock) : IRequestHandler<DeleteTaxFilingCommand, Unit>
{
    public async Task<Unit> Handle(DeleteTaxFilingCommand request, CancellationToken cancellationToken)
    {
        var vendorId = await vendorContext.RequireVendorIdAsync(cancellationToken);
        var removed = await repository.DeleteDeclarationAsync(
            vendorId, TaxStandings.UserId(currentUser), request.Year, request.PeriodType, request.PeriodNo,
            clock.GetUtcNow().UtcDateTime, cancellationToken);
        return removed ? Unit.Value : throw new NotFoundException(TaxMessages.DeclarationNotFound);
    }
}

#endregion

#region Exports

internal static class TaxBookExports
{
    public static async Task<TaxBookExport> BuildAsync(
        ITaxRepository repository, long vendorId, int? year, TimeProvider clock, CancellationToken cancellationToken)
    {
        var today = TaxStandings.Today(clock);
        var standing = await TaxStandings.LoadAsync(repository, vendorId, year, today, cancellationToken);
        var (ownerName, businessName) = await repository.GetVendorIdentityAsync(vendorId, cancellationToken);
        var yearStart = new DateOnly(standing.TaxYear, 1, 1);
        var yearEnd = new DateOnly(standing.TaxYear, 12, 31);
        var entries = await repository.ListEntriesAsync(
            vendorId, yearStart, today < yearEnd ? today : yearEnd, cancellationToken);
        var filings = await repository.ListDeclarationsAsync(vendorId, standing.TaxYear, cancellationToken);
        var periods = TaxPeriodViews.Build(standing.Policy, standing.TaxYear, today, standing.Days, filings);

        var days = standing.Days
            .OrderBy(day => day.Date)
            .Select(day =>
            {
                var channels = entries
                    .Where(entry => entry.Date == day.Date && entry.VoidedAt is null)
                    .Select(entry => ChannelLabel(entry.Channel))
                    .Distinct()
                    .ToList();
                var remarks = new List<string>();
                if (day.OnlineOrders > 0)
                {
                    remarks.Add($"{day.OnlineOrders} đơn hàng StreetBiz");
                }

                if (channels.Count > 0)
                {
                    remarks.Add("Bán trực tiếp: " + string.Join(", ", channels));
                }

                return new TaxBookDay(day.Date, day.Online, day.OnlineOrders, day.Recorded, string.Join("; ", remarks));
            })
            .ToList();

        return new TaxBookExport(
            standing.TaxYear,
            ownerName,
            businessName,
            standing.Profile?.TaxCode,
            standing.Industry.Name,
            clock.GetUtcNow().UtcDateTime,
            standing.Policy.RevenueThreshold,
            standing.Group,
            TaxGroups.Describe(standing.Group, standing.Policy),
            standing.Level,
            standing.Online,
            standing.Recorded,
            [.. TaxStandings.Months(standing.Days).Select(month => new TaxBookMonth(month.Month, month.Online, month.Recorded))],
            days,
            [.. entries.Select(entry => new TaxBookEntry(
                entry.Date, entry.Amount, ChannelLabel(entry.Channel), entry.Note, entry.CreatedAt,
                entry.VoidedAt is not null, entry.VoidReason))],
            [.. periods
                .Where(period => period.Required || period.Filing is not null)
                .Select(period => new TaxBookFiling(
                    period.Label, period.DueDate, period.Status, period.Filing?.RevenueDeclared, period.Filing?.FiledOn,
                    period.Filing?.AuthorityReference))]);
    }

    public static string ChannelLabel(string channel) => channel switch
    {
        RevenueChannels.Cash => "Tiền mặt",
        RevenueChannels.BankTransfer => "Chuyển khoản",
        RevenueChannels.Ewallet => "Ví điện tử",
        _ => "Khác",
    };
}

public sealed record ExportRevenueBookQuery(int? Year) : IRequest<FinanceFile>;

public sealed class ExportRevenueBookQueryValidator : AbstractValidator<ExportRevenueBookQuery>
{
    public ExportRevenueBookQueryValidator() =>
        RuleFor(x => x.Year).InclusiveBetween(2026, 2100).When(x => x.Year is not null);
}

public sealed class ExportRevenueBookQueryHandler(
    IVendorContext vendorContext,
    ITaxRepository repository,
    ITaxDocumentRenderer renderer,
    TimeProvider clock) : IRequestHandler<ExportRevenueBookQuery, FinanceFile>
{
    public async Task<FinanceFile> Handle(ExportRevenueBookQuery request, CancellationToken cancellationToken)
    {
        var vendorId = await vendorContext.RequireVendorIdAsync(cancellationToken);
        var book = await TaxBookExports.BuildAsync(repository, vendorId, request.Year, clock, cancellationToken);
        return new FinanceFile(
            $"so-doanh-thu-{book.TaxYear}.xlsx",
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            renderer.RenderRevenueBookXlsx(book));
    }
}

public sealed record ExportRevenueSummaryQuery(int? Year) : IRequest<FinanceFile>;

public sealed class ExportRevenueSummaryQueryValidator : AbstractValidator<ExportRevenueSummaryQuery>
{
    public ExportRevenueSummaryQueryValidator() =>
        RuleFor(x => x.Year).InclusiveBetween(2026, 2100).When(x => x.Year is not null);
}

public sealed class ExportRevenueSummaryQueryHandler(
    IVendorContext vendorContext,
    ITaxRepository repository,
    ITaxDocumentRenderer renderer,
    TimeProvider clock) : IRequestHandler<ExportRevenueSummaryQuery, FinanceFile>
{
    public async Task<FinanceFile> Handle(ExportRevenueSummaryQuery request, CancellationToken cancellationToken)
    {
        var vendorId = await vendorContext.RequireVendorIdAsync(cancellationToken);
        var book = await TaxBookExports.BuildAsync(repository, vendorId, request.Year, clock, cancellationToken);
        return new FinanceFile(
            $"tong-hop-doanh-thu-{book.TaxYear}.pdf",
            "application/pdf",
            renderer.RenderRevenueSummaryPdf(book, clock.GetUtcNow().UtcDateTime));
    }
}

#endregion
