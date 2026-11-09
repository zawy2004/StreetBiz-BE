using Microsoft.EntityFrameworkCore;
using StreetBiz.Application.Common.Security;
using StreetBiz.Infrastructure.Persistence.Repositories;
using StreetBiz.Infrastructure.Persistence.ScaffoldedModels;
using StreetBiz.Infrastructure.Tests.Support;
using static StreetBiz.Infrastructure.Tests.Support.SqliteTestDatabase;

namespace StreetBiz.Infrastructure.Tests;

/// <summary>SIDE-04 / SIDE-05 / SIDE-06 / SIDE-07: the rental application, renewal and contract repositories.</summary>
public sealed class RentalApplicationRepositoryTests
{
    [Fact]
    public async Task A_new_application_starts_pending_with_its_commitment_time()
    {
        using var db = await CreateAsync();
        var repository = new RentalApplicationRepository(db.Db);
        var accepted = new DateTime(2026, 10, 1, 3, 0, 0, DateTimeKind.Utc);

        var id = await repository.CreateAsync(RegistrationB, SlotFree, ApplicationMethods.ManualSelected, 60, accepted, default);

        var row = await repository.GetByIdAsync(id, default);
        Assert.NotNull(row);
        Assert.Equal(ApplicationStatuses.Pending, row!.ApplicationStatus);
        Assert.Equal(SlotFree, row.SlotId);
        Assert.Equal(60, row.RequestedTermDays);
        Assert.Equal(VendorB, row.VendorId);
        var stored = await db.NewContext().RentalApplications.SingleAsync(a => a.application_id == id);
        Assert.Equal(accepted, stored.commitments_accepted_at);
    }

    [Fact]
    public async Task Only_pending_or_under_review_applications_hold_a_slot()
    {
        using var db = await CreateAsync();
        var repository = new RentalApplicationRepository(db.Db);

        Assert.False(await repository.HasOpenApplicationForSlotAsync(SlotFree, default));
        var id = await repository.CreateAsync(RegistrationB, SlotFree, ApplicationMethods.ManualSelected, 30, null, default);
        Assert.True(await repository.HasOpenApplicationForSlotAsync(SlotFree, default));

        await repository.SetStatusAsync(id, ApplicationStatuses.UnderReview, default);
        Assert.True(await repository.HasOpenApplicationForSlotAsync(SlotFree, default));

        await repository.SetStatusAsync(id, ApplicationStatuses.Withdrawn, default);
        Assert.False(await repository.HasOpenApplicationForSlotAsync(SlotFree, default));
        Assert.False(await repository.HasOpenApplicationForSlotAsync(SlotRented, default)); // the seeded one is APPROVED
    }

    [Fact]
    public async Task A_vendor_lists_only_its_own_applications_newest_first()
    {
        using var db = await CreateAsync();
        var repository = new RentalApplicationRepository(db.Db);
        var first = await repository.CreateAsync(RegistrationB, SlotFree, ApplicationMethods.ManualSelected, 30, null, default);
        await Task.Delay(20);
        var second = await repository.CreateAsync(RegistrationB, SlotFree2, ApplicationMethods.ManualSelected, 30, null, default);

        var mine = await repository.ListByVendorAsync(VendorB, default);
        var theirs = await repository.ListByVendorAsync(VendorA, default);

        Assert.Equal(new[] { second, first }, mine.Select(r => r.ApplicationId));
        Assert.Equal(new[] { ApplicationA }, theirs.Select(r => r.ApplicationId));
        Assert.Empty(await repository.ListByVendorAsync(VendorC, default));
    }

    [Fact]
    public async Task Unknown_ids_read_as_null_and_status_changes_on_them_are_ignored()
    {
        using var db = await CreateAsync();
        var repository = new RentalApplicationRepository(db.Db);

        Assert.Null(await repository.GetByIdAsync(999, default));
        await repository.SetStatusAsync(999, ApplicationStatuses.Rejected, default);
        Assert.Equal(1, await db.NewContext().RentalApplications.CountAsync());
    }
}

