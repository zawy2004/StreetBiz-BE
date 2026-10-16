using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;
using StreetBiz.Infrastructure.Persistence;
using StreetBiz.Infrastructure.Persistence.Repositories;
using StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

namespace StreetBiz.Infrastructure.Tests;

/// <summary>
/// Submitting an application leaves SidewalkSlots.slot_status as AVAILABLE, so the repository has to
/// report such a slot as PENDING_APPLICATION or the map keeps showing it free.
/// </summary>
public sealed class SidewalkSlotRepositoryPendingTests : IAsyncLifetime
{
    private const long Free = 1;
    private const long WithOpenApplication = 2;
    private const long WithRejectedApplication = 3;
    private const long Rented = 4;

    private SqliteConnection connection = null!;
    private TestContext db = null!;

    public async Task InitializeAsync()
    {
        connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        // Only the rows the projection reads are seeded, so skip parents the query never touches.
        await using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys = OFF;";
            await pragma.ExecuteNonQueryAsync();
        }

        db = new TestContext(new DbContextOptionsBuilder<StreetBizDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();

        db.PricingZones.Add(new PricingZone
        {
            zone_id = 1, ward_unit_id = 10, zone_name = "Đường Nguyễn Văn Linh", price_per_day = 30000,
            created_by = 1, created_at = DateTime.UtcNow, price_display_unit = "DAY", rental_mode = "STANDARD",
        });
        db.SidewalkSlots.AddRange(
            Slot(Free, "NVL-01", SlotStatuses.Available),
            Slot(WithOpenApplication, "NVL-02", SlotStatuses.Available),
            Slot(WithRejectedApplication, "NVL-03", SlotStatuses.Available),
            Slot(Rented, "NVL-04", SlotStatuses.Active));
        db.RentalApplications.AddRange(
            Application(1, WithOpenApplication, ApplicationStatuses.UnderReview),
            Application(2, WithRejectedApplication, ApplicationStatuses.Rejected),
            Application(3, Rented, ApplicationStatuses.Pending));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    public async Task DisposeAsync()
    {
        await db.DisposeAsync();
        await connection.DisposeAsync();
    }

    private SidewalkSlotRepository Repository() => new(db, new StreetBiz.Infrastructure.Common.DateTimeProvider());

    [Fact]
    public async Task A_free_slot_with_an_open_application_is_reported_pending()
    {
        var slots = await Repository().SearchAsync(Everywhere(includeUnavailable: true), CancellationToken.None);

        Assert.Equal(SlotStatuses.Available, StatusOf(slots, Free));
        Assert.Equal(SlotStatuses.PendingApplication, StatusOf(slots, WithOpenApplication));
    }

    [Fact]
    public async Task A_closed_application_does_not_hold_the_slot()
    {
        var slot = await Repository().GetByIdAsync(WithRejectedApplication, CancellationToken.None);

        Assert.Equal(SlotStatuses.Available, slot!.SlotStatus);
    }

    [Fact]
    public async Task Other_statuses_are_left_as_they_are()
    {
        var slot = await Repository().GetByIdAsync(Rented, CancellationToken.None);

        Assert.Equal(SlotStatuses.Active, slot!.SlotStatus);
    }

    [Fact]
    public async Task Get_by_id_reports_pending_too()
    {
        var slot = await Repository().GetByIdAsync(WithOpenApplication, CancellationToken.None);

        Assert.Equal(SlotStatuses.PendingApplication, slot!.SlotStatus);
    }

    [Fact]
    public async Task Searching_only_open_slots_leaves_out_the_one_under_review()
    {
        var slots = await Repository().SearchAsync(Everywhere(includeUnavailable: false), CancellationToken.None);

        Assert.Equal(new[] { Free, WithRejectedApplication }, slots.Select(s => s.SlotId).Order());
    }

    private static string StatusOf(IReadOnlyList<SlotRow> slots, long slotId) =>
        slots.Single(s => s.SlotId == slotId).SlotStatus;

    private static SlotSearchArea Everywhere(bool includeUnavailable) =>
        new(null, null, null, null, null, null, includeUnavailable);

    private static SidewalkSlot Slot(long id, string code, string status) => new()
    {
        slot_id = id, slot_code = code, zone_id = 1, latitude = 16.05m, longitude = 108.22m,
        width_meters = 2, length_meters = 3, slot_status = status, source = SlotSources.WardDefined,
        created_at = DateTime.UtcNow,
    };

    private static RentalApplication Application(long id, long slotId, string status) => new()
    {
        application_id = id, registration_id = 1, slot_id = slotId, application_method = "MANUAL_SELECTED",
        requested_term_days = 90, application_status = status, created_at = DateTime.UtcNow,
    };

    private sealed class TestContext(DbContextOptions<StreetBizDbContext> options)
        : StreetBizDbContext(options)
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
