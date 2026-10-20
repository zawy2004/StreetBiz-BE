using Microsoft.EntityFrameworkCore;
using StreetBiz.Application.Features.Chatbot;
using StreetBiz.Infrastructure.Persistence;

namespace StreetBiz.Infrastructure.Services.Chatbot;

/// <summary>Bounded projections for legacy endpoints that return unpaged lists. Never materialize identity evidence.</summary>
public sealed class ChatbotListReader(StreetBizDbContext db, IChatbotActorResolver actors) : IChatbotListReader
{
    public async Task<IReadOnlyList<ChatbotListItem>> ReadAsync(ChatbotActor actor, string tool, CancellationToken ct)
    {
        if (!await actors.IsActiveAsync(actor, ct)) throw new ChatbotException(401, "session_expired", "Phiên đã hết hiệu lực.");
        if (tool == "admin.categories" && actor.Role == "PLATFORM_ADMIN")
            return await db.FoodCategories.AsNoTracking().OrderBy(x => x.category_id).Take(20)
                .Select(x => new ChatbotListItem(x.category_id, x.category_name, null, null, null)).ToArrayAsync(ct);
        if (actor.Role != "VENDOR" || actor.VendorId is not { } vendorId)
            throw new ChatbotException(403, "tool_denied", "Không có quyền tra cứu.");
        return tool switch
        {
            "vendor.registrations" => await db.BusinessRegistrations.AsNoTracking().Where(x => x.vendor_id == vendorId)
                .OrderByDescending(x => x.registration_id).Take(20).Select(x => new ChatbotListItem(x.registration_id, x.vendor_type, x.registration_status, null, null)).ToArrayAsync(ct),
            "vendor.rentals" => await db.RentalApplications.AsNoTracking().Where(x => x.registration.vendor_id == vendorId)
                .OrderByDescending(x => x.application_id).Take(20).Select(x => new ChatbotListItem(x.application_id, x.slot.slot_code, x.application_status, null, null)).ToArrayAsync(ct),
            "vendor.contracts" => await db.RentalContracts.AsNoTracking().Where(x => x.vendor_id == vendorId)
                .OrderByDescending(x => x.contract_id).Take(20).Select(x => new ChatbotListItem(x.contract_id, x.slot.slot_code, x.contract_status, null, x.end_date)).ToArrayAsync(ct),
            "vendor.fees" => await db.FeeScheduleItems.AsNoTracking().Where(x => x.fee_schedule.contract.vendor_id == vendorId && x.fee_schedule.superseded_at == null)
                .OrderByDescending(x => x.due_date).ThenByDescending(x => x.fee_item_id).Take(20)
                .Select(x => new ChatbotListItem(x.fee_item_id, x.fee_schedule.contract.slot.slot_code, x.item_status, x.amount, x.due_date)).ToArrayAsync(ct),
            "vendor.penalties" => await db.Penalties.AsNoTracking().Where(x => x.violation.vendor_id == vendorId)
                .OrderByDescending(x => x.penalty_id).Take(20).Select(x => new ChatbotListItem(x.penalty_id, x.violation.violation_typeNavigation.description, x.penalty_status, x.amount, null)).ToArrayAsync(ct),
            "vendor.invoices" => await db.Invoices.AsNoTracking().Where(x => x.vendor_id == vendorId)
                .OrderByDescending(x => x.invoice_id).Take(20).Select(x => new ChatbotListItem(x.invoice_id, x.invoice_number, null, x.amount, null)).ToArrayAsync(ct),
            "vendor.payments" => await db.PaymentTransactions.AsNoTracking().Where(x =>
                    (x.fee_item != null && x.fee_item.fee_schedule.contract.vendor_id == vendorId) ||
                    (x.penalty != null && x.penalty.violation.vendor_id == vendorId))
                .OrderByDescending(x => x.transaction_id).Take(20).Select(x => new ChatbotListItem(x.transaction_id, x.payment_purpose, x.transaction_status, x.amount, null)).ToArrayAsync(ct),
            "vendor.violations" => await db.Violations.AsNoTracking().Where(x => x.vendor_id == vendorId)
                .OrderByDescending(x => x.violation_id).Take(20).Select(x => new ChatbotListItem(x.violation_id, x.violation_typeNavigation.description, x.Penalty == null ? null : x.Penalty.penalty_status, null, null)).ToArrayAsync(ct),
            _ => throw new ChatbotException(403, "tool_denied", "Không có quyền tra cứu."),
        };
    }

    public async Task<ChatbotSlotPermit?> SlotPermitAsync(ChatbotActor actor, string slotCode, CancellationToken ct)
    {
        if (!await actors.IsActiveAsync(actor, ct)) throw new ChatbotException(401, "session_expired", "Phiên đã hết hiệu lực.");
        if (actor.Role != "WARD_AUTHORITY" || actor.WardId is not { } wardId)
            throw new ChatbotException(403, "tool_denied", "Không có quyền tra cứu.");
        var slot = await db.SidewalkSlots.AsNoTracking()
            .Where(s => s.slot_code == slotCode && s.zone.ward_unit_id == wardId)
            .Select(s => new { s.slot_id, s.slot_code, s.zone.zone_name })
            .FirstOrDefaultAsync(ct);
        if (slot is null) return null;
        // Read the validity view at query time; a slot can have older permits, so prefer the newest contract.
        var permit = await db.vw_PermitValidities.AsNoTracking()
            .Where(v => v.slot_id == slot.slot_id)
            .OrderByDescending(v => v.end_date).ThenByDescending(v => v.contract_id)
            .Select(v => new { v.contract_id, v.vendor_id, v.contract_status, v.effective_status, v.end_date })
            .FirstOrDefaultAsync(ct);
        if (permit is null) return new(slot.slot_code, slot.zone_name, null, null, null, "NO_PERMIT", null);
        var vendorName = await db.BusinessRegistrations.AsNoTracking()
            .Where(r => r.vendor_id == permit.vendor_id && r.registration_status == "APPROVED")
            .OrderByDescending(r => r.registration_id).Select(r => r.display_name).FirstOrDefaultAsync(ct);
        return new(slot.slot_code, slot.zone_name, vendorName ?? $"Hộ kinh doanh #{permit.vendor_id}", permit.contract_id,
            permit.contract_status, permit.effective_status, permit.end_date);
    }
}
