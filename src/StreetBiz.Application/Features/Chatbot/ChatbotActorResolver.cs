using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.Features.WardSlots;

namespace StreetBiz.Application.Features.Chatbot;

public sealed class ChatbotActorResolver(ICurrentUser caller, IUserAccountRepository users,
    ISessionRepository sessions, IVendorContext vendor, ICustomerContext customer,
    IWardActorContext ward, IPlatformAdminContext admin, TimeProvider clock, IVendorRepository vendorRepository) : IChatbotActorResolver
{
    public async Task<ChatbotActor> RequireAsync(CancellationToken ct)
    {
        if (!caller.IsAuthenticated || caller.UserId is not { } userId || caller.SessionId is not { } sessionId)
            throw new ChatbotException(401, "session_expired", "Vui lòng đăng nhập lại để sử dụng trợ lý.");
        var user = await users.GetByIdAsync(userId, ct);
        if (user is null || user.AccountStatus != "ACTIVE" || user.RoleCode != caller.RoleCode)
            throw new ChatbotException(403, "access_denied", "Tài khoản không còn quyền sử dụng phiên này.");
        long? vendorId = null;
        switch (user.RoleCode)
        {
            case "VENDOR": vendorId = await vendor.RequireVendorIdAsync(ct); break;
            case "CUSTOMER": await customer.RequireCustomerUserIdAsync(ct); break;
            case "WARD_AUTHORITY": await ward.RequireAsync(ct); break;
            case "PLATFORM_ADMIN": await admin.RequireAsync(ct); break;
            default: throw new ChatbotException(403, "access_denied", "Vai trò chưa được hỗ trợ.");
        }
        var actor = new ChatbotActor(userId, sessionId, user.RoleCode, user.WardUnitId, vendorId);
        if (!await IsActiveAsync(actor, ct))
            throw new ChatbotException(401, "session_expired", "Phiên đăng nhập đã hết hiệu lực.");
        return actor;
    }

    public async Task<bool> IsActiveAsync(ChatbotActor actor, CancellationToken ct)
    {
        if (actor.UserId == 0) return false;
        var user = await users.GetByIdAsync(actor.UserId, ct);
        var session = await sessions.GetByIdAsync(actor.SessionId, ct);
        if (actor.Role == "VENDOR" && (actor.VendorId is null || await vendorRepository.GetVendorIdByUserAsync(actor.UserId, ct) != actor.VendorId)) return false;
        return user is { AccountStatus: "ACTIVE" } && user.RoleCode == actor.Role && user.WardUnitId == actor.WardId
            && session is not null && session.UserId == actor.UserId && session.RevokedAt is null
            && session.ExpiresAt > clock.GetUtcNow().UtcDateTime;
    }
}
