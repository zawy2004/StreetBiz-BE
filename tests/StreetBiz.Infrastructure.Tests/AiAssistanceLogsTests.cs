using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Features.AiAssistance;
using StreetBiz.Application.Features.WardConfiguration;
using StreetBiz.Application.Features.WardSlots;
using StreetBiz.Infrastructure.Persistence;
using StreetBiz.Infrastructure.Persistence.ScaffoldedModels;
using StreetBiz.Infrastructure.Services;

namespace StreetBiz.Infrastructure.Tests;

/// <summary>
/// BR-41's logging core: RecordAsync/FindLatestAsync is the cache every AI feature keys off of,
/// and ReviewAsync is the only path that records an officer's accept/reject. These tests cover
/// the round trip, the input-key cache-miss rules, and ReviewAsync's ward scoping across the
/// AiLogEntities cases it switches on.
/// </summary>
public sealed class AiAssistanceLogsTests
{
    private sealed record Payload(string Text, int Score);

    #region Demo seed regression
    /// <summary>db/StreetBiz_SQL_Server.sql's AIC-04 row on slot 28 is hand-written JSON, not
    /// produced by RecordAsync -- this pins it against FindLatestAsync's actual deserialization
    /// path so a future edit to that row (or to AiProposalAssessment's shape) fails loudly here
    /// instead of silently in the ward officer's browser.</summary>
    [Fact]
    public async Task The_demo_seeds_hand_written_aic04_envelope_deserializes_into_a_proposal_assessment()
    {
        using var f = await Fixture.Create();
        using (var db = f.NewDb())
        {
            db.AIAssistanceLogs.Add(new AIAssistanceLog
            {
                feature_code = AiFeatureCodes.ProposalSite,
                entity_type = AiLogEntities.Slot,
                entity_id = 28,
                ai_output = """
                    {"inputKey":"SEED-LEGACY-KEY-DO-NOT-MATCH","output":{"estimatedSidewalkWidthMeters":2.4,"remainingPedestrianWidthMeters":0.4,"obstructionLevel":"HIGH","recommendation":"NEEDS_SURVEY","confidence":58,"reasons":["[Hệ thống] Ô cách trạm biến áp 12 m, trong ngưỡng cảnh báo.","[AI] Vỉa hè rộng khoảng 2,4 m, có xe máy đỗ một phần, lối đi còn lại hẹp."],"ruleChecks":[{"severity":"WARN","code":"NEAR_FEATURE","message":"Ô cách trạm biến áp 12 m, trong ngưỡng cảnh báo.","featureId":1,"slotId":null,"distanceMeters":12.0}],"usedSatelliteImage":true,"usedProposalPhoto":true,"isAiGenerated":true}}
                    """,
                confidence = 58.00m,
                created_at = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var found = await f.NewLogs().FindLatestAsync<AiProposalAssessment>(
            AiFeatureCodes.ProposalSite, AiLogEntities.Slot, 28, inputKey: null, default);

        Assert.NotNull(found);
        Assert.Equal("SEED-LEGACY-KEY-DO-NOT-MATCH", found!.InputKey);
        var output = found.Output;
        Assert.Equal(2.4m, output.EstimatedSidewalkWidthMeters);
        Assert.Equal(0.4m, output.RemainingPedestrianWidthMeters);
        Assert.Equal(ObstructionLevels.High, output.ObstructionLevel);
        Assert.Equal(ProposalRecommendations.NeedsSurvey, output.Recommendation);
        Assert.Equal(58, output.Confidence);
        Assert.Equal(2, output.Reasons.Count);
        Assert.Single(output.RuleChecks);
        Assert.Equal(PlacementSeverities.Warn, output.RuleChecks[0].Severity);
        Assert.True(output.IsAiGenerated);
    }
    #endregion

    #region RecordAsync / FindLatestAsync
    [Fact]
    public async Task Recorded_output_round_trips_through_find_latest_with_the_same_key()
    {
        using var f = await Fixture.Create();
        var logs = f.NewLogs();

        var logId = await logs.RecordAsync(AiFeatureCodes.ZonePrice, AiLogEntities.Zone, f.ZoneId, "key-1", new Payload("xin chào", 42), confidence: 80, default);

        var found = await logs.FindLatestAsync<Payload>(AiFeatureCodes.ZonePrice, AiLogEntities.Zone, f.ZoneId, "key-1", default);

        Assert.NotNull(found);
        Assert.Equal(logId, found!.AiLogId);
        Assert.Equal("key-1", found.InputKey);
        Assert.Equal("xin chào", found.Output.Text);
        Assert.Equal(42, found.Output.Score);
        Assert.Null(found.Accepted);
    }

    [Fact]
    public async Task Find_latest_with_a_different_key_is_a_cache_miss()
    {
        using var f = await Fixture.Create();
        var logs = f.NewLogs();
        await logs.RecordAsync(AiFeatureCodes.ZonePrice, AiLogEntities.Zone, f.ZoneId, "key-1", new Payload("a", 1), null, default);

        var found = await logs.FindLatestAsync<Payload>(AiFeatureCodes.ZonePrice, AiLogEntities.Zone, f.ZoneId, "key-2", default);

        Assert.Null(found);
    }

    [Fact]
    public async Task Find_latest_with_a_null_key_returns_the_latest_log_regardless_of_its_key()
    {
        using var f = await Fixture.Create();
        var logs = f.NewLogs();
        await logs.RecordAsync(AiFeatureCodes.ZonePrice, AiLogEntities.Zone, f.ZoneId, "key-1", new Payload("old", 1), null, default);
        var secondId = await logs.RecordAsync(AiFeatureCodes.ZonePrice, AiLogEntities.Zone, f.ZoneId, "key-2", new Payload("new", 2), null, default);

        var found = await logs.FindLatestAsync<Payload>(AiFeatureCodes.ZonePrice, AiLogEntities.Zone, f.ZoneId, inputKey: null, default);

        Assert.NotNull(found);
        Assert.Equal(secondId, found!.AiLogId);
        Assert.Equal("new", found.Output.Text);
    }

    [Fact]
    public async Task Find_latest_for_a_different_entity_or_feature_does_not_match()
    {
        using var f = await Fixture.Create();
        var logs = f.NewLogs();
        await logs.RecordAsync(AiFeatureCodes.ZonePrice, AiLogEntities.Zone, f.ZoneId, "key-1", new Payload("a", 1), null, default);

        Assert.Null(await logs.FindLatestAsync<Payload>(AiFeatureCodes.ZonePrice, AiLogEntities.Zone, f.ZoneId + 1, "key-1", default));
        Assert.Null(await logs.FindLatestAsync<Payload>(AiFeatureCodes.GeofenceDrift, AiLogEntities.Zone, f.ZoneId, "key-1", default));
    }
    #endregion

    #region ReviewAsync
    [Fact]
    public async Task Review_from_another_ward_is_not_found_and_leaves_the_log_unreviewed()
    {
        using var f = await Fixture.Create();
        var logs = f.NewLogs();
        var logId = await logs.RecordAsync(AiFeatureCodes.ZonePrice, AiLogEntities.Zone, f.ZoneId, null, new Payload("a", 1), null, default);

        await Assert.ThrowsAsync<NotFoundException>(() =>
            logs.ReviewAsync(f.OtherWardActor, logId, new AiSuggestionFeedbackRequest(true, null), default));

        using var db = f.NewDb();
        var log = await db.AIAssistanceLogs.SingleAsync(x => x.ai_log_id == logId);
        Assert.Null(log.reviewed_at);
        Assert.Null(log.accepted);
    }

    [Fact]
    public async Task Review_an_unrecognized_entity_type_is_not_found()
    {
        using var f = await Fixture.Create();
        var logs = f.NewLogs();
        var logId = await logs.RecordAsync("AIC-99", "SomethingElse", 1, null, new Payload("a", 1), null, default);

        await Assert.ThrowsAsync<NotFoundException>(() =>
            logs.ReviewAsync(f.Actor, logId, new AiSuggestionFeedbackRequest(true, null), default));
    }

    [Fact]
    public async Task Review_a_nonexistent_log_id_is_not_found()
    {
        using var f = await Fixture.Create();
        var logs = f.NewLogs();

        await Assert.ThrowsAsync<NotFoundException>(() =>
            logs.ReviewAsync(f.Actor, 999_999, new AiSuggestionFeedbackRequest(true, null), default));
    }

    [Theory]
    [InlineData(AiLogEntities.Zone)]
    [InlineData(AiLogEntities.Slot)]
    [InlineData(AiLogEntities.Registration)]
    [InlineData(AiLogEntities.Permit)]
    [InlineData(AiLogEntities.ScanLog)]
    [InlineData(AiLogEntities.Violation)]
    [InlineData(AiLogEntities.Ward)]
    public async Task Review_from_the_owning_ward_succeeds_and_writes_reviewed_at_plus_an_audit_log(string entityType)
    {
        using var f = await Fixture.Create();
        var logs = f.NewLogs();
        var entityId = f.OwnWardEntityId(entityType);
        var logId = await logs.RecordAsync("AIC-01", entityType, entityId, null, new Payload("a", 1), null, default);

        var result = await logs.ReviewAsync(f.Actor, logId, new AiSuggestionFeedbackRequest(true, "Đạt yêu cầu"), default);

        Assert.True(result.Accepted);
        Assert.Equal(f.Actor.Name, result.ReviewerName);

        using var db = f.NewDb();
        var log = await db.AIAssistanceLogs.SingleAsync(x => x.ai_log_id == logId);
        Assert.True(log.accepted);
        Assert.Equal(f.Actor.UserId, log.reviewed_by);
        Assert.NotNull(log.reviewed_at);

        var audit = await db.AuditLogs.SingleAsync(a => a.action == "AI_SUGGESTION_REVIEWED");
        Assert.Equal(f.Actor.UserId, audit.actor_user_id);
        Assert.Equal("AIAssistanceLog", audit.entity_type);
        Assert.Equal(logId, audit.entity_id);
        Assert.Contains(entityType, audit.details);
    }

    [Theory]
    [InlineData(AiLogEntities.Zone)]
    [InlineData(AiLogEntities.Slot)]
    [InlineData(AiLogEntities.Registration)]
    [InlineData(AiLogEntities.Permit)]
    [InlineData(AiLogEntities.ScanLog)]
    [InlineData(AiLogEntities.Violation)]
    public async Task Review_of_a_record_belonging_to_another_ward_is_not_found(string entityType)
    {
        using var f = await Fixture.Create();
        var logs = f.NewLogs();
        var entityId = f.OtherWardEntityId(entityType);
        var logId = await logs.RecordAsync("AIC-01", entityType, entityId, null, new Payload("a", 1), null, default);

        await Assert.ThrowsAsync<NotFoundException>(() =>
            logs.ReviewAsync(f.Actor, logId, new AiSuggestionFeedbackRequest(false, null), default));
    }

    [Fact]
    public async Task Reviewing_a_log_a_second_time_overwrites_the_first_decision()
    {
        using var f = await Fixture.Create();
        var logs = f.NewLogs();
        var logId = await logs.RecordAsync(AiFeatureCodes.ZonePrice, AiLogEntities.Zone, f.ZoneId, null, new Payload("a", 1), null, default);

        var first = await logs.ReviewAsync(f.Actor, logId, new AiSuggestionFeedbackRequest(false, "Chưa đạt"), default);
        var second = await logs.ReviewAsync(f.Actor, logId, new AiSuggestionFeedbackRequest(true, "Đã xem lại, đạt"), default);

        Assert.False(first.Accepted);
        Assert.True(second.Accepted);
        Assert.True(second.ReviewedAt >= first.ReviewedAt);

        using var db = f.NewDb();
        var log = await db.AIAssistanceLogs.SingleAsync(x => x.ai_log_id == logId);
        Assert.True(log.accepted);
        Assert.Equal(2, await db.AuditLogs.CountAsync(a => a.action == "AI_SUGGESTION_REVIEWED"));
    }
    #endregion

    private sealed class Fixture : IDisposable
    {
        public SqliteConnection Connection { get; } = new("Data Source=:memory:");
        public WardActor Actor { get; } = new(1, 1, "Cán bộ phường");
        public WardActor OtherWardActor { get; } = new(2, 2, "Cán bộ phường khác");

        public int ZoneId { get; private set; }
        private int OtherWardZoneId { get; set; }
        private long SlotId { get; set; }
        private long OtherWardSlotId { get; set; }
        private long RegistrationId { get; set; }
        private long OtherWardRegistrationId { get; set; }
        private long PermitId { get; set; }
        private long OtherWardPermitId { get; set; }
        private long ScanId { get; set; }
        private long OtherWardScanId { get; set; }
        private long ViolationId { get; set; }
        private long OtherWardViolationId { get; set; }

        public long OwnWardEntityId(string entityType) => entityType switch
        {
            AiLogEntities.Zone => ZoneId,
            AiLogEntities.Slot => SlotId,
            AiLogEntities.Registration => RegistrationId,
            AiLogEntities.Permit => PermitId,
            AiLogEntities.ScanLog => ScanId,
            AiLogEntities.Violation => ViolationId,
            AiLogEntities.Ward => Actor.WardId,
            _ => throw new ArgumentOutOfRangeException(nameof(entityType)),
        };

        public long OtherWardEntityId(string entityType) => entityType switch
        {
            AiLogEntities.Zone => OtherWardZoneId,
            AiLogEntities.Slot => OtherWardSlotId,
            AiLogEntities.Registration => OtherWardRegistrationId,
            AiLogEntities.Permit => OtherWardPermitId,
            AiLogEntities.ScanLog => OtherWardScanId,
            AiLogEntities.Violation => OtherWardViolationId,
            _ => throw new ArgumentOutOfRangeException(nameof(entityType)),
        };

        public static async Task<Fixture> Create()
        {
            var f = new Fixture();
            await f.Connection.OpenAsync();
            using var db = f.NewDb();
            await db.Database.EnsureCreatedAsync();

            db.Roles.AddRange(
                new Role { role_code = "WARD_AUTHORITY", role_name = "Ward" },
                new Role { role_code = "VENDOR", role_name = "Vendor" });
            db.AdministrativeUnits.AddRange(
                new AdministrativeUnit { unit_id = 1, unit_type = "WARD", unit_name = "Phường 1" },
                new AdministrativeUnit { unit_id = 2, unit_type = "WARD", unit_name = "Phường 2" });
            db.UserAccounts.AddRange(
                new UserAccount { user_id = 1, phone_number = "0900000001", password_hash = "x", full_name = "Cán bộ", role_code = "WARD_AUTHORITY", ward_unit_id = 1, account_status = "ACTIVE" },
                new UserAccount { user_id = 2, phone_number = "0900000002", password_hash = "x", full_name = "Cán bộ 2", role_code = "WARD_AUTHORITY", ward_unit_id = 2, account_status = "ACTIVE" },
                new UserAccount { user_id = 10, phone_number = "0900000010", password_hash = "x", full_name = "Chủ hộ A", role_code = "VENDOR", account_status = "ACTIVE" });
            db.Vendors.Add(new Vendor { vendor_id = 10, user_id = 10 });

            f.RegistrationId = 1;
            f.OtherWardRegistrationId = 2;
            db.BusinessRegistrations.AddRange(
                new BusinessRegistration { registration_id = f.RegistrationId, vendor_id = 10, ward_unit_id = 1, vendor_type = "ITINERANT", display_name = "Hộ A", registration_status = "APPROVED" },
                new BusinessRegistration { registration_id = f.OtherWardRegistrationId, vendor_id = 10, ward_unit_id = 2, vendor_type = "ITINERANT", display_name = "Hộ B", registration_status = "APPROVED" });

            f.ZoneId = 1;
            f.OtherWardZoneId = 2;
            db.PricingZones.AddRange(
                new PricingZone { zone_id = f.ZoneId, ward_unit_id = 1, zone_code = "T1", zone_name = "Tuyến 1", price_per_day = 30000, created_by = 1 },
                new PricingZone { zone_id = f.OtherWardZoneId, ward_unit_id = 2, zone_code = "T2", zone_name = "Tuyến phường khác", price_per_day = 20000, created_by = 2 });

            f.SlotId = 1;
            f.OtherWardSlotId = 2;
            db.SidewalkSlots.AddRange(
                new SidewalkSlot { slot_id = f.SlotId, zone_id = f.ZoneId, slot_code = "A-01", source = "WARD_DEFINED", slot_status = "ACTIVE", latitude = 10m, longitude = 106m },
                new SidewalkSlot { slot_id = f.OtherWardSlotId, zone_id = f.OtherWardZoneId, slot_code = "B-01", source = "WARD_DEFINED", slot_status = "ACTIVE", latitude = 20m, longitude = 108m });

            db.RentalApplications.AddRange(
                new RentalApplication { application_id = 1, registration_id = f.RegistrationId, slot_id = f.SlotId, application_method = "MANUAL_SELECTED", application_status = "APPROVED", requested_term_days = 180 },
                new RentalApplication { application_id = 2, registration_id = f.OtherWardRegistrationId, slot_id = f.OtherWardSlotId, application_method = "MANUAL_SELECTED", application_status = "APPROVED", requested_term_days = 180 });
            db.RentalContracts.AddRange(
                new RentalContract { contract_id = 1, application_id = 1, slot_id = f.SlotId, vendor_id = 10, start_date = new DateOnly(2026, 1, 1), end_date = new DateOnly(2027, 1, 1), contract_status = "ACTIVE" },
                new RentalContract { contract_id = 2, application_id = 2, slot_id = f.OtherWardSlotId, vendor_id = 10, start_date = new DateOnly(2026, 1, 1), end_date = new DateOnly(2027, 1, 1), contract_status = "ACTIVE" });

            f.PermitId = 1;
            f.OtherWardPermitId = 2;
            db.DigitalPermits.AddRange(
                new DigitalPermit { permit_id = f.PermitId, contract_id = 1, qr_payload = "QR-1", permit_status = "ACTIVE", issued_at = DateTime.UtcNow },
                new DigitalPermit { permit_id = f.OtherWardPermitId, contract_id = 2, qr_payload = "QR-2", permit_status = "ACTIVE", issued_at = DateTime.UtcNow });

            f.ScanId = 1;
            f.OtherWardScanId = 2;
            db.PermitScanLogs.AddRange(
                new PermitScanLog { scan_id = f.ScanId, permit_id = f.PermitId, qr_payload = "QR-1", scan_context = "WARD_INSPECTION", scan_result = "VALID", scanned_at = DateTime.UtcNow },
                new PermitScanLog { scan_id = f.OtherWardScanId, permit_id = f.OtherWardPermitId, qr_payload = "QR-2", scan_context = "WARD_INSPECTION", scan_result = "VALID", scanned_at = DateTime.UtcNow });

            db.ViolationTypes.Add(new ViolationType { violation_type_code = "UNAUTHORIZED_BUSINESS_USE", description = "Sử dụng trái phép vỉa hè", is_active = true });
            f.ViolationId = 1;
            f.OtherWardViolationId = 2;
            db.Violations.AddRange(
                new Violation { violation_id = f.ViolationId, slot_id = f.SlotId, vendor_id = 10, violation_type = "UNAUTHORIZED_BUSINESS_USE", recorded_by = 1, source = "ON_SITE", recorded_at = DateTime.UtcNow },
                new Violation { violation_id = f.OtherWardViolationId, slot_id = f.OtherWardSlotId, vendor_id = 10, violation_type = "UNAUTHORIZED_BUSINESS_USE", recorded_by = 2, source = "ON_SITE", recorded_at = DateTime.UtcNow });

            await db.SaveChangesAsync();
            return f;
        }

        public TestContext NewDb() =>
            new(new DbContextOptionsBuilder<StreetBizDbContext>().UseSqlite(Connection).Options);

        public AiAssistanceLogs NewLogs() => new(NewDb(), TimeProvider.System);

        public void Dispose() => Connection.Dispose();
    }

    private sealed class TestContext(DbContextOptions<StreetBizDbContext> options) : StreetBizDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            foreach (var entity in modelBuilder.Model.GetEntityTypes())
                foreach (var property in entity.GetProperties())
                {
                    if (property.GetComputedColumnSql() is not null)
                    {
                        property.SetComputedColumnSql(null);
                        property.ValueGenerated = ValueGenerated.Never;
                    }
                    property.SetDefaultValueSql(null);
                }
        }
    }
}
