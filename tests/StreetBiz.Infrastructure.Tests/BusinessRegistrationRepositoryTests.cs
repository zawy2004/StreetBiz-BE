using Microsoft.EntityFrameworkCore;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;
using StreetBiz.Infrastructure.Persistence.Repositories;
using StreetBiz.Infrastructure.Tests.Support;
using static StreetBiz.Infrastructure.Tests.Support.SqliteTestDatabase;

namespace StreetBiz.Infrastructure.Tests;

/// <summary>REG-01..REG-06: a registration from draft to decision, its evidence and the fast-track flag.</summary>
public sealed class BusinessRegistrationRepositoryTests
{
    private static readonly TestClock Clock = new(new DateTime(2026, 11, 1, 3, 0, 0));

    private static NewBizRegistration Data(string type = VendorTypes.FixedStorefront, string name = "Quán C", bool foodSafety = true,
        params NewHouseholdMember[] members) =>
        new(type, name, "12 Lê Duẩn", 16.06m, 108.21m, Ward, new DateOnly(1990, 5, 1), "FEMALE", "Kinh", "Việt Nam", "CCCD",
            new DateOnly(2021, 6, 1), "Cục CS QLHC", "12 Lê Duẩn", null, "Bánh mì", "5610", 20_000_000m, 1, new DateOnly(2026, 12, 1),
            foodSafety, members);

    private static async Task<(SqliteTestDatabase Db, BusinessRegistrationRepository Repo, long Id)> Draft(params NewHouseholdMember[] members)
    {
        var db = await CreateAsync();
        var repo = new BusinessRegistrationRepository(db.Db, Clock);
        var id = await repo.CreateAsync(VendorC, Data(members: members), default);
        return (db, repo, id);
    }

    [Fact]
    public async Task A_new_registration_is_a_draft_with_its_owner_business_and_household_data()
    {
        var (db, repo, id) = await Draft(new NewHouseholdMember("Nguyễn Văn B", null, "048090000999", "Chồng", 5_000_000m));
        using var _ = db;

        var row = (await new BusinessRegistrationRepository(db.NewContext(), Clock).GetByIdAsync(id, default))!;

        Assert.Equal(RegistrationStatuses.Draft, row.RegistrationStatus);
        Assert.False(row.FastTrackFlag);
        Assert.Equal("Bánh mì", row.BusinessLine);
        Assert.Equal(20_000_000m, row.CapitalAmount);
        Assert.Equal(Clock.UtcNow, row.FoodSafetyCommitmentAt);
        Assert.Equal("Nguyễn Văn B", Assert.Single(row.HouseholdMembersOrEmpty).FullName);
        Assert.Equal(new[] { id }, (await repo.ListByVendorAsync(VendorC, default)).Select(r => r.RegistrationId));
        Assert.Null(await repo.GetByIdAsync(999, default));
    }

    [Fact]
    public async Task Only_submitted_or_under_review_files_count_as_pending()
    {
        var (db, repo, id) = await Draft();
        using var _ = db;

        Assert.False(await repo.HasActivePendingAsync(VendorC, default));
        await repo.SetStatusAsync(id, RegistrationStatuses.Submitted, default);
        Assert.True(await repo.HasActivePendingAsync(VendorC, default));
        Assert.False(await repo.HasActivePendingAsync(VendorC, default, excludeRegistrationId: id));
        await repo.SetStatusAsync(id, RegistrationStatuses.UnderReview, default);
        Assert.True(await repo.HasActivePendingAsync(VendorC, default));
        await repo.SetStatusAsync(999, RegistrationStatuses.Approved, default); // unknown id: ignored
    }

    [Theory]
    [InlineData(RegistrationStatuses.Draft, RegistrationStatuses.Submitted, true)]
    [InlineData(RegistrationStatuses.Draft, RegistrationStatuses.Approved, false)]
    [InlineData(RegistrationStatuses.Rejected, RegistrationStatuses.Submitted, false)]
    [InlineData(RegistrationStatuses.Withdrawn, RegistrationStatuses.Approved, false)]
    [InlineData(RegistrationStatuses.Approved, RegistrationStatuses.Withdrawn, true)]
    public async Task Status_changes_follow_the_transition_table(string from, string to, bool allowed)
    {
        var (db, repo, id) = await Draft();
        using var _ = db;
        await repo.SetStatusAsync(id, from, default);

        if (allowed)
        {
            Assert.True(await repo.TryTransitionAsync(id, to, default));
            Assert.Equal(to, (await new BusinessRegistrationRepository(db.NewContext(), Clock).GetByIdAsync(id, default))!.RegistrationStatus);
        }
        else
        {
            await Assert.ThrowsAsync<DomainRuleException>(() => repo.TryTransitionAsync(id, to, default));
        }
    }

