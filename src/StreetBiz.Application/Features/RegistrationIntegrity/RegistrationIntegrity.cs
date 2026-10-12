using System.Globalization;
using System.Text;
using MediatR;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.Features.WardSlots;

namespace StreetBiz.Application.Features.RegistrationIntegrity;

// AIC-03: duplicate / suspicious applications. Given one registration, look for the same CCCD or
// address filed through another account, a burst of filings, and the same person competing with
// themselves for one slot. The result is a warning for the officer (BR-41), never a decision:
// nothing here rejects, holds or flags a registration in the database.

public static class IntegritySeverities
{
    public const string None = "NONE";
    public const string Low = "LOW";
    public const string Medium = "MEDIUM";
    public const string High = "HIGH";

    public static int Rank(string severity) => severity switch
    {
        High => 3,
        Medium => 2,
        Low => 1,
        _ => 0,
    };
}

public static class IntegrityFlagCodes
{
    public const string IdSharedAcrossVendors = "ID_SHARED_ACROSS_VENDORS";
    public const string AddressSharedAcrossVendors = "ADDRESS_SHARED_ACROSS_VENDORS";
    public const string RegistrationBurst = "REGISTRATION_BURST";
    public const string ManyOpenApplications = "MANY_OPEN_APPLICATIONS";
    public const string SlotContestedBySameId = "SLOT_CONTESTED_BY_SAME_ID";
}

// ---- what the reader finds in the database -------------------------------------------------

public sealed record RelatedRegistrationFact(
    long RegistrationId,
    string BusinessName,
    int WardId,
    string WardName,
    string Status,
    string? Note = null);

public sealed record RegistrationIntegrityFacts(
    long RegistrationId,
    string? IdNumber,
    string? DeclaredAddress,
    IReadOnlyList<RelatedRegistrationFact> SameIdOtherVendors,
    IReadOnlyList<RelatedRegistrationFact> SameAddressOtherVendors,
    int RegistrationsByVendorAroundThisOne,
    int OpenApplications,
    IReadOnlyList<RelatedRegistrationFact> SlotsContestedBySameId);

public interface IRegistrationIntegrityReader
{
    /// <summary>The facts for a registration of the officer's ward, or null when it is not one.</summary>
    Task<RegistrationIntegrityFacts?> GetFactsAsync(int wardId, long registrationId, CancellationToken cancellationToken);
}

// ---- what the officer sees -----------------------------------------------------------------

/// <summary>A registration another flag points at. Outside the officer's ward only the ward and status are shown.</summary>
public sealed record RelatedRegistrationDto(
    long? RegistrationId,
    string? BusinessName,
    string WardName,
    string Status,
    string? Note);

public sealed record IntegrityFlagDto(
    string Code,
    string Severity,
    string Title,
    string Detail,
    IReadOnlyList<RelatedRegistrationDto> Related);

public sealed record RegistrationIntegrityDto(
    long RegistrationId,
    string Level,
    bool HasIdNumber,
    IReadOnlyList<IntegrityFlagDto> Flags,
    string Basis);

// ---- the rules -----------------------------------------------------------------------------

public static class RegistrationIntegrityRules
{
    public const string Basis =
        "Đối chiếu tự động theo số CCCD, địa chỉ và lịch sử nộp hồ sơ. Đây chỉ là cảnh báo để cán bộ kiểm tra; " +
        "hệ thống không tự từ chối hay giữ hồ sơ.";

    /// <summary>Registrations by one account within the window that start to look like a burst.</summary>
    public const int BurstThreshold = 3;

    /// <summary>Days either side of the registration that the burst is counted over.</summary>
    public const int BurstWindowDays = 30;

    /// <summary>Open rental applications on one registration that start to look like hoarding.</summary>
    public const int OpenApplicationsThreshold = 3;

    private static readonly string[] DeadStatuses =
    [
        RegistrationStatuses.Rejected,
        RegistrationStatuses.Withdrawn,
        RegistrationStatuses.Draft,
    ];

