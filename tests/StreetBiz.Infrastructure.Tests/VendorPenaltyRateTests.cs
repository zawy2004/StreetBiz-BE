using Microsoft.Extensions.Options;
using StreetBiz.Infrastructure.Payments;
using StreetBiz.Infrastructure.Persistence.Repositories;
using StreetBiz.Infrastructure.Persistence.ScaffoldedModels;
using static StreetBiz.Infrastructure.Tests.Support.SqliteTestDatabase;

namespace StreetBiz.Infrastructure.Tests;

/// <summary>WARD-03 seen by the vendor: the rates in force in the wards the vendor is registered in.</summary>
public sealed class VendorPenaltyRateTests
{
    private const int OtherWard = 2;

    [Fact]
    public async Task A_vendor_sees_the_rates_in_force_in_its_own_ward_only()
    {
        using var db = await CreateAsync();
        db.Db.AdministrativeUnits.Add(new AdministrativeUnit { unit_id = OtherWard, unit_type = "WARD", unit_name = "Phường Hai" });
        db.Db.ViolationTypes.AddRange(
            new ViolationType { violation_type_code = "OUTSIDE_SLOT", description = "Bán ngoài ô", is_active = true },
            new ViolationType { violation_type_code = "NO_PERMIT", description = "Không có giấy phép", is_active = true },
            new ViolationType { violation_type_code = "EXPIRED", description = "Hết hiệu lực", is_active = true },
            new ViolationType { violation_type_code = "FUTURE", description = "Chưa hiệu lực", is_active = true },
            new ViolationType { violation_type_code = "RETIRED", description = "Đã bỏ", is_active = false });
        db.Db.PenaltyFeeSchedules.AddRange(
            Rate(1, Ward, "OUTSIDE_SLOT", 500_000, new DateOnly(2026, 1, 1), null),
            // Ended on 1 October, starts next year, or the type itself is retired: not in force on 15 October.
            Rate(2, Ward, "EXPIRED", 1_000_000, new DateOnly(2026, 1, 1), new DateOnly(2026, 10, 1)),
            Rate(3, Ward, "NO_PERMIT", 1_500_000, new DateOnly(2026, 10, 1), null),
            Rate(4, Ward, "RETIRED", 100_000, new DateOnly(2026, 1, 1), null),
            Rate(5, Ward, "FUTURE", 900_000, new DateOnly(2027, 1, 1), null),
            Rate(6, OtherWard, "OUTSIDE_SLOT", 700_000, new DateOnly(2026, 1, 1), null));
        await db.Db.SaveChangesAsync();
        var repository = new FinanceRepository(db.NewContext(), TimeProvider.System, Options.Create(new PaymentGatewaySettings()));

        var rates = await repository.ListWardPenaltyRatesAsync(VendorA, new DateOnly(2026, 10, 15), default);

        Assert.Equal(
            [(Ward, "Phường Một", "OUTSIDE_SLOT", 500_000m), (Ward, "Phường Một", "NO_PERMIT", 1_500_000m)],
            rates.Select(r => (r.WardUnitId, r.WardName, r.ViolationType, r.PenaltyAmount)).ToList());
        Assert.Empty(await repository.ListWardPenaltyRatesAsync(VendorC, new DateOnly(2026, 10, 15), default));
    }

    private static PenaltyFeeSchedule Rate(int id, int ward, string type, decimal amount, DateOnly from, DateOnly? to) => new()
    {
        penalty_schedule_id = id,
        ward_unit_id = ward,
        violation_type = type,
        penalty_amount = amount,
        legal_basis = "Nghị định 123/2021/NĐ-CP",
        effective_from = from,
        effective_to = to,
        created_by = Officer,
    };
}