    [Fact]
    public async Task Transitioning_an_unknown_registration_is_not_found()
    {
        var (db, repo, _) = await Draft();
        using var __ = db;

        await Assert.ThrowsAsync<NotFoundException>(() => repo.TryTransitionAsync(999, RegistrationStatuses.Submitted, default));
    }

    [Fact]
    public async Task Editing_a_filed_registration_refiles_it_and_clears_the_stale_identity_check()
    {
        var (db, repo, id) = await Draft(new NewHouseholdMember("Old member", null, null, null, null));
        using var _ = db;
        var e = await db.Db.BusinessRegistrations.SingleAsync(r => r.registration_id == id);
        e.registration_status = RegistrationStatuses.MoreInformationRequired;
        e.identity_verified_by = Officer;
        e.identity_verified_at = Clock.UtcNow.AddDays(-1);
        e.review_decision_reason = "Bổ sung";
        await db.Db.SaveChangesAsync();
        db.Db.ChangeTracker.Clear();

        await repo.UpdateAndResubmitAsync(id, Data(name: "Quán C v2", members: new NewHouseholdMember("New member", null, null, null, null)), default);

        var row = (await new BusinessRegistrationRepository(db.NewContext(), Clock).GetByIdAsync(id, default))!;
        Assert.Equal(RegistrationStatuses.Submitted, row.RegistrationStatus);
        Assert.Equal("Quán C v2", row.DisplayName);
        Assert.Null(row.IdentityVerifiedAt);
        Assert.Null(row.ReviewDecisionReason);
        Assert.Equal("New member", Assert.Single(row.HouseholdMembersOrEmpty).FullName);
        var read = db.NewContext();
        Assert.True(await read.AuditLogs.AnyAsync(a => a.action == "ENROLLMENT_RESUBMITTED" && a.entity_id == id));
        Assert.True(await read.Notifications.AnyAsync(n => n.user_id == Officer && n.related_entity_id == id));
    }

    [Fact]
    public async Task Saving_a_draft_keeps_it_a_draft_without_telling_the_ward()
    {
        var (db, repo, id) = await Draft();
        using var _ = db;

        await repo.UpdateAndResubmitAsync(id, Data(name: "Nháp 2"), default);

        var read = db.NewContext();
        Assert.Equal(RegistrationStatuses.Draft, (await read.BusinessRegistrations.SingleAsync(r => r.registration_id == id)).registration_status);
        Assert.False(await read.Notifications.AnyAsync());
    }

    [Fact]
    public async Task A_decided_registration_can_no_longer_be_edited()
    {
        var (db, repo, id) = await Draft();
        using var _ = db;
        await repo.SetStatusAsync(id, RegistrationStatuses.Approved, default);

        await Assert.ThrowsAsync<DomainRuleException>(() => repo.UpdateAndResubmitAsync(id, Data(), default));
        await Assert.ThrowsAsync<NotFoundException>(() => repo.UpdateAndResubmitAsync(999, Data(), default));
    }

    [Fact]
    public async Task Filing_notifies_every_active_officer_of_the_ward()
    {
        var (db, repo, id) = await Draft();
        using var _ = db;

        await repo.RecordSubmittedAsync(id, default);
        await repo.RecordSubmittedAsync(999, default); // unknown: nothing

        var read = db.NewContext();
        var note = Assert.Single(await read.Notifications.ToListAsync());
        Assert.Equal(Officer, note.user_id);
        Assert.Contains("Quán C", note.body);
        Assert.Single(await read.AuditLogs.Where(a => a.action == "ENROLLMENT_SUBMITTED").ToListAsync());
    }

