using System.Text.RegularExpressions;
using FluentValidation;
using MediatR;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;

namespace StreetBiz.Application.Features.Tax;

// The platform administrator publishes the parameters the whole tax module runs on. A published
// version is never edited (a household's earlier figures must stay explainable): a correction is a
// new version of the same year, and the highest version is the one in force.

/// <summary>A published version, and whether it is the one in force for its year.</summary>
public sealed record TaxPolicyVersionDto(TaxPolicyDto Policy, bool InForce);

public sealed record ListTaxPoliciesQuery : IRequest<IReadOnlyList<TaxPolicyVersionDto>>;

public sealed class ListTaxPoliciesQueryHandler(
    IPlatformAdminContext adminContext,
    ITaxRepository repository) : IRequestHandler<ListTaxPoliciesQuery, IReadOnlyList<TaxPolicyVersionDto>>
{
    public async Task<IReadOnlyList<TaxPolicyVersionDto>> Handle(
        ListTaxPoliciesQuery request, CancellationToken cancellationToken)
    {
        await adminContext.RequireAsync(cancellationToken);
        var policies = await repository.ListPoliciesAsync(cancellationToken);
        var latest = policies.GroupBy(policy => policy.TaxYear).ToDictionary(group => group.Key, group => group.Max(p => p.VersionNo));
        return [.. policies.Select(policy => new TaxPolicyVersionDto(
            TaxPolicyDto.From(policy, policy.TaxYear), latest[policy.TaxYear] == policy.VersionNo))];
    }
}

public sealed record CreateTaxPolicyCommand(CreateTaxPolicyRequest Request) : IRequest<TaxPolicyVersionDto>;

public sealed partial class CreateTaxPolicyCommandValidator : AbstractValidator<CreateTaxPolicyCommand>
{
    private const decimal MaxMoney = 1_000_000_000_000m;

    public CreateTaxPolicyCommandValidator()
    {
        RuleFor(x => x.Request.TaxYear).InclusiveBetween(2026, 2100).WithMessage(TaxMessages.PolicyYearRange);
        RuleFor(x => x.Request.LegalBasis).NotEmpty().WithMessage("Nhập căn cứ pháp lý của các tham số này.").MaximumLength(500);
        RuleFor(x => x.Request.Note).MaximumLength(1000);

        RuleFor(x => x.Request.RevenueThreshold).Money("Ngưỡng doanh thu");
        RuleFor(x => x.Request.EInvoiceThreshold).Money("Ngưỡng hoá đơn điện tử");
        RuleFor(x => x.Request.Group2Ceiling).Money("Trần nhóm 2");
        RuleFor(x => x.Request.Group3Ceiling).Money("Trần nhóm 3");
        RuleFor(x => x.Request)
            .Must(request => request.Group2Ceiling > request.RevenueThreshold && request.Group3Ceiling > request.Group2Ceiling)
            .WithName("Group2Ceiling")
            .WithMessage("Các mốc phải tăng dần: ngưỡng < trần nhóm 2 < trần nhóm 3.");

        RuleFor(x => x.Request.PitProfitRateG2).InclusiveBetween(0m, 1m);
        RuleFor(x => x.Request.PitProfitRateG3).InclusiveBetween(0m, 1m);
        RuleFor(x => x.Request.PitProfitRateG4).InclusiveBetween(0m, 1m);
        RuleFor(x => x.Request.WarnRatio).GreaterThan(0m).LessThan(1m)
            .WithMessage("Tỷ lệ cảnh báo phải lớn hơn 0 và nhỏ hơn 1 (ví dụ 0,8 là 80% ngưỡng).");

        RuleFor(x => x.Request.AnnualNoticeMonth).InclusiveBetween(1, 12);
        RuleFor(x => x.Request.AnnualNoticeDay).InclusiveBetween(1, 31);
        RuleFor(x => x.Request)
            .Must(request => request.AnnualNoticeMonth is < 1 or > 12
                || request.AnnualNoticeDay <= MaxDayOf(request.AnnualNoticeMonth))
            .WithName("AnnualNoticeDay")
            .WithMessage("Ngày không tồn tại trong tháng đã chọn.");

        RuleFor(x => x.Request.Rates).NotEmpty().WithMessage(TaxMessages.PolicyNeedsRates);
        RuleFor(x => x.Request.Rates)
            .Must(rates => rates.Select(rate => rate.Code).Distinct(StringComparer.Ordinal).Count() == rates.Count
                && rates.Any(rate => rate.Code == "FOOD_SERVICE")
                && rates.Any(rate => rate.Code == "OTHER"))
            .When(x => x.Request.Rates is { Count: > 0 })
            .WithMessage(TaxMessages.PolicyNeedsRates);
        RuleForEach(x => x.Request.Rates).ChildRules(rate =>
        {
            rate.RuleFor(r => r.Code).Matches(CodePattern()).WithMessage("Mã ngành gồm chữ in hoa, số và dấu gạch dưới (2-30 ký tự).");
            rate.RuleFor(r => r.Name).NotEmpty().MaximumLength(200);
            rate.RuleFor(r => r.VatRate).InclusiveBetween(0m, 1m);
            rate.RuleFor(r => r.PitRate).InclusiveBetween(0m, 1m);
            rate.RuleFor(r => r.Note).MaximumLength(300);
        });
    }

    private static int MaxDayOf(int month) => month switch
    {
        2 => 28,
        4 or 6 or 9 or 11 => 30,
        _ => 31,
    };

    [GeneratedRegex("^[A-Z0-9_]{2,30}$")]
    private static partial Regex CodePattern();
}

internal static class TaxValidationRules
{
    public static IRuleBuilderOptions<T, decimal> Money<T>(this IRuleBuilder<T, decimal> rule, string name) =>
        rule.GreaterThan(0).WithMessage($"{name} phải lớn hơn 0.")
            .LessThanOrEqualTo(1_000_000_000_000m).WithMessage($"{name} quá lớn.")
            .Must(value => value == decimal.Truncate(value)).WithMessage($"{name} tính bằng đồng, không có phần lẻ.");
}

public sealed class CreateTaxPolicyCommandHandler(
    IPlatformAdminContext adminContext,
    ITaxRepository repository,
    TimeProvider clock) : IRequestHandler<CreateTaxPolicyCommand, TaxPolicyVersionDto>
{
    public async Task<TaxPolicyVersionDto> Handle(CreateTaxPolicyCommand request, CancellationToken cancellationToken)
    {
        var admin = await adminContext.RequireAsync(cancellationToken);
        var body = request.Request;
        var created = await repository.CreatePolicyAsync(
            admin.UserId,
            new NewTaxPolicy(
                body.TaxYear, body.LegalBasis.Trim(), body.RevenueThreshold, body.EInvoiceThreshold, body.Group2Ceiling,
                body.Group3Ceiling, body.PitDeductsThreshold, body.PitProfitRateG2, body.PitProfitRateG3,
                body.PitProfitRateG4, body.AnnualNoticeMonth, body.AnnualNoticeDay, body.WarnRatio,
                string.IsNullOrWhiteSpace(body.Note) ? null : body.Note.Trim(),
                [.. body.Rates.Select(rate => new TaxIndustryRateRow(
                    rate.Code, rate.Name.Trim(), rate.VatRate, rate.PitRate,
                    string.IsNullOrWhiteSpace(rate.Note) ? null : rate.Note.Trim()))]),
            clock.GetUtcNow().UtcDateTime,
            cancellationToken);

        // A new version is the highest of its year the moment it exists.
        return new TaxPolicyVersionDto(TaxPolicyDto.From(created, created.TaxYear), true);
    }
}