public sealed class RenewalRequestRepositoryTests
{
    [Fact]
    public async Task A_renewal_request_is_created_pending_without_a_new_end_date()
    {
        using var db = await CreateAsync();
        var repository = new RenewalRequestRepository(db.Db);

        var id = await repository.CreateAsync(ContractA, 90, default);

        var row = await repository.GetByIdAsync(id, default);
        Assert.Equal(RenewalStatuses.Pending, row!.RenewalStatus);
        Assert.Equal(90, row.RequestedTermDays);
        Assert.Null(row.NewEndDate);
        Assert.True(await repository.HasOpenAsync(ContractA, default));
    }

    [Fact]
    public async Task Withdrawing_closes_an_open_request_once()
    {
        using var db = await CreateAsync();
        var repository = new RenewalRequestRepository(db.Db);
        var id = await repository.CreateAsync(ContractA, 30, default);

        Assert.True(await repository.WithdrawAsync(id, default));
        Assert.False(await repository.WithdrawAsync(id, default));
        Assert.False(await repository.WithdrawAsync(999, default));
        Assert.False(await repository.HasOpenAsync(ContractA, default));
        Assert.Equal(RenewalStatuses.Withdrawn, (await repository.GetByIdAsync(id, default))!.RenewalStatus);
    }

    [Fact]
    public async Task Closing_a_contracts_open_requests_leaves_decided_ones_alone()
    {
        using var db = await CreateAsync();
        db.Db.RenewalRequests.AddRange(
            new RenewalRequest { renewal_id = 1, contract_id = ContractA, requested_term_days = 30, renewal_status = RenewalStatuses.Approved },
            new RenewalRequest { renewal_id = 2, contract_id = ContractA, requested_term_days = 30, renewal_status = RenewalStatuses.UnderReview });
        await db.Db.SaveChangesAsync();
        var repository = new RenewalRequestRepository(db.NewContext());

        await repository.CloseOpenForContractAsync(ContractA, default);
        await repository.CloseOpenForContractAsync(ContractA, default); // nothing open any more: a no-op

        var rows = await repository.ListByContractAsync(ContractA, default);
        Assert.Equal(RenewalStatuses.Approved, rows.Single(r => r.RenewalId == 1).RenewalStatus);
        Assert.Equal(RenewalStatuses.Withdrawn, rows.Single(r => r.RenewalId == 2).RenewalStatus);
    }

    [Fact]
    public async Task Requests_are_listed_per_contract()
    {
        using var db = await CreateAsync();
        var repository = new RenewalRequestRepository(db.Db);
        await repository.CreateAsync(ContractA, 30, default);

        Assert.Single(await repository.ListByContractAsync(ContractA, default));
        Assert.Empty(await repository.ListByContractAsync(999, default));
        Assert.Null(await repository.GetByIdAsync(999, default));
    }
}

public sealed class RentalContractRepositoryTests
{
    [Fact]
    public async Task Contracts_list_with_slot_zone_and_whether_a_permit_exists()
    {
        using var db = await CreateAsync();
        var repository = new RentalContractRepository(db.Db);

        var before = Assert.Single(await repository.ListByVendorAsync(VendorA, null, default));
        Assert.Equal("S-01", before.SlotCode);
        Assert.Equal("Nguyễn Văn Linh", before.ZoneName);
        Assert.False(before.HasPermit);

        db.Db.DigitalPermits.Add(new DigitalPermit { contract_id = ContractA, qr_payload = "SBP1.test", permit_status = "ACTIVE" });
        await db.Db.SaveChangesAsync();

        Assert.True(Assert.Single(await repository.ListByVendorAsync(VendorA, null, default)).HasPermit);
        Assert.Single(await repository.ListByVendorAsync(VendorA, ContractStatuses.Active, default));
        Assert.Empty(await repository.ListByVendorAsync(VendorA, ContractStatuses.Cancelled, default));
        Assert.Empty(await repository.ListByVendorAsync(VendorB, null, default));
    }

    [Fact]
    public async Task Only_an_active_auto_adjacent_contract_counts_as_the_adjacent_slot()
    {
        using var db = await CreateAsync();
        var repository = new RentalContractRepository(db.Db);

        Assert.True(await repository.HasActiveAdjacentContractAsync(RegistrationA, default));
        Assert.False(await repository.HasActiveAdjacentContractAsync(RegistrationB, default));

        await repository.CancelAsync(ContractA, VendorA, "Chuyển địa điểm", default);
        Assert.False(await repository.HasActiveAdjacentContractAsync(RegistrationA, default));
    }

