using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.Features.Tax;
using StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

namespace StreetBiz.Infrastructure.Persistence.Repositories;

/// <summary>
/// Data access of the household tax module. Money is added up in memory rather than with SQL
/// aggregates: one household's year is a few thousand rows at most, and it keeps the same code
/// correct on SQL Server and on the SQLite the tests run on (which cannot sum decimals).
/// </summary>
public sealed class TaxRepository(StreetBizDbContext db) : ITaxRepository
{
    private const string WardInspection = "WARD_INSPECTION";

    // ------------------------------------------------------------------------------ policies

    public async Task<TaxPolicyRow?> GetPolicyAsync(int taxYear, CancellationToken cancellationToken)
    {
        var policy = await db.TaxPolicies.AsNoTracking()
            .Include(row => row.TaxIndustryRates)
            .Where(row => row.tax_year <= taxYear)
            .OrderByDescending(row => row.tax_year)
            .ThenByDescending(row => row.version_no)
            .FirstOrDefaultAsync(cancellationToken);
        return policy is null ? null : ToRow(policy);
    }

    public async Task<IReadOnlyList<TaxPolicyRow>> ListPoliciesAsync(CancellationToken cancellationToken) =>
        [.. (await db.TaxPolicies.AsNoTracking()
                .Include(row => row.TaxIndustryRates)
                .OrderByDescending(row => row.tax_year)
                .ThenByDescending(row => row.version_no)
                .ToListAsync(cancellationToken))
            .Select(ToRow)];

