using Microsoft.EntityFrameworkCore;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.Features.VendorKyc;
using StreetBiz.Infrastructure.Persistence.Repositories;
using StreetBiz.Infrastructure.Persistence.ScaffoldedModels;
using StreetBiz.Infrastructure.Tests.Support;
using static StreetBiz.Infrastructure.Tests.Support.SqliteTestDatabase;

namespace StreetBiz.Infrastructure.Tests;

/// <summary>SIDE-01 / SIDE-02 / SIDE-11: the vendor-facing slot map, slot detail and slot proposals.</summary>
public sealed class SidewalkSlotRepositoryTests
{
    private static readonly TestClock Clock = new(new DateTime(2026, 11, 1, 3, 0, 0));

    private static SlotSearchArea Area(bool includeUnavailable = false, int? zone = null, int? ward = null,
        decimal? minLat = null, decimal? maxLat = null) =>
        new(minLat, maxLat, null, null, ward, zone, includeUnavailable);

    [Fact]
    public async Task The_map_shows_only_available_slots_unless_asked_for_all()
    {
        using var db = await CreateAsync();
        var repository = new SidewalkSlotRepository(db.Db, Clock);

        var open = await repository.SearchAsync(Area(), default);
        var all = await repository.SearchAsync(Area(includeUnavailable: true), default);

        Assert.Equal(new[] { SlotFree, SlotFree2 }, open.Select(s => s.SlotId).OrderBy(x => x));
        Assert.Equal(3, all.Count);
        Assert.Equal("Quán A", all.Single(s => s.SlotId == SlotRented).TenantName);
        Assert.Null(all.Single(s => s.SlotId == SlotFree).TenantName);
    }

    [Fact]
    public async Task A_slot_with_an_open_application_reads_as_pending_and_leaves_the_open_map()
    {
        using var db = await CreateAsync();
        await new RentalApplicationRepository(db.Db).CreateAsync(RegistrationB, SlotFree, ApplicationMethods.ManualSelected, 30, null, default);
        var repository = new SidewalkSlotRepository(db.NewContext(), Clock);

        Assert.DoesNotContain(await repository.SearchAsync(Area(), default), s => s.SlotId == SlotFree);
        Assert.Equal(SlotStatuses.PendingApplication, (await repository.GetByIdAsync(SlotFree, default))!.SlotStatus);
    }

    [Fact]
    public async Task Bounding_box_zone_and_ward_filters_narrow_the_map()
    {
        using var db = await CreateAsync();
        var repository = new SidewalkSlotRepository(db.Db, Clock);

        Assert.Equal(new[] { SlotFree }, (await repository.SearchAsync(Area(minLat: 16.0611m, maxLat: 16.0620m), default)).Select(s => s.SlotId));
        Assert.Equal(2, (await repository.SearchAsync(Area(zone: Zone), default)).Count);
        Assert.Empty(await repository.SearchAsync(Area(zone: 99), default));
        Assert.Empty(await repository.SearchAsync(Area(ward: 99), default));
        Assert.Equal(2, (await repository.SearchAsync(Area(ward: Ward), default)).Count);
    }

    [Fact]
    public async Task Only_a_live_hold_is_reported()
    {
        using var db = await CreateAsync();
        db.Db.SlotHolds.AddRange(
            new SlotHold { slot_id = SlotFree, registration_id = RegistrationB, held_at = Clock.UtcNow.AddMinutes(-5), expires_at = Clock.UtcNow.AddMinutes(10) },
            new SlotHold { slot_id = SlotFree2, registration_id = RegistrationB, held_at = Clock.UtcNow.AddMinutes(-30), expires_at = Clock.UtcNow.AddMinutes(-15) });
        await db.Db.SaveChangesAsync();
        var repository = new SidewalkSlotRepository(db.Db, Clock);

        Assert.NotNull((await repository.GetByIdAsync(SlotFree, default))!.HoldExpiresAt);
        Assert.Null((await repository.GetByIdAsync(SlotFree2, default))!.HoldExpiresAt);
    }

    [Fact]
    public async Task A_proposal_is_pending_and_hidden_from_the_map_until_approved()
    {
        using var db = await CreateAsync();
        var repository = new SidewalkSlotRepository(db.Db, Clock);

        var id = await repository.ProposeAsync(RegistrationB, new NewSlotProposal(Zone, 16.0650m, 108.2200m, 1.5m, 2m, "/api/uploads/evidence/11/a.jpg"), "VP-1-TEST", default);

        var proposal = await repository.GetProposalByIdAsync(id, default);
        Assert.Equal(ProposalReviewStatuses.Pending, proposal!.ProposalReviewStatus);
        Assert.Equal("/api/uploads/evidence/11/a.jpg", proposal.ProposalPhotoUrl);
        Assert.Equal(new[] { id }, (await repository.ListProposalsByVendorAsync(VendorB, default)).Select(p => p.SlotId));
        Assert.Empty(await repository.ListProposalsByVendorAsync(VendorA, default));
        Assert.DoesNotContain(await repository.SearchAsync(Area(includeUnavailable: true), default), s => s.SlotId == id);

        var slot = await db.Db.SidewalkSlots.SingleAsync(s => s.slot_id == id);
        slot.proposal_review_status = ProposalReviewStatuses.Approved;
        await db.Db.SaveChangesAsync();
        Assert.Contains(await repository.SearchAsync(Area(), default), s => s.SlotId == id);
    }

