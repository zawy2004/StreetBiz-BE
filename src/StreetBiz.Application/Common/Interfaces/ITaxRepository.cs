using StreetBiz.Application.Common.Models;

namespace StreetBiz.Application.Common.Interfaces;

/// <summary>
/// Data access of the household tax module. Every method that takes a vendor id or a ward id
/// scopes to it itself, so a handler cannot read another household's or ward's rows by passing a
/// foreign id. The rules (groups, projection, estimate, calendar) are not here: they are pure
/// functions in Features/Tax/TaxRules.cs.
/// </summary>
public interface ITaxRepository
{
    // ---- policies -------------------------------------------------------------------------

    /// <summary>
    /// The policy in force for a tax year: its highest version, else the latest version of the most
    /// recent earlier year (a year nobody has configured yet inherits the last known rules). Null
    /// when no policy was ever published.
    /// </summary>
    Task<TaxPolicyRow?> GetPolicyAsync(int taxYear, CancellationToken cancellationToken);

    /// <summary>Every published version, newest year and version first.</summary>
    Task<IReadOnlyList<TaxPolicyRow>> ListPoliciesAsync(CancellationToken cancellationToken);

    Task<TaxPolicyRow> CreatePolicyAsync(
        long adminUserId, NewTaxPolicy policy, DateTime now, CancellationToken cancellationToken);

    // ---- household: profile, revenue book, filings ---------------------------------------

    /// <summary>The owner's name and the business name(s) of the household's approved registrations.</summary>
    Task<(string OwnerName, string? BusinessName)> GetVendorIdentityAsync(
        long vendorId, CancellationToken cancellationToken);

    Task<VendorTaxProfileRow?> GetProfileAsync(long vendorId, CancellationToken cancellationToken);

    /// <summary>Creates or updates the profile; a change of the sharing choice is audited.</summary>
    Task<VendorTaxProfileRow> SaveProfileAsync(
        long vendorId,
        long actorUserId,
        string? taxCode,
        string industryCode,
        bool eInvoiceRegistered,
        bool shareRevenueWithWard,
        DateTime now,
        CancellationToken cancellationToken);

    /// <summary>Days (Vietnamese calendar, inclusive) that had sales; days without any are absent.</summary>
    Task<IReadOnlyList<RevenueDayRow>> GetDailyRevenueAsync(
        long vendorId, DateOnly from, DateOnly to, CancellationToken cancellationToken);

    /// <summary>The recorded entries of a range, voided ones included (marked), oldest first.</summary>
    Task<IReadOnlyList<RevenueEntryRow>> ListEntriesAsync(
        long vendorId, DateOnly from, DateOnly to, CancellationToken cancellationToken);

    Task<RevenueEntryRow> AddEntryAsync(
        long vendorId,
        DateOnly date,
        decimal amount,
        string channel,
        string? note,
        DateTime now,
        CancellationToken cancellationToken);

    /// <summary>Voids an entry of this household; false when it is not theirs or already void.</summary>
    Task<RevenueEntryRow?> VoidEntryAsync(
        long vendorId, long actorUserId, long entryId, string? reason, DateTime now, CancellationToken cancellationToken);

    Task<IReadOnlyList<TaxDeclarationRow>> ListDeclarationsAsync(
        long vendorId, int taxYear, CancellationToken cancellationToken);

    Task<TaxDeclarationRow> SaveDeclarationAsync(
        long vendorId, long actorUserId, SaveTaxDeclaration declaration, DateTime now, CancellationToken cancellationToken);

    Task<bool> DeleteDeclarationAsync(
        long vendorId, long actorUserId, int taxYear, string periodType, int periodNo, DateTime now,
        CancellationToken cancellationToken);

    // ---- many households (ward views, sweeps) --------------------------------------------

    /// <summary>
    /// Revenue of several households for a tax year in one query (households with none are absent),
    /// from 1 January through <c>through</c> (null: the whole year, or up to today for the current one).
    /// </summary>
    Task<IReadOnlyList<YearRevenueRow>> GetYearRevenueAsync(
        IReadOnlyCollection<long> vendorIds, int taxYear, DateOnly? through, CancellationToken cancellationToken);

    /// <summary>Which of these households have a confirmed filing for the period.</summary>
    Task<IReadOnlySet<long>> ListFiledVendorsAsync(
        IReadOnlyCollection<long> vendorIds, int taxYear, string periodType, int periodNo,
        CancellationToken cancellationToken);

    // ---- ward ------------------------------------------------------------------------------

    Task<string?> GetWardNameAsync(int wardId, CancellationToken cancellationToken);

    /// <summary>The households with an approved registration in the ward, with the facts their status is derived from.</summary>
    Task<IReadOnlyList<WardHouseholdFacts>> ListWardHouseholdsAsync(
        int wardId, DateOnly today, CancellationToken cancellationToken);

    /// <summary>
    /// Finds what the ward should pass on to the tax office from its own records and adds the new
    /// ones (existing rows, and the officer's decision on them, are untouched). <c>wardId</c> null
    /// refreshes every ward. Pending "new household" rows whose household has since given a tax
    /// code are closed by the system.
    /// </summary>
    Task<TaxReferralRefreshResult> RefreshReferralsAsync(
        int? wardId, DateTime now, int lookbackDays, CancellationToken cancellationToken);

    Task<IReadOnlyList<TaxReferralRow>> ListReferralsAsync(
        int wardId, string? status, CancellationToken cancellationToken);

    /// <summary>Marks a pending referral of this ward reported or dismissed; null when it is not pending here.</summary>
    Task<TaxReferralRow?> HandleReferralAsync(
        int wardId,
        long referralId,
        long officerUserId,
        string status,
        string? authorityReference,
        string? note,
        DateTime now,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<TaxAnnouncementRow>> ListAnnouncementsAsync(int wardId, CancellationToken cancellationToken);

    /// <summary>The user ids of the households a segment of the ward's announcement reaches.</summary>
    Task<IReadOnlyList<long>> ListRecipientUserIdsAsync(
        int wardId, string segment, int taxYear, CancellationToken cancellationToken);

    /// <summary>Stores the announcement and gives every recipient a notification, in one transaction.</summary>
    Task<TaxAnnouncementRow> CreateAnnouncementAsync(
        int wardId,
        long officerUserId,
        string segment,
        int taxYear,
        string title,
        string body,
        IReadOnlyList<long> recipientUserIds,
        DateTime now,
        CancellationToken cancellationToken);

    // ---- sweeps ----------------------------------------------------------------------------

    /// <summary>Households with an approved registration, with the user to notify.</summary>
    Task<IReadOnlyList<(long VendorId, long UserId)>> ListTaxableVendorsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Sends a notification unless the user already has one with the same type and related record,
    /// so a daily sweep never repeats itself. True when one was created.
    /// </summary>
    Task<bool> NotifyOnceAsync(
        long userId,
        string type,
        string title,
        string body,
        string relatedType,
        long relatedId,
        DateTime now,
        CancellationToken cancellationToken);
}