    public async Task<TaxPolicyRow> CreatePolicyAsync(
        long adminUserId, NewTaxPolicy policy, DateTime now, CancellationToken cancellationToken)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);

            var latest = await db.TaxPolicies
                .Where(row => row.tax_year == policy.TaxYear)
                .MaxAsync(row => (int?)row.version_no, cancellationToken) ?? 0;
            var entity = new TaxPolicy
            {
                tax_year = policy.TaxYear,
                version_no = latest + 1,
                legal_basis = policy.LegalBasis,
                revenue_threshold = policy.RevenueThreshold,
                einvoice_threshold = policy.EInvoiceThreshold,
                group2_ceiling = policy.Group2Ceiling,
                group3_ceiling = policy.Group3Ceiling,
                pit_deducts_threshold = policy.PitDeductsThreshold,
                pit_profit_rate_g2 = policy.PitProfitRateG2,
                pit_profit_rate_g3 = policy.PitProfitRateG3,
                pit_profit_rate_g4 = policy.PitProfitRateG4,
                annual_notice_month = (byte)policy.AnnualNoticeMonth,
                annual_notice_day = (byte)policy.AnnualNoticeDay,
                warn_ratio = policy.WarnRatio,
                note = policy.Note,
                created_by = adminUserId,
                created_at = now,
            };
            foreach (var rate in policy.Rates)
            {
                entity.TaxIndustryRates.Add(new TaxIndustryRate
                {
                    industry_code = rate.Code,
                    industry_name = rate.Name,
                    vat_rate = rate.VatRate,
                    pit_rate = rate.PitRate,
                    note = rate.Note,
                });
            }

            db.TaxPolicies.Add(entity);
            await db.SaveChangesAsync(cancellationToken);

            Audit(adminUserId, "TAX_POLICY_PUBLISHED", "TaxPolicy", entity.policy_id, now, new
            {
                entity.tax_year,
                entity.version_no,
                entity.revenue_threshold,
                entity.einvoice_threshold,
                entity.legal_basis,
            });
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return ToRow(entity);
        });
    }

    private static TaxPolicyRow ToRow(TaxPolicy policy) => new(
        policy.policy_id, policy.tax_year, policy.version_no, policy.legal_basis, policy.revenue_threshold,
        policy.einvoice_threshold, policy.group2_ceiling, policy.group3_ceiling, policy.pit_deducts_threshold,
        policy.pit_profit_rate_g2, policy.pit_profit_rate_g3, policy.pit_profit_rate_g4, policy.annual_notice_month,
        policy.annual_notice_day, policy.warn_ratio, policy.note, Utc(policy.created_at),
        [.. policy.TaxIndustryRates
            .OrderBy(rate => rate.rate_id)
            .Select(rate => new TaxIndustryRateRow(rate.industry_code, rate.industry_name, rate.vat_rate, rate.pit_rate, rate.note))]);

    // ------------------------------------------------------------------- household: profile

    public async Task<(string OwnerName, string? BusinessName)> GetVendorIdentityAsync(
        long vendorId, CancellationToken cancellationToken)
    {
        var owner = await db.Vendors.AsNoTracking()
            .Where(vendor => vendor.vendor_id == vendorId)
            .Select(vendor => vendor.user.full_name)
            .FirstOrDefaultAsync(cancellationToken);
        var names = await db.BusinessRegistrations.AsNoTracking()
            .Where(row => row.vendor_id == vendorId && row.registration_status == RegistrationStatuses.Approved)
            .OrderBy(row => row.reviewed_at ?? row.created_at)
            .ThenBy(row => row.registration_id)
            .Select(row => row.display_name)
            .ToListAsync(cancellationToken);
        return (owner ?? "Hộ kinh doanh", names.Count == 0 ? null : string.Join(" · ", names));
    }

    public async Task<VendorTaxProfileRow?> GetProfileAsync(long vendorId, CancellationToken cancellationToken)
    {
        var profile = await db.VendorTaxProfiles.AsNoTracking()
            .FirstOrDefaultAsync(row => row.vendor_id == vendorId, cancellationToken);
        return profile is null ? null : ToRow(profile);
    }

    public async Task<VendorTaxProfileRow> SaveProfileAsync(
        long vendorId,
        long actorUserId,
        string? taxCode,
        string industryCode,
        bool eInvoiceRegistered,
        bool shareRevenueWithWard,
        DateTime now,
        CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        var profile = await db.VendorTaxProfiles.FirstOrDefaultAsync(row => row.vendor_id == vendorId, cancellationToken);
        var shareChanged = profile is null ? shareRevenueWithWard : profile.share_revenue_with_ward != shareRevenueWithWard;
        if (profile is null)
        {
            profile = new VendorTaxProfile { vendor_id = vendorId, created_at = now };
            db.VendorTaxProfiles.Add(profile);
        }
        else
        {
            profile.updated_at = now;
        }

        profile.tax_code = taxCode;
        profile.industry_code = industryCode;
        profile.einvoice_registered = eInvoiceRegistered;
        if (shareChanged)
        {
            profile.share_revenue_with_ward = shareRevenueWithWard;
            profile.consent_updated_at = now;
            Audit(actorUserId, shareRevenueWithWard ? "TAX_REVENUE_SHARING_ON" : "TAX_REVENUE_SHARING_OFF",
                "VendorTaxProfile", vendorId, now, new { shareRevenueWithWard });
        }

        await db.SaveChangesAsync(cancellationToken);
        return ToRow(profile);
    }

    private static VendorTaxProfileRow ToRow(VendorTaxProfile profile) => new(
        profile.vendor_id, profile.tax_code, profile.industry_code, profile.einvoice_registered,
        profile.share_revenue_with_ward, Utc(profile.consent_updated_at));

    // ------------------------------------------------------------------------ revenue book

    public async Task<IReadOnlyList<RevenueDayRow>> GetDailyRevenueAsync(
        long vendorId, DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        var (orders, refunded) = await CompletedOrdersAsync([vendorId], from, to, cancellationToken);
        var entries = await db.RevenueEntries.AsNoTracking()
            .Where(row => row.vendor_id == vendorId && row.voided_at == null && row.entry_date >= from && row.entry_date <= to)
            .Select(row => new { row.entry_date, row.amount })
            .ToListAsync(cancellationToken);

        var online = orders
            .GroupBy(order => order.Day)
            .ToDictionary(group => group.Key, group => (
                Amount: group.Sum(order => NetOf(order, refunded)),
                Count: group.Count()));
        var recorded = entries
            .GroupBy(entry => entry.entry_date)
            .ToDictionary(group => group.Key, group => group.Sum(entry => entry.amount));

        return [.. online.Keys.Union(recorded.Keys)
            .OrderBy(day => day)
            .Select(day => new RevenueDayRow(
                day,
                online.TryGetValue(day, out var sale) ? sale.Amount : 0m,
                online.TryGetValue(day, out sale) ? sale.Count : 0,
                recorded.GetValueOrDefault(day)))];
    }

    public async Task<IReadOnlyList<RevenueEntryRow>> ListEntriesAsync(
        long vendorId, DateOnly from, DateOnly to, CancellationToken cancellationToken) =>
        [.. (await db.RevenueEntries.AsNoTracking()
                .Where(row => row.vendor_id == vendorId && row.entry_date >= from && row.entry_date <= to)
                .OrderBy(row => row.entry_date)
                .ThenBy(row => row.entry_id)
                .ToListAsync(cancellationToken))
            .Select(ToRow)];

    public async Task<RevenueEntryRow> AddEntryAsync(
        long vendorId,
        DateOnly date,
        decimal amount,
        string channel,
        string? note,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var entry = new RevenueEntry
        {
            vendor_id = vendorId,
            entry_date = date,
            amount = amount,
            channel = channel,
            note = note,
            created_at = now,
        };
        db.RevenueEntries.Add(entry);
        await db.SaveChangesAsync(cancellationToken);
        return ToRow(entry);
    }

    public async Task<RevenueEntryRow?> VoidEntryAsync(
        long vendorId, long actorUserId, long entryId, string? reason, DateTime now, CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        var entry = await db.RevenueEntries
            .FirstOrDefaultAsync(row => row.entry_id == entryId && row.vendor_id == vendorId, cancellationToken);
        if (entry is null)
        {
            return null;
        }

        if (entry.voided_at is not null)
        {
            throw new ConflictException(TaxMessages.EntryAlreadyVoided);
        }

        entry.voided_at = now;
        entry.void_reason = reason;
        Audit(actorUserId, "REVENUE_ENTRY_VOIDED", "RevenueEntry", entry.entry_id, now,
            new { entry.entry_date, entry.amount, entry.channel, reason });
        await db.SaveChangesAsync(cancellationToken);
        return ToRow(entry);
    }

    private static RevenueEntryRow ToRow(RevenueEntry entry) => new(
        entry.entry_id, entry.entry_date, entry.amount, entry.channel, entry.note, Utc(entry.created_at),
        Utc(entry.voided_at), entry.void_reason);

    public async Task<IReadOnlyList<YearRevenueRow>> GetYearRevenueAsync(
        IReadOnlyCollection<long> vendorIds, int taxYear, DateOnly? through, CancellationToken cancellationToken)
    {
        var yearStart = new DateOnly(taxYear, 1, 1);
        var yearEnd = new DateOnly(taxYear, 12, 31);
        var to = through is { } limit && limit < yearEnd ? limit : yearEnd;
        var ids = vendorIds.Distinct().ToList();
        if (ids.Count == 0 || to < yearStart)
        {
            return [];
        }

        var (orders, refunded) = await CompletedOrdersAsync(ids, yearStart, to, cancellationToken);
        var entries = await db.RevenueEntries.AsNoTracking()
            .Where(row => ids.Contains(row.vendor_id) && row.voided_at == null
                && row.entry_date >= yearStart && row.entry_date <= to)
            .Select(row => new { row.vendor_id, row.entry_date, row.amount })
            .ToListAsync(cancellationToken);

        var online = orders.GroupBy(order => order.VendorId).ToDictionary(
            group => group.Key,
            group => (Amount: group.Sum(order => NetOf(order, refunded)), First: group.Min(order => order.Day)));
        var recorded = entries.GroupBy(entry => entry.vendor_id).ToDictionary(
            group => group.Key,
            group => (Amount: group.Sum(entry => entry.amount), First: group.Min(entry => entry.entry_date)));

        return [.. online.Keys.Union(recorded.Keys)
            .Select(vendorId =>
            {
                var hasOnline = online.TryGetValue(vendorId, out var sale);
                var hasRecorded = recorded.TryGetValue(vendorId, out var entered);
                DateOnly? first = (hasOnline, hasRecorded) switch
                {
                    (true, true) => sale.First < entered.First ? sale.First : entered.First,
                    (true, false) => sale.First,
                    _ => entered.First,
                };
                return new YearRevenueRow(vendorId, hasOnline ? sale.Amount : 0m, hasRecorded ? entered.Amount : 0m, first);
            })];
    }

    private sealed record CompletedOrder(long OrderId, long VendorId, DateOnly Day, decimal Total);

    private async Task<(List<CompletedOrder> Orders, Dictionary<long, decimal> Refunded)> CompletedOrdersAsync(
        List<long> vendorIds, DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        var fromUtc = BusinessCalendar.StartOfDayUtc(from);
        var toUtc = BusinessCalendar.StartOfDayUtc(to.AddDays(1));
        var raw = await db.Orders.AsNoTracking()
            .Where(order => order.order_status == OrderStatuses.Completed
                && order.completed_at >= fromUtc && order.completed_at < toUtc
                && vendorIds.Contains(order.storefront.registration.vendor_id))
            .Select(order => new
            {
                order.order_id,
                VendorId = order.storefront.registration.vendor_id,
                order.completed_at,
                order.total_amount,
            })
            .ToListAsync(cancellationToken);
        var orders = raw
            .Select(order => new CompletedOrder(
                order.order_id, order.VendorId, BusinessCalendar.DateOf(order.completed_at!.Value), order.total_amount))
            .ToList();

        var orderIds = orders.Select(order => order.OrderId).ToList();
        var refunded = orderIds.Count == 0
            ? new Dictionary<long, decimal>()
            : (await db.RefundTransactions.AsNoTracking()
                    .Where(refund => orderIds.Contains(refund.order_id) && refund.refund_status == "SUCCESS")
                    .Select(refund => new { refund.order_id, refund.amount })
                    .ToListAsync(cancellationToken))
                .GroupBy(refund => refund.order_id)
                .ToDictionary(group => group.Key, group => group.Sum(refund => refund.amount));
        return (orders, refunded);
    }

    /// <summary>An order's revenue is what the customer paid less what was paid back, never below zero.</summary>
    private static decimal NetOf(CompletedOrder order, Dictionary<long, decimal> refunded) =>
        Math.Max(order.Total - refunded.GetValueOrDefault(order.OrderId), 0m);

    // ---------------------------------------------------------------------------- filings

    public async Task<IReadOnlyList<TaxDeclarationRow>> ListDeclarationsAsync(
        long vendorId, int taxYear, CancellationToken cancellationToken) =>
        [.. (await db.TaxDeclarations.AsNoTracking()
                .Where(row => row.vendor_id == vendorId && row.tax_year == taxYear)
                .OrderBy(row => row.period_type)
                .ThenBy(row => row.period_no)
                .ToListAsync(cancellationToken))
            .Select(ToRow)];

    public async Task<TaxDeclarationRow> SaveDeclarationAsync(
        long vendorId, long actorUserId, SaveTaxDeclaration declaration, DateTime now, CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        var period = (byte)declaration.PeriodNo;
        var row = await db.TaxDeclarations.FirstOrDefaultAsync(
            existing => existing.vendor_id == vendorId && existing.tax_year == declaration.TaxYear
                && existing.period_type == declaration.PeriodType && existing.period_no == period,
            cancellationToken);
        var isNew = row is null;
        if (row is null)
        {
            row = new TaxDeclaration
            {
                vendor_id = vendorId,
                tax_year = declaration.TaxYear,
                period_type = declaration.PeriodType,
                period_no = period,
                created_at = now,
            };
            db.TaxDeclarations.Add(row);
        }
        else
        {
            row.updated_at = now;
        }

        row.revenue_declared = declaration.RevenueDeclared;
        row.revenue_system = declaration.RevenueSystem;
        row.filed_on = declaration.FiledOn;
        row.authority_reference = declaration.AuthorityReference;
        row.receipt_url = declaration.ReceiptUrl;
        row.note = declaration.Note;
        await db.SaveChangesAsync(cancellationToken);

        Audit(actorUserId, isNew ? "TAX_FILING_CONFIRMED" : "TAX_FILING_UPDATED", "TaxDeclaration", row.declaration_id, now,
            new { declaration.TaxYear, declaration.PeriodType, declaration.PeriodNo, declaration.RevenueDeclared, declaration.RevenueSystem });
        await db.SaveChangesAsync(cancellationToken);
        return ToRow(row);
    }

    public async Task<bool> DeleteDeclarationAsync(
        long vendorId, long actorUserId, int taxYear, string periodType, int periodNo, DateTime now,
        CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        var period = (byte)periodNo;
        var row = await db.TaxDeclarations.FirstOrDefaultAsync(
            existing => existing.vendor_id == vendorId && existing.tax_year == taxYear
                && existing.period_type == periodType && existing.period_no == period,
            cancellationToken);
        if (row is null)
        {
            return false;
        }

        Audit(actorUserId, "TAX_FILING_RETRACTED", "TaxDeclaration", row.declaration_id, now,
            new { taxYear, periodType, periodNo, row.revenue_declared, row.authority_reference });
        db.TaxDeclarations.Remove(row);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    private static TaxDeclarationRow ToRow(TaxDeclaration row) => new(
        row.declaration_id, row.tax_year, row.period_type, row.period_no, row.revenue_declared, row.revenue_system,
        row.filed_on, row.authority_reference, row.receipt_url, row.note, Utc(row.created_at), Utc(row.updated_at));

    public async Task<IReadOnlySet<long>> ListFiledVendorsAsync(
        IReadOnlyCollection<long> vendorIds, int taxYear, string periodType, int periodNo, CancellationToken cancellationToken)
    {
        var ids = vendorIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            return new HashSet<long>();
        }

        var period = (byte)periodNo;
        var filed = await db.TaxDeclarations.AsNoTracking()
            .Where(row => ids.Contains(row.vendor_id) && row.tax_year == taxYear
                && row.period_type == periodType && row.period_no == period)
            .Select(row => row.vendor_id)
            .ToListAsync(cancellationToken);
        return filed.ToHashSet();
    }

    // ------------------------------------------------------------------------------- ward

    public async Task<string?> GetWardNameAsync(int wardId, CancellationToken cancellationToken) =>
        await db.AdministrativeUnits.AsNoTracking()
            .Where(unit => unit.unit_id == wardId)
            .Select(unit => unit.unit_name)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<WardHouseholdFacts>> ListWardHouseholdsAsync(
        int wardId, DateOnly today, CancellationToken cancellationToken)
    {
        var registrations = await db.BusinessRegistrations.AsNoTracking()
            .Where(row => row.ward_unit_id == wardId && row.registration_status == RegistrationStatuses.Approved)
            .Select(row => new
            {
                row.vendor_id,
                row.display_name,
                row.declared_address,
                row.business_line,
                row.vendor_type,
                row.reviewed_at,
                row.created_at,
                OwnerUserId = row.vendor.user_id,
                OwnerName = row.vendor.user.full_name,
                Phone = row.vendor.user.phone_number,
            })
            .ToListAsync(cancellationToken);
        if (registrations.Count == 0)
        {
            return [];
        }

        var vendorIds = registrations.Select(row => row.vendor_id).Distinct().ToList();
        var contracts = await db.RentalContracts.AsNoTracking()
            .Where(row => vendorIds.Contains(row.vendor_id))
            .Select(row => new { row.vendor_id, row.contract_id, row.contract_status, row.end_date, SlotCode = row.slot.slot_code })
            .ToListAsync(cancellationToken);
        var contractIds = contracts.Select(row => row.contract_id).ToList();
        var permits = contractIds.Count == 0
            ? []
            : await db.DigitalPermits.AsNoTracking()
                .Where(row => contractIds.Contains(row.contract_id) && row.permit_status != PermitStatuses.Revoked)
                .Select(row => new { row.contract_id, row.permit_status })
                .ToListAsync(cancellationToken);
        var inspections = await db.PermitScanLogs.AsNoTracking()
            .Where(row => row.scan_context == WardInspection && row.permit_id != null
                && vendorIds.Contains(row.permit!.contract.vendor_id))
            .Select(row => new { VendorId = row.permit!.contract.vendor_id, row.scanned_at })
            .ToListAsync(cancellationToken);
        var orders = await db.Orders.AsNoTracking()
            .Where(row => row.placed_at != null && vendorIds.Contains(row.storefront.registration.vendor_id))
            .Select(row => new { VendorId = row.storefront.registration.vendor_id, row.placed_at })
            .ToListAsync(cancellationToken);
        var profiles = (await db.VendorTaxProfiles.AsNoTracking()
                .Where(row => vendorIds.Contains(row.vendor_id))
                .ToListAsync(cancellationToken))
            .ToDictionary(row => row.vendor_id);

        var lastInspection = inspections.GroupBy(row => row.VendorId).ToDictionary(g => g.Key, g => g.Max(row => row.scanned_at));
        var lastOrder = orders.GroupBy(row => row.VendorId).ToDictionary(g => g.Key, g => g.Max(row => row.placed_at!.Value));

        return [.. registrations
            .GroupBy(row => row.vendor_id)
            .Select(group =>
            {
                var vendorId = group.Key;
                // In the order the registrations were approved, so names, address and line are stable.
                var ordered = group.OrderBy(row => row.reviewed_at ?? row.created_at).ThenBy(row => row.display_name).ToList();
                var first = ordered[0];
                var own = contracts.Where(row => row.vendor_id == vendorId).ToList();
                var active = own.Where(row => row.contract_status == ContractStatuses.Active && row.end_date >= today).ToList();
                var activeIds = active.Select(row => row.contract_id).ToHashSet();
                var activePermits = permits.Where(row => activeIds.Contains(row.contract_id)).Select(row => row.permit_status).ToList();
                var permitStatus = activePermits.Contains(PermitStatuses.Active) ? PermitStatuses.Active
                    : activePermits.Contains(PermitStatuses.Suspended) ? PermitStatuses.Suspended
                    : null;
                profiles.TryGetValue(vendorId, out var profile);
                return new WardHouseholdFacts(
                    vendorId,
                    first.OwnerUserId,
                    string.Join(" · ", ordered.Select(row => row.display_name).Distinct()),
                    first.OwnerName ?? "Chủ hộ",
                    first.Phone,
                    ordered.Select(row => row.declared_address).FirstOrDefault(address => !string.IsNullOrWhiteSpace(address)),
                    ordered.Select(row => row.business_line).FirstOrDefault(line => !string.IsNullOrWhiteSpace(line)),
                    ordered.Any(row => row.vendor_type == "FIXED_STOREFRONT") ? "FIXED_STOREFRONT" : "ITINERANT",
                    Utc(first.reviewed_at ?? first.created_at),
                    own.Count,
                    active.Count,
                    own.Count(row => row.contract_status == ContractStatuses.Suspended),
                    [.. active.Select(row => row.SlotCode).Distinct().Order()],
                    permitStatus,
                    lastInspection.TryGetValue(vendorId, out var inspected) ? Utc(inspected) : null,
                    lastOrder.TryGetValue(vendorId, out var lastSale) ? Utc(lastSale) : null,
                    profile?.tax_code,
                    profile?.einvoice_registered ?? false,
                    profile?.share_revenue_with_ward ?? false);
            })];
    }

    // --------------------------------------------------------------------------- referrals

    private sealed record Candidate(
        int WardId, long? VendorId, string Reason, string SourceType, long SourceId, string Summary, DateTime DetectedAt);

    public async Task<TaxReferralRefreshResult> RefreshReferralsAsync(
        int? wardId, DateTime now, int lookbackDays, CancellationToken cancellationToken)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);

            var since = now.AddDays(-lookbackDays);
            var today = BusinessCalendar.DateOf(now);
            var candidates = new List<Candidate>();
            candidates.AddRange(await ViolationCandidatesAsync(since, cancellationToken));
            candidates.AddRange(await ScanCandidatesAsync(since, cancellationToken));
            candidates.AddRange(await AddressChangeCandidatesAsync(since, cancellationToken));
            candidates.AddRange(await EndedContractCandidatesAsync(BusinessCalendar.DateOf(since), today, cancellationToken));
            candidates.AddRange(await NewHouseholdCandidatesAsync(since, cancellationToken));

            var existing = (await db.TaxReferrals.AsNoTracking()
                    .Select(row => new { row.reason_code, row.source_type, row.source_id })
                    .ToListAsync(cancellationToken))
                .Select(row => (row.reason_code, row.source_type, row.source_id))
                .ToHashSet();

            var created = 0;
            foreach (var candidate in candidates.Where(c => wardId is null || c.WardId == wardId))
            {
                if (!existing.Add((candidate.Reason, candidate.SourceType, candidate.SourceId)))
                {
                    continue;
                }

                db.TaxReferrals.Add(new TaxReferral
                {
                    ward_unit_id = candidate.WardId,
                    vendor_id = candidate.VendorId,
                    reason_code = candidate.Reason,
                    source_type = candidate.SourceType,
                    source_id = candidate.SourceId,
                    summary = Truncate(candidate.Summary, 500),
                    detected_at = candidate.DetectedAt,
                    created_at = now,
                    referral_status = TaxReferralStatuses.Pending,
                });
                created++;
            }

            // A household that has since given its tax code no longer needs a referral about it.
            var pendingNew = await db.TaxReferrals
                .Where(row => row.reason_code == TaxReferralReasons.NewHousehold
                    && row.referral_status == TaxReferralStatuses.Pending
                    && row.vendor_id != null
                    && (wardId == null || row.ward_unit_id == wardId))
                .ToListAsync(cancellationToken);
            var closed = 0;
            if (pendingNew.Count > 0)
            {
                var ids = pendingNew.Select(row => row.vendor_id!.Value).Distinct().ToList();
                var withCode = (await db.VendorTaxProfiles.AsNoTracking()
                        .Where(row => ids.Contains(row.vendor_id) && row.tax_code != null)
                        .Select(row => row.vendor_id)
                        .ToListAsync(cancellationToken))
                    .ToHashSet();
                foreach (var row in pendingNew.Where(row => withCode.Contains(row.vendor_id!.Value)))
                {
                    row.referral_status = TaxReferralStatuses.Dismissed;
                    row.handled_at = now;
                    row.handling_note = "Hộ đã khai mã số thuế trên StreetBiz.";
                    closed++;
                }
            }

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new TaxReferralRefreshResult(created, closed);
        });
    }

    private async Task<List<Candidate>> ViolationCandidatesAsync(DateTime since, CancellationToken cancellationToken)
    {
        var labels = await db.ViolationTypes.AsNoTracking()
            .ToDictionaryAsync(row => row.violation_type_code, row => row.description, cancellationToken);
        var rows = await db.Violations.AsNoTracking()
            .Where(row => row.recorded_at >= since
                && (((row.violation_type == "NO_PERMIT" || row.violation_type == "UNAUTHORIZED_BUSINESS_USE") && row.contract_id == null)
                    || row.violation_type == "EXPIRED_OR_INVALID_PERMIT"
                    || row.violation_type == "PERMIT_TAMPERING"))
            .Select(row => new
            {
                row.violation_id,
                row.violation_type,
                row.vendor_id,
                row.bien_ban_so,
                row.recorded_at,
                SlotCode = row.slot != null ? row.slot.slot_code : null,
                SlotWard = row.slot != null ? (int?)row.slot.zone.ward_unit_id : null,
                ContractWard = row.contract != null ? (int?)row.contract.slot.zone.ward_unit_id : null,
                RecorderWard = row.UserAccount != null ? row.UserAccount.ward_unit_id : null,
            })
            .ToListAsync(cancellationToken);

        return [.. rows
            .Select(row => (Row: row, Ward: row.SlotWard ?? row.ContractWard ?? row.RecorderWard))
            .Where(item => item.Ward is not null)
            .Select(item =>
            {
                var row = item.Row;
                var unlicensed = row.violation_type is "NO_PERMIT" or "UNAUTHORIZED_BUSINESS_USE";
                var label = labels.GetValueOrDefault(row.violation_type) ?? row.violation_type;
                var place = row.SlotCode is null ? "" : $" tại ô {row.SlotCode}";
                var who = row.vendor_id is null ? " Chưa xác định chủ hộ." : "";
                return new Candidate(
                    item.Ward!.Value,
                    row.vendor_id,
                    unlicensed ? TaxReferralReasons.UnregisteredActivity : TaxReferralReasons.InvalidPermitActivity,
                    "VIOLATION",
                    row.violation_id,
                    $"Biên bản {row.bien_ban_so ?? "#" + row.violation_id}: {label}{place}.{who}",
                    row.recorded_at);
            })];
    }

    private async Task<List<Candidate>> ScanCandidatesAsync(DateTime since, CancellationToken cancellationToken)
    {
        var rows = await db.PermitScanLogs.AsNoTracking()
            .Where(row => row.scan_context == WardInspection && row.scanned_at >= since
                && (row.scan_result == "NOT_FOUND" || row.scan_result == "REVOKED"
                    || row.scan_result == "EXPIRED" || row.scan_result == "SUSPENDED"))
            .Select(row => new
            {
                row.scan_id,
                row.scan_result,
                row.scanned_at,
                ScannerWard = row.UserAccount != null ? row.UserAccount.ward_unit_id : null,
                VendorId = row.permit != null ? (long?)row.permit.contract.vendor_id : null,
                SlotCode = row.permit != null ? row.permit.contract.slot.slot_code : null,
            })
            .ToListAsync(cancellationToken);

        return [.. rows
            .Where(row => row.ScannerWard is not null)
            .Select(row =>
            {
                var forged = row.scan_result == "NOT_FOUND";
                var place = row.SlotCode is null ? "" : $" tại ô {row.SlotCode}";
                var summary = forged
                    ? "Kiểm tra thực địa: mã QR giấy phép không có trong hệ thống (có thể là giấy phép giả hoặc đã huỷ)."
                    : $"Kiểm tra thực địa{place}: giấy phép {ScanResultLabel(row.scan_result)} nhưng vẫn đang kinh doanh.";
                return new Candidate(
                    row.ScannerWard!.Value,
                    row.VendorId,
                    forged ? TaxReferralReasons.UnregisteredActivity : TaxReferralReasons.InvalidPermitActivity,
                    "PERMIT_SCAN",
                    row.scan_id,
                    summary,
                    row.scanned_at);
            })];
    }

    private static string ScanResultLabel(string result) => result switch
    {
        "REVOKED" => "đã bị thu hồi",
        "EXPIRED" => "đã hết hạn",
        "SUSPENDED" => "đang bị đình chỉ",
        _ => "không hợp lệ",
    };

    private async Task<List<Candidate>> AddressChangeCandidatesAsync(DateTime since, CancellationToken cancellationToken)
    {
        var rows = await db.AddressChangeRequests.AsNoTracking()
            .Where(row => row.change_status == "APPROVED" && row.reviewed_at >= since)
            .Select(row => new
            {
                row.address_change_id,
                row.new_address,
                row.reviewed_at,
                WardId = row.registration.ward_unit_id,
                VendorId = row.registration.vendor_id,
                row.registration.display_name,
            })
            .ToListAsync(cancellationToken);
        return [.. rows.Select(row => new Candidate(
            row.WardId, row.VendorId, TaxReferralReasons.AddressChanged, "ADDRESS_CHANGE", row.address_change_id,
            $"Hộ {row.display_name} được duyệt đổi địa điểm kinh doanh sang: {row.new_address}.",
            row.reviewed_at!.Value))];
    }

    private async Task<List<Candidate>> EndedContractCandidatesAsync(
        DateOnly sinceDate, DateOnly today, CancellationToken cancellationToken)
    {
        var rows = await db.RentalContracts.AsNoTracking()
            .Where(row => row.contract_status == ContractStatuses.Expired
                || row.contract_status == ContractStatuses.Cancelled
                || row.contract_status == ContractStatuses.Revoked
                || (row.contract_status == ContractStatuses.Active && row.end_date < today))
            .Select(row => new
            {
                row.contract_id,
                row.vendor_id,
                row.contract_status,
                row.end_date,
                row.cancelled_at,
                row.updated_at,
                row.created_at,
                SlotCode = row.slot.slot_code,
                WardId = row.slot.zone.ward_unit_id,
                Name = row.application.registration.display_name,
            })
            .ToListAsync(cancellationToken);

        var ended = rows
            .Select(row => (Row: row, On: row.contract_status is ContractStatuses.Cancelled or ContractStatuses.Revoked
                ? BusinessCalendar.DateOf(row.cancelled_at ?? row.updated_at ?? row.created_at)
                : row.end_date))
            .Where(item => item.On >= sinceDate && item.On <= today)
            .ToList();
        if (ended.Count == 0)
        {
            return [];
        }

        // A household still holding another contract has not stopped trading.
        var vendorIds = ended.Select(item => item.Row.vendor_id).Distinct().ToList();
        var stillTrading = (await db.RentalContracts.AsNoTracking()
                .Where(row => vendorIds.Contains(row.vendor_id)
                    && ((row.contract_status == ContractStatuses.Active && row.end_date >= today)
                        || row.contract_status == ContractStatuses.Suspended))
                .Select(row => row.vendor_id)
                .ToListAsync(cancellationToken))
            .ToHashSet();

        return [.. ended
            .Where(item => !stillTrading.Contains(item.Row.vendor_id))
            .Select(item => new Candidate(
                item.Row.WardId,
                item.Row.vendor_id,
                TaxReferralReasons.BusinessEnded,
                "CONTRACT",
                item.Row.contract_id,
                $"Hộ {item.Row.Name} không còn hợp đồng thuê ô {item.Row.SlotCode} từ {TaxFormat.Day(item.On)} " +
                $"({ContractEndLabel(item.Row.contract_status)}) và không còn ô nào khác. Đề nghị cơ quan thuế cập nhật tình trạng hoạt động.",
                BusinessCalendar.StartOfDayUtc(item.On)))];
    }

    private static string ContractEndLabel(string status) => status switch
    {
        ContractStatuses.Cancelled => "trả ô",
        ContractStatuses.Revoked => "bị thu hồi",
        _ => "hết hạn",
    };

    private async Task<List<Candidate>> NewHouseholdCandidatesAsync(DateTime since, CancellationToken cancellationToken)
    {
        var rows = await db.BusinessRegistrations.AsNoTracking()
            .Where(row => row.registration_status == RegistrationStatuses.Approved && row.reviewed_at >= since)
            .Select(row => new
            {
                row.vendor_id,
                row.registration_id,
                row.display_name,
                row.ward_unit_id,
                row.reviewed_at,
                Owner = row.vendor.user.full_name,
            })
            .ToListAsync(cancellationToken);
        if (rows.Count == 0)
        {
            return [];
        }

        var vendorIds = rows.Select(row => row.vendor_id).Distinct().ToList();
        var withCode = (await db.VendorTaxProfiles.AsNoTracking()
                .Where(row => vendorIds.Contains(row.vendor_id) && row.tax_code != null)
                .Select(row => row.vendor_id)
                .ToListAsync(cancellationToken))
            .ToHashSet();

        return [.. rows
            .Where(row => !withCode.Contains(row.vendor_id))
            .GroupBy(row => row.vendor_id)
            .Select(group =>
            {
                var first = group.OrderBy(row => row.reviewed_at).First();
                return new Candidate(
                    first.ward_unit_id,
                    first.vendor_id,
                    TaxReferralReasons.NewHousehold,
                    "VENDOR",
                    first.vendor_id,
                    $"Hộ {first.display_name} (chủ hộ {first.Owner ?? "chưa rõ"}) được duyệt đăng ký ngày " +
                    $"{TaxFormat.Day(BusinessCalendar.DateOf(first.reviewed_at!.Value))} nhưng chưa khai mã số thuế. " +
                    "Đề nghị hướng dẫn đăng ký thuế.",
                    first.reviewed_at!.Value);
            })];
    }

    public async Task<IReadOnlyList<TaxReferralRow>> ListReferralsAsync(
        int wardId, string? status, CancellationToken cancellationToken)
    {
        var query = db.TaxReferrals.AsNoTracking().Where(row => row.ward_unit_id == wardId);
        if (status is not null)
        {
            query = query.Where(row => row.referral_status == status);
        }

        var rows = await query
            .OrderBy(row => row.referral_status == TaxReferralStatuses.Pending ? 0 : 1)
            .ThenByDescending(row => row.detected_at)
            .ToListAsync(cancellationToken);
        return await ToRowsAsync(rows, cancellationToken);
    }

    public async Task<TaxReferralRow?> HandleReferralAsync(
        int wardId,
        long referralId,
        long officerUserId,
        string status,
        string? authorityReference,
        string? note,
        DateTime now,
        CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        var row = await db.TaxReferrals.FirstOrDefaultAsync(
            referral => referral.referral_id == referralId && referral.ward_unit_id == wardId
                && referral.referral_status == TaxReferralStatuses.Pending,
            cancellationToken);
        if (row is null)
        {
            return null;
        }

        row.referral_status = status;
        row.handled_by = officerUserId;
        row.handled_at = now;
        row.authority_reference = authorityReference;
        row.handling_note = note;
        Audit(officerUserId,
            status == TaxReferralStatuses.Reported ? "TAX_REFERRAL_REPORTED" : "TAX_REFERRAL_DISMISSED",
            "TaxReferral", row.referral_id, now,
            new { row.reason_code, row.source_type, row.source_id, authorityReference, note });
        await db.SaveChangesAsync(cancellationToken);
        return (await ToRowsAsync([row], cancellationToken))[0];
    }

    private async Task<IReadOnlyList<TaxReferralRow>> ToRowsAsync(List<TaxReferral> rows, CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
        {
            return [];
        }

        var vendorIds = rows.Where(row => row.vendor_id is not null).Select(row => row.vendor_id!.Value).Distinct().ToList();
        var vendorNames = (await db.BusinessRegistrations.AsNoTracking()
                .Where(row => vendorIds.Contains(row.vendor_id))
                .OrderBy(row => row.registration_status == RegistrationStatuses.Approved ? 0 : 1)
                .ThenBy(row => row.registration_id)
                .Select(row => new { row.vendor_id, row.display_name })
                .ToListAsync(cancellationToken))
            .GroupBy(row => row.vendor_id)
            .ToDictionary(group => group.Key, group => group.First().display_name);
        var officerIds = rows.Where(row => row.handled_by is not null).Select(row => row.handled_by!.Value).Distinct().ToList();
        var officerNames = officerIds.Count == 0
            ? new Dictionary<long, string?>()
            : await db.UserAccounts.AsNoTracking()
                .Where(user => officerIds.Contains(user.user_id))
                .ToDictionaryAsync(user => user.user_id, user => user.full_name, cancellationToken);

        return [.. rows.Select(row => new TaxReferralRow(
            row.referral_id,
            row.ward_unit_id,
            row.vendor_id,
            row.vendor_id is { } vendorId ? vendorNames.GetValueOrDefault(vendorId) : null,
            row.reason_code,
            row.source_type,
            row.source_id,
            row.summary,
            Utc(row.detected_at),
            Utc(row.created_at),
            row.referral_status,
            row.handled_by,
            row.handled_by is { } officerId ? officerNames.GetValueOrDefault(officerId) : null,
            Utc(row.handled_at),
            row.authority_reference,
            row.handling_note))];
    }

    // ----------------------------------------------------------------------- announcements

    public async Task<IReadOnlyList<TaxAnnouncementRow>> ListAnnouncementsAsync(
        int wardId, CancellationToken cancellationToken)
    {
        var rows = await db.TaxAnnouncements.AsNoTracking()
            .Where(row => row.ward_unit_id == wardId)
            .OrderByDescending(row => row.created_at)
            .Take(100)
            .ToListAsync(cancellationToken);
        var creatorIds = rows.Select(row => row.created_by).Distinct().ToList();
        var names = await db.UserAccounts.AsNoTracking()
            .Where(user => creatorIds.Contains(user.user_id))
            .ToDictionaryAsync(user => user.user_id, user => user.full_name, cancellationToken);
        return [.. rows.Select(row => ToRow(row, names.GetValueOrDefault(row.created_by)))];
    }

    public async Task<IReadOnlyList<long>> ListRecipientUserIdsAsync(
        int wardId, string segment, int taxYear, CancellationToken cancellationToken)
    {
        var households = (await db.BusinessRegistrations.AsNoTracking()
                .Where(row => row.ward_unit_id == wardId && row.registration_status == RegistrationStatuses.Approved)
                .Select(row => new { row.vendor_id, UserId = row.vendor.user_id })
                .Distinct()
                .ToListAsync(cancellationToken));
        var vendorIds = households.Select(row => row.vendor_id).ToList();
        if (vendorIds.Count == 0)
        {
            return [];
        }

        IEnumerable<long> selected = vendorIds;
        if (segment == TaxAnnouncementSegments.NotFiled)
        {
            var filed = await ListFiledVendorsAsync(vendorIds, taxYear, TaxPeriodTypes.Year, 0, cancellationToken);
            selected = vendorIds.Where(id => !filed.Contains(id));
        }
        else if (segment == TaxAnnouncementSegments.NoTaxCode)
        {
            var withCode = (await db.VendorTaxProfiles.AsNoTracking()
                    .Where(row => vendorIds.Contains(row.vendor_id) && row.tax_code != null)
                    .Select(row => row.vendor_id)
                    .ToListAsync(cancellationToken))
                .ToHashSet();
            selected = vendorIds.Where(id => !withCode.Contains(id));
        }

        var chosen = selected.ToHashSet();
        return [.. households.Where(row => chosen.Contains(row.vendor_id)).Select(row => row.UserId).Distinct()];
    }

    public async Task<TaxAnnouncementRow> CreateAnnouncementAsync(
        int wardId,
        long officerUserId,
        string segment,
        int taxYear,
        string title,
        string body,
        IReadOnlyList<long> recipientUserIds,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);

            var announcement = new TaxAnnouncement
            {
                ward_unit_id = wardId,
                segment = segment,
                tax_year = taxYear,
                title = title,
                body = body,
                recipient_count = recipientUserIds.Count,
                created_by = officerUserId,
                created_at = now,
            };
            db.TaxAnnouncements.Add(announcement);
            await db.SaveChangesAsync(cancellationToken);

            foreach (var userId in recipientUserIds)
            {
                db.Notifications.Add(new Notification
                {
                    user_id = userId,
                    notification_type = "TAX",
                    title = title,
                    body = body,
                    related_entity_type = "TAX_ANNOUNCEMENT",
                    related_entity_id = announcement.announcement_id,
                    is_read = false,
                    sent_at = now,
                });
            }

            Audit(officerUserId, "TAX_ANNOUNCEMENT_SENT", "TaxAnnouncement", announcement.announcement_id, now,
                new { segment, taxYear, recipients = recipientUserIds.Count });
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            var creator = await db.UserAccounts.AsNoTracking()
                .Where(user => user.user_id == officerUserId)
                .Select(user => user.full_name)
                .FirstOrDefaultAsync(cancellationToken);
            return ToRow(announcement, creator);
        });
    }

    private static TaxAnnouncementRow ToRow(TaxAnnouncement row, string? creatorName) => new(
        row.announcement_id, row.ward_unit_id, row.segment, row.tax_year, row.title, row.body, row.recipient_count,
        creatorName, Utc(row.created_at));

    // SQL Server hands DATETIME2 back as Unspecified. Every column here is written in UTC, so say so
    // rather than letting a client read the value as local wall-clock time.
    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private static DateTime? Utc(DateTime? value) => value is { } moment ? Utc(moment) : null;

    // ------------------------------------------------------------------------------ sweeps

    public async Task<IReadOnlyList<(long VendorId, long UserId)>> ListTaxableVendorsAsync(CancellationToken cancellationToken) =>
        [.. (await db.BusinessRegistrations.AsNoTracking()
                .Where(row => row.registration_status == RegistrationStatuses.Approved)
                .Select(row => new { row.vendor_id, UserId = row.vendor.user_id })
                .Distinct()
                .ToListAsync(cancellationToken))
            .Select(row => (row.vendor_id, row.UserId))];

    public async Task<bool> NotifyOnceAsync(
        long userId,
        string type,
        string title,
        string body,
        string relatedType,
        long relatedId,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var exists = await db.Notifications.AsNoTracking().AnyAsync(
            row => row.user_id == userId && row.notification_type == type
                && row.related_entity_type == relatedType && row.related_entity_id == relatedId,
            cancellationToken);
        if (exists)
        {
            return false;
        }

        db.Notifications.Add(new Notification
        {
            user_id = userId,
            notification_type = type,
            title = Truncate(title, 200),
            body = Truncate(body, 1000),
            related_entity_type = relatedType,
            related_entity_id = relatedId,
            is_read = false,
            sent_at = now,
        });
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    // ------------------------------------------------------------------------------ helpers

    private void Audit(long actorUserId, string action, string entityType, long entityId, DateTime now, object details) =>
        db.AuditLogs.Add(new AuditLog
        {
            actor_user_id = actorUserId,
            action = action,
            entity_type = entityType,
            entity_id = entityId,
            details = JsonSerializer.Serialize(details),
            created_at = now,
        });

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..(max - 1)] + "…";
}