    [Fact]
    public async Task Zone_existence_and_unknown_slots()
    {
        using var db = await CreateAsync();
        var repository = new SidewalkSlotRepository(db.Db, Clock);

        Assert.True(await repository.ZoneExistsAsync(Zone, default));
        Assert.False(await repository.ZoneExistsAsync(42, default));
        Assert.Null(await repository.GetByIdAsync(999, default));
        Assert.Null(await repository.GetProposalByIdAsync(999, default));
    }
}

/// <summary>SIDE-12 / SIDE-13: creating and moving a transfer request through its states.</summary>
public sealed class SlotTransferRequestRepositoryLifecycleTests
{
    [Fact]
    public async Task A_new_transfer_is_pending_open_and_stamped_by_the_database()
    {
        using var db = await CreateAsync();
        var repository = new SlotTransferRequestRepository(db.Db);

        var id = await repository.CreateAsync(ContractA, VendorA, VendorB, default);

        var row = await repository.GetByIdAsync(id, default);
        Assert.Equal(TransferStatuses.Pending, row!.TransferStatus);
        Assert.Equal("S-01", row.SlotCode);
        Assert.NotEqual(default, row.InitiatedAt);
        Assert.True(await repository.HasOpenForContractAsync(ContractA, default));
    }

    [Fact]
    public async Task Acceptance_keeps_it_open_and_a_decision_closes_it()
    {
        using var db = await CreateAsync();
        var repository = new SlotTransferRequestRepository(db.Db);
        var id = await repository.CreateAsync(ContractA, VendorA, VendorB, default);
        var acceptedAt = new DateTime(2026, 11, 2, 1, 0, 0, DateTimeKind.Utc);

        await repository.SetStatusAsync(id, TransferStatuses.AcceptedByReceiver, acceptedAt, default);
        Assert.True(await repository.HasOpenForContractAsync(ContractA, default));
        Assert.Equal(acceptedAt, (await repository.GetByIdAsync(id, default))!.AcceptedAt);

        await repository.SetStatusAsync(id, TransferStatuses.Rejected, null, default);
        Assert.False(await repository.HasOpenForContractAsync(ContractA, default));
        Assert.Equal(acceptedAt, (await repository.GetByIdAsync(id, default))!.AcceptedAt); // a null leaves it as it was

        await repository.SetStatusAsync(999, TransferStatuses.Approved, null, default); // unknown id: no-op
        Assert.Equal(TransferStatuses.Rejected, (await repository.GetByIdAsync(id, default))!.TransferStatus);
    }
}

/// <summary>AIC-01: recording eKYC results so the reviewing officer sees the latest check of each type.</summary>
public sealed class KycResultRepositoryTests
{
    private static KycIdCardExtraction Ocr(bool ai, params string[] warnings) =>
        new("048090000123", "NGUYEN VAN A", null, null, null, null, null, null, null, 92, false, warnings, ai, "ok");

    private static KycFaceMatchResult Face(bool ai, double similarity) =>
        new(similarity > 80, similarity, false, false, Array.Empty<string>(), ai, "ok");

    [Fact]
    public async Task A_provider_failure_is_not_recorded_as_a_result()
    {
        using var db = await CreateAsync();
        var repository = new KycResultRepository(db.Db, new TestClock(new DateTime(2026, 11, 1)));

        await repository.RecordIdCardCheckAsync(VendorB, Ocr(ai: false), default);
        await repository.RecordFaceMatchAsync(VendorB, Face(ai: false, 0), default);

        Assert.Equal(0, await db.NewContext().KycVerificationResults.CountAsync());
    }

    [Fact]
    public async Task Results_are_linked_to_the_registration_and_the_latest_of_each_type_wins()
    {
        using var db = await CreateAsync();
        var clock = new TestClock(new DateTime(2026, 11, 1, 1, 0, 0));
        var repository = new KycResultRepository(db.Db, clock);
        await repository.RecordIdCardCheckAsync(VendorB, Ocr(true, "Ảnh mờ"), default);
        clock.UtcNow = clock.UtcNow.AddMinutes(5);
        await repository.RecordIdCardCheckAsync(VendorB, Ocr(true), default);
        await repository.RecordFaceMatchAsync(VendorB, Face(true, 91.5), default);
        await repository.RecordIdCardCheckAsync(VendorC, Ocr(true), default); // someone else's

        Assert.Empty(await repository.ListForRegistrationAsync(RegistrationB, default));
        await repository.LinkPendingResultsAsync(VendorB, RegistrationB, default);
        await repository.LinkPendingResultsAsync(VendorB, RegistrationB, default); // nothing pending: no-op

        var checks = await new KycResultRepository(db.NewContext(), clock).ListForRegistrationAsync(RegistrationB, default);
        Assert.Equal(new[] { KycCheckTypes.FaceMatch, KycCheckTypes.IdCardOcr }, checks.Select(c => c.CheckType));
        var ocr = checks.Single(c => c.CheckType == KycCheckTypes.IdCardOcr);
        Assert.Null(ocr.Warnings); // the retry without warnings is the one shown
        Assert.Equal(92, ocr.ConfidencePercent);
        Assert.Equal(91.5m, checks.Single(c => c.CheckType == KycCheckTypes.FaceMatch).SimilarityPercent);
        Assert.Equal("FPT.AI", ocr.Provider);
    }

    [Fact]
    public async Task Very_long_warnings_are_cut_to_the_column_size()
    {
        using var db = await CreateAsync();
        var repository = new KycResultRepository(db.Db, new TestClock(new DateTime(2026, 11, 1)));

        await repository.RecordIdCardCheckAsync(VendorB, Ocr(true, new string('x', 700), new string('y', 700)), default);

        var stored = await db.NewContext().KycVerificationResults.SingleAsync();
        Assert.Equal(1000, stored.warnings!.Length);
    }
}