    public static RegistrationIntegrityDto Evaluate(RegistrationIntegrityFacts facts, int officerWardId)
    {
        var flags = new List<IntegrityFlagDto>();

        if (facts.SameIdOtherVendors.Count > 0)
        {
            // A rejected or withdrawn filing under another account is still worth a look, but it
            // is not the same thing as two live registrations on one identity card.
            var live = facts.SameIdOtherVendors.Any(r => !DeadStatuses.Contains(r.Status));
            var count = facts.SameIdOtherVendors.Count;
            flags.Add(new IntegrityFlagDto(
                IntegrityFlagCodes.IdSharedAcrossVendors,
                live ? IntegritySeverities.High : IntegritySeverities.Medium,
                "Số CCCD đã dùng ở tài khoản khác",
                live
                    ? $"Số CCCD của hồ sơ này cũng xuất hiện ở {count} hồ sơ thuộc tài khoản khác, trong đó có hồ sơ còn hiệu lực."
                    : $"Số CCCD của hồ sơ này từng xuất hiện ở {count} hồ sơ thuộc tài khoản khác (đã bị từ chối, rút hoặc chưa nộp).",
                Related(facts.SameIdOtherVendors, officerWardId)));
        }

        if (facts.SameAddressOtherVendors.Count > 0)
        {
            flags.Add(new IntegrityFlagDto(
                IntegrityFlagCodes.AddressSharedAcrossVendors,
                IntegritySeverities.Medium,
                "Địa chỉ kinh doanh trùng với hồ sơ khác",
                $"Địa chỉ khai báo trùng với {facts.SameAddressOtherVendors.Count} hồ sơ đang xử lý hoặc đã duyệt của tài khoản khác. " +
                "Có thể là cùng toà nhà hoặc cùng chợ; nên đối chiếu tại chỗ.",
                Related(facts.SameAddressOtherVendors, officerWardId)));
        }

        if (facts.RegistrationsByVendorAroundThisOne >= BurstThreshold)
        {
            flags.Add(new IntegrityFlagDto(
                IntegrityFlagCodes.RegistrationBurst,
                IntegritySeverities.Low,
                "Tài khoản nộp nhiều hồ sơ trong thời gian ngắn",
                $"Tài khoản này đã tạo {facts.RegistrationsByVendorAroundThisOne} hồ sơ trong khoảng {BurstWindowDays} ngày quanh hồ sơ này.",
                []));
        }

        if (facts.OpenApplications >= OpenApplicationsThreshold)
        {
            flags.Add(new IntegrityFlagDto(
                IntegrityFlagCodes.ManyOpenApplications,
                IntegritySeverities.Medium,
                "Nhiều đơn thuê ô cùng lúc",
                $"Hồ sơ này đang có {facts.OpenApplications} đơn thuê ô chờ xử lý cùng lúc.",
                []));
        }

        if (facts.SlotsContestedBySameId.Count > 0)
        {
            flags.Add(new IntegrityFlagDto(
                IntegrityFlagCodes.SlotContestedBySameId,
                IntegritySeverities.High,
                "Cùng một CCCD xin chung một ô qua hai tài khoản",
                "Có hồ sơ khác dùng cùng số CCCD đang xin hoặc đã được cấp chính ô mà hồ sơ này xin.",
                Related(facts.SlotsContestedBySameId, officerWardId)));
        }

        var ordered = flags.OrderByDescending(f => IntegritySeverities.Rank(f.Severity)).ToList();
        var level = ordered.Count == 0 ? IntegritySeverities.None : ordered[0].Severity;
        return new RegistrationIntegrityDto(
            facts.RegistrationId, level, !string.IsNullOrWhiteSpace(facts.IdNumber), ordered, Basis);
    }

    private static IReadOnlyList<RelatedRegistrationDto> Related(
        IReadOnlyList<RelatedRegistrationFact> facts, int officerWardId) =>
        facts.Select(f => f.WardId == officerWardId
                ? new RelatedRegistrationDto(f.RegistrationId, f.BusinessName, f.WardName, f.Status, f.Note)
                // Another ward's household is not this officer's to read: the ward and status are
                // enough to tell them where to ask.
                : new RelatedRegistrationDto(null, null, f.WardName, f.Status, f.Note))
            .ToList();
}

/// <summary>Reduces an address to letters and digits so "12 Lê Duẩn, P. Hải Châu" and "12 le duan p hai chau" compare equal.</summary>
public static class AddressKey
{
    public static string Normalize(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return string.Empty;
        }

        var decomposed = address.Trim().ToLowerInvariant().Replace('đ', 'd').Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        var pendingSpace = false;
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(ch))
            {
                if (pendingSpace && builder.Length > 0)
                {
                    builder.Append(' ');
                }

                builder.Append(ch);
                pendingSpace = false;
            }
            else
            {
                pendingSpace = true;
            }
        }

        return builder.ToString();
    }
}

// ---- the use case --------------------------------------------------------------------------

public sealed record GetRegistrationIntegrityQuery(long RegistrationId) : IRequest<RegistrationIntegrityDto>;

public sealed class GetRegistrationIntegrityQueryHandler(
    IWardActorContext wardActorContext,
    IRegistrationIntegrityReader reader) : IRequestHandler<GetRegistrationIntegrityQuery, RegistrationIntegrityDto>
{
    public async Task<RegistrationIntegrityDto> Handle(
        GetRegistrationIntegrityQuery request, CancellationToken cancellationToken)
    {
        var actor = await wardActorContext.RequireAsync(cancellationToken);
        var facts = await reader.GetFactsAsync(actor.WardId, request.RegistrationId, cancellationToken)
            ?? throw new NotFoundException("Registration not found.");
        return RegistrationIntegrityRules.Evaluate(facts, actor.WardId);
    }
}