    [Fact]
    public async Task An_overdue_fee_or_unpaid_penalty_is_outstanding_debt()
    {
        using var db = await CreateAsync();
        var repository = new RentalContractRepository(db.Db);
        Assert.False(await repository.HasOutstandingDebtAsync(ContractA, default));

        var item = SeedSchedule(db, (new DateOnly(2026, 9, 1), DebtStatuses.FeeItemPaid), (new DateOnly(2026, 10, 1), DebtStatuses.FeeItemOverdue))[1];
        await db.Db.SaveChangesAsync();
        Assert.True(await repository.HasOutstandingDebtAsync(ContractA, default));

        item.item_status = DebtStatuses.FeeItemPaid;
        db.Db.ViolationTypes.Add(new ViolationType { violation_type_code = "OUTSIDE_SLOT", description = "x", is_active = true });
        db.Db.PenaltyFeeSchedules.Add(new PenaltyFeeSchedule { penalty_schedule_id = 1, ward_unit_id = Ward, violation_type = "OUTSIDE_SLOT", penalty_amount = 500000, effective_from = new DateOnly(2026, 1, 1), created_by = Officer });
        db.Db.Violations.Add(new Violation { violation_id = 1, contract_id = ContractA, vendor_id = VendorA, violation_type = "OUTSIDE_SLOT", recorded_by = Officer, source = "ON_SITE" });
        db.Db.Penalties.Add(new Penalty { penalty_id = 1, violation_id = 1, penalty_schedule_id = 1, amount = 500000, penalty_status = DebtStatuses.PenaltyUnpaid });
        await db.Db.SaveChangesAsync();
        Assert.True(await repository.HasOutstandingDebtAsync(ContractA, default));
    }

    [Fact]
    public async Task Cancelling_frees_the_slot_and_drops_only_future_pending_instalments()
    {
        using var db = await CreateAsync();
        SeedSchedule(db, (new DateOnly(2026, 9, 1), DebtStatuses.FeeItemPaid), (new DateOnly(2026, 10, 1), DebtStatuses.FeeItemPending), (new DateOnly(2026, 11, 1), DebtStatuses.FeeItemPending));
        await db.Db.SaveChangesAsync();
        db.Db.ChangeTracker.Clear();

        await new RentalContractRepository(db.Db).CancelAsync(ContractA, VendorA, "Trả ô", default);

        var read = db.NewContext();
        var contract = await read.RentalContracts.Include(c => c.slot).SingleAsync(c => c.contract_id == ContractA);
        Assert.Equal(ContractStatuses.Cancelled, contract.contract_status);
        Assert.Equal("Trả ô", contract.cancellation_reason);
        Assert.Equal(VendorA, contract.cancelled_by);
        Assert.NotNull(contract.cancelled_at);
        Assert.Equal(SlotStatuses.Available, contract.slot.slot_status);
        var items = await read.FeeScheduleItems.OrderBy(i => i.due_date).Select(i => i.item_status).ToListAsync();
        Assert.Equal(new[] { DebtStatuses.FeeItemPaid, DebtStatuses.FeeItemCancelled, DebtStatuses.FeeItemCancelled }, items);
    }

    [Fact]
    public async Task Cancelling_an_unknown_contract_does_nothing()
    {
        using var db = await CreateAsync();
        var repository = new RentalContractRepository(db.Db);

        await repository.CancelAsync(999, VendorA, null, default);

        Assert.Null(await repository.GetByIdAsync(999, default));
        Assert.Equal(ContractStatuses.Active, (await repository.GetByIdAsync(ContractA, default))!.ContractStatus);
    }

    private static List<FeeScheduleItem> SeedSchedule(SqliteTestDatabase db, params (DateOnly Due, string Status)[] items)
    {
        var schedule = new FeeSchedule { contract_id = ContractA, revision = 1, total_amount = 900000 * items.Length };
        var rows = items.Select(i => new FeeScheduleItem { due_date = i.Due, amount = 900000, item_status = i.Status }).ToList();
        foreach (var row in rows)
        {
            schedule.FeeScheduleItems.Add(row);
        }

        db.Db.FeeSchedules.Add(schedule);
        return rows;
    }
}
