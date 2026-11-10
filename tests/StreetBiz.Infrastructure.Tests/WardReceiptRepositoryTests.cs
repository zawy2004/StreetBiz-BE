using StreetBiz.Infrastructure.Persistence.Repositories;
using static StreetBiz.Infrastructure.Tests.PayeeTestFixture;
using FeeScheduleItem = StreetBiz.Infrastructure.Persistence.ScaffoldedModels.FeeScheduleItem;
using Invoice = StreetBiz.Infrastructure.Persistence.ScaffoldedModels.Invoice;
using PaymentTransaction = StreetBiz.Infrastructure.Persistence.ScaffoldedModels.PaymentTransaction;

namespace StreetBiz.Infrastructure.Tests;

/// <summary>
/// "Lịch sử thu": the ward's receipts with the payment behind each one, and the check that a
/// receipt belongs to the ward before its PDF is handed out.
/// </summary>
public sealed class WardReceiptRepositoryTests
{
    private const long FeeInvoice = 101;
    private const long FineInvoice = 102;
    private const long OctoberInvoice = 103;
    private static readonly DateOnly November1 = new(2026, 11, 1);
    private static readonly DateOnly November2 = new(2026, 11, 2);

    /// <summary>
    /// The fee of slot S-01 paid by payOS (after a failed attempt, and with a second payment owed
    /// back), the fine of the same slot paid to the Treasury and refunded after its decision was
    /// annulled, and an October fee receipt outside the period.
    /// </summary>
    private static async Task<PayeeTestFixture> Seeded()
    {
        var f = await Create();
        var db = f.Db;
        db.FeeScheduleItems.Add(new FeeScheduleItem
        {
            fee_item_id = 4, fee_schedule_id = 1, due_date = new DateOnly(2026, 10, 10), amount = 1_000_000m,
            item_status = "PAID", paid_at = new DateTime(2026, 10, 5, 3, 0, 0, DateTimeKind.Utc),
        });
        db.Invoices.AddRange(
            new Invoice
            {
                invoice_id = FeeInvoice, invoice_number = "HD-2026-000101", fee_item_id = FeeItem, vendor_id = Vendor,
                amount = 1_040_000m, issued_at = Now.UtcDateTime.AddDays(-1),
            },
            new Invoice
            {
                invoice_id = FineInvoice, invoice_number = "HD-2026-000102", penalty_id = Penalty, vendor_id = Vendor,
                amount = 500_000m, issued_at = Now.UtcDateTime,
            },
            new Invoice
            {
                invoice_id = OctoberInvoice, invoice_number = "HD-2026-000090", fee_item_id = 4, vendor_id = Vendor,
                amount = 1_000_000m, issued_at = new DateTime(2026, 10, 5, 3, 0, 0, DateTimeKind.Utc),
            });
        db.PaymentTransactions.AddRange(
            Payment("RENTAL_FEE", FeeItem, null, "PAYOS", "PAYOS-11001", "FAILED"),
            Payment("RENTAL_FEE", FeeItem, null, "PAYOS", "PAYOS-11002", "SUCCESS"),
            Payment("RENTAL_FEE", FeeItem, null, "MOMO", "MOMO-DUP-1", "REFUND_REQUIRED"),
            Payment("PENALTY", null, Penalty, "TREASURY", "KB-2026-1", "REFUNDED"));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return f;
    }

    private static PaymentTransaction Payment(
        string purpose, long? feeItemId, long? penaltyId, string provider, string reference, string status) => new()
    {
        idempotency_key = $"key-{Guid.NewGuid():N}", payment_purpose = purpose, fee_item_id = feeItemId, penalty_id = penaltyId,
        provider = provider, provider_reference = reference, amount = 1_040_000m, transaction_status = status,
        created_at = Now.UtcDateTime.AddDays(-1),
    };

    [Fact]
    public async Task Lists_the_periods_receipts_newest_first_with_the_payment_behind_each()
    {
        using var f = await Seeded();

        var rows = await new WardReportRepository(f.Db).ListWardReceiptsAsync(Ward, November1, November2, default);

        Assert.Equal([FineInvoice, FeeInvoice], rows.Select(r => r.InvoiceId));
        var fee = rows[1];
        Assert.Equal("FEE", fee.Kind);
        Assert.Equal("HD-2026-000101", fee.InvoiceNumber);
        Assert.Equal("User 7", fee.PayerName);
        Assert.Equal("0900000007", fee.PayerPhone);
        Assert.Equal("S-01", fee.SlotCode);
        Assert.StartsWith("Phí thuê ô S-01 — Kỳ", fee.Description);
        Assert.Equal(1_040_000m, fee.Amount);
        // The applied payment, not the failed attempt nor the second one owed back.
        Assert.Equal("PAYOS", fee.PaymentProvider);
        Assert.Equal("PAYOS-11002", fee.ProviderReference);
        Assert.Equal("SUCCESS", fee.PaymentStatus);

        var fine = rows[0];
        Assert.Equal("PENALTY", fine.Kind);
        Assert.Equal("Tiền phạt: Cản trở lối đi bộ (QĐ QĐXP-2026/0001)", fine.Description);
        Assert.Equal("TREASURY", fine.PaymentProvider);
        Assert.Equal("KB-2026-1", fine.ProviderReference);
        Assert.Equal("REFUNDED", fine.PaymentStatus);
    }

    [Fact]
    public async Task Keeps_to_the_period_and_to_the_ward()
    {
        using var f = await Seeded();
        var reports = new WardReportRepository(f.Db);

        var october = await reports.ListWardReceiptsAsync(Ward, new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31), default);
        var otherWard = await reports.ListWardReceiptsAsync(OtherWard, November1, November2, default);

        Assert.Equal([OctoberInvoice], october.Select(r => r.InvoiceId));
        Assert.Null(october[0].PaymentProvider); // no payment record behind it in this fixture
        Assert.Empty(otherWard);
    }

    [Fact]
    public async Task The_export_reads_the_same_receipts_oldest_first()
    {
        using var f = await Seeded();

        var rows = await new WardReportRepository(f.Db).ListWardInvoicesAsync(Ward, November1, November2, default);

        Assert.Equal(["HD-2026-000101", "HD-2026-000102"], rows.Select(r => r.InvoiceNumber));
        Assert.Equal(["PAYOS", "TREASURY"], rows.Select(r => r.PaymentProvider));
        Assert.Equal("User 7", rows[0].PayerName);
    }

    [Fact]
    public async Task Names_the_household_of_a_receipt_only_for_the_receipts_own_ward()
    {
        using var f = await Seeded();
        var reports = new WardReportRepository(f.Db);

        Assert.Equal(Vendor, await reports.GetReceiptVendorAsync(Ward, FeeInvoice, default));
        Assert.Equal(Vendor, await reports.GetReceiptVendorAsync(Ward, FineInvoice, default));
        Assert.Null(await reports.GetReceiptVendorAsync(OtherWard, FeeInvoice, default));
        Assert.Null(await reports.GetReceiptVendorAsync(Ward, 999, default));
    }
}
