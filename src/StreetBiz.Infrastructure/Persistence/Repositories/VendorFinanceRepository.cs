using Microsoft.EntityFrameworkCore;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;

namespace StreetBiz.Infrastructure.Persistence.Repositories;

/// <summary>
/// The vendor's fees contract by contract. Separate small queries rather than one projection,
/// for the reason WardReportRepository gives; positions within a schedule are numbered in memory,
/// as FinanceRepository.ListFeeItemsAsync does.
/// </summary>
public sealed class VendorFinanceRepository(StreetBizDbContext db) : IVendorFinanceRepository
{
    public async Task<IReadOnlyList<VendorContractRow>> ListContractsAsync(
        long vendorId, CancellationToken cancellationToken)
    {
        var contracts = await db.FeeSchedules.AsNoTracking()
            .Where(schedule => schedule.superseded_at == null && schedule.contract.vendor_id == vendorId)
            .Select(schedule => new
            {
                schedule.contract_id,
                schedule.total_amount,
                SlotCode = schedule.contract.slot.slot_code,
                ZoneName = schedule.contract.slot.zone.zone_name,
                WardUnitId = schedule.contract.slot.zone.ward_unit_id,
                Address = schedule.contract.application.registration.declared_address,
                schedule.contract.start_date,
                schedule.contract.end_date,
                schedule.contract.contract_status,
            })
            .ToListAsync(cancellationToken);
        if (contracts.Count == 0)
        {
            return [];
        }

        var wardIds = contracts.Select(row => row.WardUnitId).Distinct().ToArray();
        var wardNames = await db.AdministrativeUnits.AsNoTracking()
            .Where(unit => wardIds.Contains(unit.unit_id))
            .ToDictionaryAsync(unit => unit.unit_id, unit => unit.unit_name, cancellationToken);

        return contracts
            .Select(row => new VendorContractRow(
                row.contract_id,
                row.SlotCode,
                row.ZoneName,
                wardNames.GetValueOrDefault(row.WardUnitId),
                row.Address,
                row.start_date,
                row.end_date,
                row.contract_status,
                row.total_amount))
            .ToList();
    }

    public async Task<IReadOnlyList<ScheduleItemRow>> ListScheduleItemsAsync(
        long vendorId, long? contractId, CancellationToken cancellationToken)
    {
        var items = await db.FeeScheduleItems.AsNoTracking()
            .Where(item => item.fee_schedule.superseded_at == null
                && item.fee_schedule.contract.vendor_id == vendorId
                && (contractId == null || item.fee_schedule.contract_id == contractId))
            .Select(item => new
            {
                item.fee_item_id,
                item.fee_schedule_id,
                item.fee_schedule.contract_id,
                item.due_date,
                item.amount,
                item.item_status,
                item.paid_at,
            })
            .ToListAsync(cancellationToken);
        if (items.Count == 0)
        {
            return [];
        }

        var ids = items.Select(item => item.fee_item_id).ToArray();
        var invoices = await db.Invoices.AsNoTracking()
            .Where(invoice => invoice.fee_item_id != null && ids.Contains(invoice.fee_item_id.Value))
            .Select(invoice => new { FeeItemId = invoice.fee_item_id!.Value, invoice.invoice_id, invoice.invoice_number })
            .ToListAsync(cancellationToken);
        var invoiceByItem = invoices
            .GroupBy(invoice => invoice.FeeItemId)
            .ToDictionary(group => group.Key, group => group.First());

        return items
            .GroupBy(item => item.fee_schedule_id)
            .SelectMany(schedule =>
            {
                var ordered = schedule.OrderBy(item => item.due_date).ThenBy(item => item.fee_item_id).ToList();
                return ordered.Select((item, index) =>
                {
                    var invoice = invoiceByItem.GetValueOrDefault(item.fee_item_id);
                    return new ScheduleItemRow(
                        item.fee_item_id,
                        item.contract_id,
                        index + 1,
                        ordered.Count,
                        item.due_date,
                        item.amount,
                        item.item_status,
                        item.paid_at,
                        invoice?.invoice_id,
                        invoice?.invoice_number);
                });
            })
            .OrderBy(item => item.DueDate)
            .ThenBy(item => item.FeeItemId)
            .ToList();
    }

    public async Task<(string VendorName, string? BusinessName)> GetVendorNamesAsync(
        long vendorId, CancellationToken cancellationToken)
    {
        var vendorName = await db.Vendors.AsNoTracking()
            .Where(vendor => vendor.vendor_id == vendorId)
            .Select(vendor => vendor.user.full_name ?? vendor.user.phone_number)
            .SingleOrDefaultAsync(cancellationToken);
        var businessName = await db.BusinessRegistrations.AsNoTracking()
            .Where(registration => registration.vendor_id == vendorId)
            .OrderByDescending(registration => registration.registration_id)
            .Select(registration => registration.display_name)
            .FirstOrDefaultAsync(cancellationToken);
        return (vendorName ?? $"Hộ kinh doanh #{vendorId}", businessName);
    }
}