    [Fact]
    public async Task A_business_licence_on_a_fixed_storefront_turns_fast_track_on_and_its_removal_turns_it_off()
    {
        var (db, repo, id) = await Draft();
        using var _ = db;

        await repo.AddEvidenceAsync(id, new NewRegistrationEvidence(EvidenceTypes.IdentityDocument, "/api/uploads/evidence/12/a.jpg", null), default);
        Assert.False((await new BusinessRegistrationRepository(db.NewContext(), Clock).GetByIdAsync(id, default))!.FastTrackFlag);

        var licence = await repo.AddEvidenceAsync(id, new NewRegistrationEvidence(EvidenceTypes.BusinessLicense, "/api/uploads/evidence/12/b.jpg", null), default);
        Assert.True((await new BusinessRegistrationRepository(db.NewContext(), Clock).GetByIdAsync(id, default))!.FastTrackFlag);
        Assert.Equal(2, (await repo.ListEvidenceAsync(id, default)).Count);
        Assert.True(await repo.IsEvidenceFileUsedAsync("/api/uploads/evidence/12/b.jpg", default));

        var stored = await db.NewContext().RegistrationEvidences.SingleAsync(e => e.evidence_id == licence);
        Assert.Equal(Clock.UtcNow.Add(EvidenceFiles.RetentionPeriod), stored.retention_expires_at);

        Assert.True(await new BusinessRegistrationRepository(db.NewContext(), Clock).RemoveEvidenceAsync(id, licence, default));
        Assert.False(await repo.RemoveEvidenceAsync(id, licence, default));
        Assert.False((await new BusinessRegistrationRepository(db.NewContext(), Clock).GetByIdAsync(id, default))!.FastTrackFlag);
        Assert.False(await repo.IsEvidenceFileUsedAsync("/api/uploads/evidence/12/b.jpg", default));
    }

    [Fact]
    public async Task An_itinerant_vendor_never_gets_fast_track_even_with_a_licence()
    {
        using var db = await CreateAsync();
        var repo = new BusinessRegistrationRepository(db.Db, Clock);
        var id = await repo.CreateAsync(VendorC, Data(type: VendorTypes.Itinerant), default);

        await repo.AddEvidenceAsync(id, new NewRegistrationEvidence(EvidenceTypes.BusinessLicense, "/api/uploads/evidence/12/c.jpg", null), default);

        Assert.False((await new BusinessRegistrationRepository(db.NewContext(), Clock).GetByIdAsync(id, default))!.FastTrackFlag);
    }

    [Fact]
    public async Task Evidence_is_visible_only_to_its_own_ward_and_owner()
    {
        var (db, repo, id) = await Draft();
        using var _ = db;
        await repo.AddEvidenceAsync(id, new NewRegistrationEvidence(EvidenceTypes.IdentityDocument, "/api/uploads/evidence/12/d.jpg", null), default);

        Assert.True(await repo.EvidenceBelongsToWardAsync(VendorC, "/api/uploads/evidence/12/d.jpg", Ward, default));
        Assert.False(await repo.EvidenceBelongsToWardAsync(VendorC, "/api/uploads/evidence/12/d.jpg", 2, default));
        Assert.False(await repo.EvidenceBelongsToWardAsync(VendorA, "/api/uploads/evidence/12/d.jpg", Ward, default));
    }

    [Fact]
    public async Task Approval_and_active_contract_lookups()
    {
        using var db = await CreateAsync();
        var repo = new BusinessRegistrationRepository(db.Db, Clock);

        Assert.True(await repo.HasApprovedRegistrationAsync(VendorA, default));
        Assert.False(await repo.HasApprovedRegistrationAsync(VendorC, default));
        Assert.True(await repo.HasActiveContractAsync(RegistrationA, default));
        Assert.False(await repo.HasActiveContractAsync(RegistrationB, default));
    }

    [Fact]
    public async Task Biometric_consent_is_stamped_once()
    {
        var (db, repo, id) = await Draft();
        using var _ = db;

        await repo.RecordBiometricConsentAsync(id, default);
        Clock.UtcNow = Clock.UtcNow.AddHours(1);
        await repo.RecordBiometricConsentAsync(id, default);
        await repo.RecordBiometricConsentAsync(999, default);

        var stored = await db.NewContext().BusinessRegistrations.SingleAsync(r => r.registration_id == id);
        Assert.Equal(new DateTime(2026, 11, 1, 3, 0, 0, DateTimeKind.Utc), stored.biometric_consent_at);
        Clock.UtcNow = new DateTime(2026, 11, 1, 3, 0, 0, DateTimeKind.Utc);
    }
}
