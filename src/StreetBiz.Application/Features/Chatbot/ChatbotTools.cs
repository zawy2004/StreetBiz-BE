using System.Globalization;
using System.Text.Json;
using MediatR;
using StreetBiz.Application.Features.CommunityVendors;
using StreetBiz.Application.Features.Commerce;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.Features.DigitalPermits.GetPermit;
using StreetBiz.Application.Features.Finance.GetSummary;
using StreetBiz.Application.Features.Finance.ListFeeItems;
using StreetBiz.Application.Features.Finance.ListPenalties;
using StreetBiz.Application.Features.Finance.ListInvoices;
using StreetBiz.Application.Features.Finance.GetInvoice;
using StreetBiz.Application.Features.Finance.ListPayments;
using StreetBiz.Application.Features.Finance.ListViolations;
using StreetBiz.Application.Features.Finance.WardReports;
using StreetBiz.Application.Features.PlatformAdministration;
using StreetBiz.Application.Features.RentalApplications.TrackApplications;
using StreetBiz.Application.Features.RentalApplications.GetApplication;
using StreetBiz.Application.Features.RentalContracts.ListContracts;
using StreetBiz.Application.Features.RentalContracts.GetContract;
using StreetBiz.Application.Features.VendorRegistration.TrackRegistrations;
using StreetBiz.Application.Features.VendorRegistration.GetRegistration;
using StreetBiz.Application.Features.WardCompliance;
using StreetBiz.Application.Features.Notifications;
using StreetBiz.Application.Features.SidewalkSlots.GetSlot;
using StreetBiz.Application.DTOs.Commerce;

namespace StreetBiz.Application.Features.Chatbot;

public sealed record ChatbotToolSpec(string Name, string Role, string Description, bool Id = false, bool Page = false);

/// <summary>Explicit, read-only dispatch. No reflection, commands, arbitrary SQL or provider-controlled identity.</summary>
public sealed class ChatbotTools(ISender mediator, IChatbotActorResolver actors, ChatbotKnowledge knowledge, TimeProvider clock, IChatbotListReader lists)
{
    public static readonly ChatbotToolSpec[] Catalog =
    [
        new("help.search", "*", "Tìm hướng dẫn sản phẩm StreetBiz theo câu hỏi, không phải căn cứ pháp luật."),
        new("navigation.list", "*", "Các màn hình được phép mở cho vai trò hiện tại."),
        new("account.notifications", "ACCOUNT", "Thông báo mới nhất của chính người gọi; chỉ đọc, không đánh dấu đã đọc."),
        new("vendor.slot", "VENDOR", "Chi tiết ô vỉa hè đã chọn, giá tham khảo không phải khoản phải trả.", true),
        new("public.vendors", "PUBLIC", "Tối đa 20 người bán công khai trên bản đồ; không là tổng toàn hệ thống."),
        new("public.vendor", "PUBLIC", "Hồ sơ công khai của người bán đã chọn; không phải xác minh QR.", true),
        new("public.food", "CUSTOMER", "Tìm món và gian hàng công khai theo tên nhận diện từ ảnh hoặc câu hỏi. Chỉ là thông tin niêm yết; không xác nhận còn món. Truyền query là tên món ngắn; không đưa cả câu hỏi."),
        new("vendor.registrations", "VENDOR", "Danh sách hồ sơ kinh doanh của chính người gọi."),
        new("vendor.registration", "VENDOR", "Trạng thái và lý do xử lý hồ sơ của chính người gọi.", true),
        new("vendor.rentals", "VENDOR", "Các đơn thuê ô của chính người gọi."),
        new("vendor.rental", "VENDOR", "Đơn thuê ô được chọn thuộc người gọi.", true),
        new("vendor.contracts", "VENDOR", "Hợp đồng của tôi, bao gồm thời hạn để xem việc gia hạn."),
        new("vendor.contract", "VENDOR", "Hợp đồng thuộc người gọi, id là contractId.", true),
        new("vendor.permit", "VENDOR", "Kiểm tra trực tiếp hiệu lực giấy phép cho hợp đồng của tôi. id là contractId, không phải permitId.", true),
        new("vendor.finance", "VENDOR", "Tổng phí và tiền phạt phải trả của chính người gọi, do backend tính."),
        new("vendor.fees", "VENDOR", "Các kỳ phí của tôi, hạn và trạng thái."),
        new("vendor.penalties", "VENDOR", "Khoản phạt đã được cán bộ ghi nhận đối với tôi."),
        new("vendor.invoices", "VENDOR", "Danh sách hóa đơn đã phát hành của tôi."),
        new("vendor.invoice", "VENDOR", "Chi tiết hóa đơn đã phát hành thuộc người gọi.", true),
        new("vendor.payments", "VENDOR", "Trạng thái thanh toán đã được hệ thống ghi nhận của tôi."),
        new("vendor.violations", "VENDOR", "Các vi phạm đã ghi nhận đối với tôi; không tự kết luận vi phạm mới."),
        new("ward.dashboard", "WARD_AUTHORITY", "Số liệu tổng quan thật trong phường được phân công."),
        new("ward.collection", "WARD_AUTHORITY", "Báo cáo thu và công nợ trong kỳ mặc định của phường, nêu rõ khoảng ngày."),
        new("ward.registrations", "WARD_AUTHORITY", "Trang danh sách hồ sơ kinh doanh trong phường.", Page: true),
        new("ward.registration", "WARD_AUTHORITY", "Tóm tắt hồ sơ kinh doanh trong phường để cán bộ đối chiếu, không tự duyệt.", true),
        new("ward.rentals", "WARD_AUTHORITY", "Trang danh sách đơn thuê trong phường.", Page: true),
        new("ward.rental", "WARD_AUTHORITY", "Đơn thuê trong phường và các điều kiện còn thiếu.", true),
        new("ward.renewals", "WARD_AUTHORITY", "Trang danh sách đề nghị gia hạn trong phường.", Page: true),
        new("ward.violations", "WARD_AUTHORITY", "Trang vi phạm đã ghi nhận trong phường.", Page: true),
        new("ward.slot_permit", "WARD_AUTHORITY", "Kiểm tra trực tiếp hiệu lực giấy phép của một ô vỉa hè trong phường theo mã ô (ví dụ HC-08). Chỉ đọc; không ghi vi phạm."),
        new("admin.categories", "PLATFORM_ADMIN", "Danh mục hiện có của nền tảng."),
        new("admin.reports", "PLATFORM_ADMIN", "Trang nội dung bị báo cáo, chỉ thuộc phạm vi kiểm duyệt nền tảng.", Page: true),
        new("admin.report", "PLATFORM_ADMIN", "Nội dung bị báo cáo đã chọn để quản trị viên xem xét.", true),
    ];

    public static bool Allowed(ChatbotToolSpec spec, string role) => spec.Role == "*" || spec.Role == role
        || (spec.Role == "ACCOUNT" && role is "CUSTOMER" or "VENDOR" or "WARD_AUTHORITY" or "PLATFORM_ADMIN")
        || (spec.Role == "PUBLIC" && role is "GUEST" or "CUSTOMER");

    public static readonly string[] Confidences = ["high", "medium", "low"];

    public IReadOnlyList<ChatbotToolDefinition> Definitions(string role, bool photo = false) => Catalog.Where(s => Allowed(s, role))
        .Select(s => new ChatbotToolDefinition(s.Name, photo && s.Name == "public.food" ? PhotoFoodDescription : s.Description, Schema(s, photo))).ToArray();

    private const string PhotoFoodDescription = "Tìm quầy công khai bán món nhận diện được trong ảnh. query là tên món có khả năng nhất (ngắn, tiếng Việt). "
        + "alternatives là tối đa 2 tên món khác cũng có thể đúng; bỏ trống nếu chắc chắn. confidence: high/medium/low theo độ rõ của ảnh. "
        + "cues là dấu hiệu nhìn thấy được (tối đa 1 câu ngắn), không đoán thành phần không thấy.";

    private static JsonElement Schema(ChatbotToolSpec spec, bool photo = false)
    {
        var properties = new Dictionary<string, object>();
        if (spec.Id) properties["id"] = new { type = "string", pattern = "^[1-9][0-9]{0,17}$", description = "ID đối tượng đã được người dùng chọn hoặc tool trả về" };
        if (spec.Page) properties["page"] = new { type = "integer", minimum = 1, maximum = 100 };
        if (spec.Name == "help.search") properties["question"] = new { type = "string", maxLength = 500 };
        if (spec.Name == "public.food") properties["query"] = new { type = "string", minLength = 2, maxLength = 100 };
        if (spec.Name == "public.food" && photo)
        {
            properties["alternatives"] = new { type = "array", maxItems = 2, items = new { type = "string", minLength = 2, maxLength = 60 } };
            properties["confidence"] = new { type = "string", @enum = Confidences };
            properties["cues"] = new { type = "string", maxLength = 200 };
        }
        if (spec.Name == "ward.slot_permit") properties["slotCode"] = new { type = "string", pattern = SlotCodePattern, description = "Mã ô đúng như người dùng nói/ghi, ví dụ HC-08" };
        if (spec.Name == "ward.collection")
        {
            properties["from"] = new { type = "string", format = "date", description = "Ngày bắt đầu yyyy-MM-dd; chỉ dùng khoảng ngày người dùng yêu cầu." };
            properties["to"] = new { type = "string", format = "date", description = "Ngày kết thúc yyyy-MM-dd; tối đa 366 ngày." };
        }
        var required = spec.Id ? new[] { "id" } : spec.Name switch
        {
            "public.food" => ["query"],
            "ward.slot_permit" => ["slotCode"],
            _ => Array.Empty<string>(),
        };
        return JsonSerializer.SerializeToElement(new { type = "object", properties, required, additionalProperties = false });
    }

    public const string SlotCodePattern = "^[A-Za-z0-9][A-Za-z0-9-]{0,19}$";

    public async Task<ChatbotEvidence> ExecuteAsync(ChatbotActor actor, string name, string arguments, string question, CancellationToken ct,
        ChatbotLocation? location = null)
    {
        var spec = Catalog.FirstOrDefault(t => t.Name == name && Allowed(t, actor.Role))
            ?? throw new ChatbotException(403, "tool_denied", "Chức năng tra cứu không thuộc quyền của bạn.");
        if (actor.UserId != 0 && !await actors.IsActiveAsync(actor, ct))
            throw new ChatbotException(401, "session_expired", "Phiên đăng nhập không còn hiệu lực.");
        if (arguments.Length > 2000) throw InvalidArguments();
        JsonElement args;
        try { args = JsonSerializer.Deserialize<JsonElement>(arguments); }
        catch (JsonException) { throw InvalidArguments(); }
        if (args.ValueKind != JsonValueKind.Object) throw InvalidArguments();
        var allowed = new HashSet<string>();
        if (spec.Id) allowed.Add("id");
        if (spec.Page) allowed.Add("page");
        if (name == "help.search") allowed.Add("question");
        if (name == "public.food") { allowed.Add("query"); allowed.Add("alternatives"); allowed.Add("confidence"); allowed.Add("cues"); }
        if (name == "ward.slot_permit") allowed.Add("slotCode");
        if (name == "ward.collection") { allowed.Add("from"); allowed.Add("to"); }
        if (args.EnumerateObject().Any(p => !allowed.Contains(p.Name)) || args.EnumerateObject().Select(p => p.Name).Distinct().Count() != args.EnumerateObject().Count()) throw InvalidArguments();
        long id = 0;
        if (spec.Id && (!args.TryGetProperty("id", out var idElement) || idElement.ValueKind != JsonValueKind.String
            || !long.TryParse(idElement.GetString(), out id) || id <= 0)) throw InvalidArguments();
        var page = 1;
        if (args.TryGetProperty("page", out var p) && (p.ValueKind != JsonValueKind.Number || !p.TryGetInt32(out page) || page is < 1 or > 100)) throw InvalidArguments();
        if (args.TryGetProperty("question", out var q) && (q.ValueKind != JsonValueKind.String || q.GetString()!.Length > 500)) throw InvalidArguments();
        string? foodQuery = null;
        if (name == "public.food")
        {
            if (!args.TryGetProperty("query", out var term) || term.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(term.GetString()) || term.GetString()!.Trim().Length is < 2 or > 100) throw InvalidArguments();
            foodQuery = ChatbotPrivacy.Text(term.GetString()!.Trim(), 100);
            // Photo hints are interpreted by ChatbotService; here they only have to be well-formed.
            if (args.TryGetProperty("alternatives", out var alternatives) && (alternatives.ValueKind != JsonValueKind.Array
                || alternatives.GetArrayLength() > 2 || alternatives.EnumerateArray().Any(a => a.ValueKind != JsonValueKind.String || a.GetString()!.Trim().Length is < 2 or > 60)))
                throw InvalidArguments();
            if (args.TryGetProperty("confidence", out var confidence) && (confidence.ValueKind != JsonValueKind.String || !Confidences.Contains(confidence.GetString())))
                throw InvalidArguments();
            if (args.TryGetProperty("cues", out var cues) && (cues.ValueKind != JsonValueKind.String || cues.GetString()!.Length > 200))
                throw InvalidArguments();
        }
        string? slotCode = null;
        if (name == "ward.slot_permit")
        {
            if (!args.TryGetProperty("slotCode", out var code) || code.ValueKind != JsonValueKind.String
                || !System.Text.RegularExpressions.Regex.IsMatch(code.GetString()!.Trim(), SlotCodePattern)) throw InvalidArguments();
            slotCode = code.GetString()!.Trim().ToUpperInvariant();
        }
        DateOnly? from = null, to = null;
        if (args.TryGetProperty("from", out var fromJson) || args.TryGetProperty("to", out _))
        {
            if (fromJson.ValueKind != JsonValueKind.String || !args.TryGetProperty("to", out var toJson) || toJson.ValueKind != JsonValueKind.String
                || !DateOnly.TryParseExact(fromJson.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start)
                || !DateOnly.TryParseExact(toJson.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var end)
                || end < start || end.DayNumber - start.DayNumber > 366) throw InvalidArguments();
            from = start; to = end;
        }
        var invocation = Guid.NewGuid().ToString("N");
        var cards = new List<ChatbotCard>();
        var actions = new List<ChatbotAction>();
        var publicImages = new List<ChatbotPublicImage>();
        var title = spec.Description;
        var authoritative = name is "vendor.permit" or "vendor.finance" or "vendor.fees" or "vendor.penalties" or "vendor.invoice" or "vendor.invoices" or "vendor.payments" or "ward.collection" or "ward.dashboard" or "ward.slot_permit";
        void Add(string heading, string route, params ChatbotField[] fields) => AddPlace(heading, route, null, fields);
        void AddPlace(string heading, string route, ChatbotPlace? place, params ChatbotField[] fields)
        {
            if (cards.Count >= 20) return;
            var actionId = $"{name}:{invocation}:{cards.Count}";
            cards.Add(new("status", ChatbotPrivacy.Text(heading, 100), fields.Where(f => f.Value != Omit).ToArray(), actionId, Place: place));
            actions.Add(ChatbotNavigation.A(actionId, "Mở chi tiết", route));
        }

        var listRoute = name switch
        {
            "vendor.registrations" => ("Hồ sơ", "/vendor/registrations/"),
            "vendor.rentals" => ("Đơn thuê", "/vendor/slots/rental-applications/"),
            "vendor.contracts" => ("Hợp đồng", "/vendor/slots/contracts/"),
            "vendor.fees" => ("Khoản phí", "/vendor/finance"),
            "vendor.penalties" => ("Khoản phạt", "/vendor/finance"),
            "vendor.invoices" => ("Hóa đơn", "/vendor/finance/invoices/"),
            "vendor.payments" => ("Giao dịch", "/vendor/finance/payments"),
            "vendor.violations" => ("Biên bản", "/vendor/finance/violations"),
            "admin.categories" => ("Danh mục", "/platform/categories"),
            _ => ((string, string)?)null,
        };
        if (listRoute is { } route)
        {
            foreach (var x in await lists.ReadAsync(actor, name, ct))
            {
                var fields = new List<ChatbotField> { F("Mã", x.Id), F("Thông tin", x.Label) };
                if (x.Status != null) fields.Add(F("Trạng thái ghi nhận", x.Status));
                if (x.Amount is { } amount) fields.Add(Money("Số tiền", amount));
                if (x.Date is { } date) fields.Add(F(name == "vendor.contracts" ? "Ngày kết thúc" : "Hạn", date));
                Add($"{route.Item1} #{x.Id}", route.Item2 + (route.Item2.EndsWith('/') ? x.Id : ""), fields.ToArray());
            }
        }
        else switch (name)
        {
            case "account.notifications":
                var notifications = await mediator.Send(new ListNotificationsQuery(null, 20), ct);
                foreach (var x in notifications.Items)
                    Add("Thông báo", "/account/notifications", F("Nội dung", x.Title), F("Chi tiết", x.Body));
                break;
            case "vendor.slot":
                var slot = await mediator.Send(new GetSlotQuery(id), ct);
                Add($"Ô {slot.SlotCode}", $"/vendor/slots/{id}", F("Khu vực", slot.ZoneName), F("Trạng thái", slot.SlotStatus),
                    Money("Giá tham khảo/ngày", slot.PricePerDay), F("Lưu ý", "Mở ô để xem điều kiện và báo giá theo thời hạn. Chưa giữ ô hoặc tạo khoản thu."));
                break;
            case "help.search": return knowledge.Evidence(actor.Role, question);
            case "navigation.list":
                actions.AddRange(ChatbotNavigation.ForRole(actor.Role)); break;
            case "public.vendors":
                foreach (var x in await mediator.Send(new SearchActiveVendorsQuery(null, null, null, 20), ct))
                    Add(x.DisplayName, $"/customer/explore/vendors/{x.VendorId}", F("Mã người bán", x.VendorId), F("Loại", x.VendorType), F("Ô", x.SlotCode), F("Khu vực", x.ZoneName));
                break;
            case "public.food":
                var near = ChatbotPrivacy.Coarse(location);
                var dishes = await mediator.Send(new SearchMarketplaceMenuQuery(foodQuery, Take: 16), ct);
                var stores = await mediator.Send(new ListStorefrontsQuery(Query: foodQuery, Latitude: near?.Latitude, Longitude: near?.Longitude, Take: 8), ct);
                string? alias = null;
                // Curated regional names only (dish-synonyms.json), and only when the given name finds nothing.
                foreach (var other in dishes.Count == 0 && stores.Count == 0 ? ChatbotFoodPhoto.OtherNames(foodQuery!).Take(2) : Array.Empty<string>())
                {
                    dishes = await mediator.Send(new SearchMarketplaceMenuQuery(other, Take: 16), ct);
                    stores = await mediator.Send(new ListStorefrontsQuery(Query: other, Latitude: near?.Latitude, Longitude: near?.Longitude, Take: 8), ct);
                    if (dishes.Count > 0 || stores.Count > 0) { alias = other; foodQuery = other; break; }
                }
                var directory = stores.ToDictionary(s => s.StorefrontId);
                if (dishes.Any(d => !directory.ContainsKey(d.StorefrontId)))
                    foreach (var shop in await mediator.Send(new ListStorefrontsQuery(Latitude: near?.Latitude, Longitude: near?.Longitude, Take: 100), ct))
                        directory.TryAdd(shop.StorefrontId, shop);
                var wanted = ChatbotKnowledge.Normalize(foodQuery!);
                // Exact dish-name matches first, then stalls open now, then nearest when a position was shared.
                dishes = dishes
                    .OrderBy(d => ChatbotKnowledge.Normalize(d.ItemName).Contains(wanted) ? 0 : 1)
                    .ThenBy(d => directory.GetValueOrDefault(d.StorefrontId)?.IsOpenNow == true ? 0 : 1)
                    .ThenBy(d => directory.GetValueOrDefault(d.StorefrontId)?.DistanceMeters ?? double.MaxValue)
                    .Take(8).ToArray();
                stores = stores.OrderBy(s => s.IsOpenNow ? 0 : 1).ThenBy(s => s.DistanceMeters ?? double.MaxValue).ToArray();
                foreach (var dish in dishes)
                {
                    var listedStore = directory.GetValueOrDefault(dish.StorefrontId);
                    AddPlace(dish.StorefrontName, $"/customer/explore/stores/{dish.StorefrontId}", Place(listedStore, dish.UnitPrice),
                        F("Món niêm yết", dish.ItemName), F("Danh mục", dish.CategoryName),
                        Money("Giá niêm yết", dish.UnitPrice), F("Địa chỉ", listedStore?.Address), F("Khu vực", listedStore?.WardName),
                        Opt("Giờ mở cửa", listedStore is null ? null : listedStore.IsOpenNow ? "Đang mở" : "Ngoài giờ mở cửa"),
                        Opt("Khoảng cách", Distance(listedStore?.DistanceMeters)),
                        F("Cơ sở gợi ý", "Từ khóa khớp tên quầy, tên món hoặc danh mục công khai: " + foodQuery),
                        F("Lưu ý", "Thông tin niêm yết tại lúc tra cứu; chưa xác nhận quầy còn món hôm nay."));
                    var previewImage = listedStore?.ImageUrl;
                    if (!MenuImageFiles.TryParseUrl(previewImage, out var previewOwner, out _) || previewOwner <= 0)
                        previewImage = dish.ImageUrl;
                    if (previewImage is { } dishImage)
                    {
                        publicImages.Add(new(dishImage, $"Ảnh công khai tại quầy {ChatbotPrivacy.Text(dish.StorefrontName, 100)}; ID {dish.StorefrontId}; món tìm: {ChatbotPrivacy.Text(dish.ItemName, 100)}"));
                        if (MenuImageFiles.TryParseUrl(dishImage, out var ownerId, out _) && ownerId > 0)
                            cards[^1] = cards[^1] with { ImageUrl = dishImage };
                    }
                }
                foreach (var shop in stores.Where(s => dishes.All(d => d.StorefrontId != s.StorefrontId)))
                {
                    AddPlace(shop.StorefrontName, $"/customer/explore/stores/{shop.StorefrontId}", Place(shop, shop.MinPrice),
                        F("Mô tả", shop.Description), F("Địa chỉ", shop.Address), F("Khu vực", shop.WardName),
                        Opt("Giờ mở cửa", shop.IsOpenNow ? "Đang mở" : "Ngoài giờ mở cửa"),
                        Opt("Khoảng cách", Distance(shop.DistanceMeters)),
                        F("Cơ sở gợi ý", "Từ khóa khớp thông tin gian hàng/món công khai: " + foodQuery),
                        F("Lưu ý", "Kết quả khớp thông tin quầy; chưa xác nhận quầy đang bán đúng món bạn tìm."));
                    if (shop.ImageUrl is { } shopImage)
                    {
                        publicImages.Add(new(shopImage, $"Ảnh quầy {ChatbotPrivacy.Text(shop.StorefrontName, 100)}; ID {shop.StorefrontId}"));
                        if (MenuImageFiles.TryParseUrl(shopImage, out var ownerId, out _) && ownerId > 0)
                            cards[^1] = cards[^1] with { ImageUrl = shopImage };
                    }
                }
                title = "Tìm món/quầy công khai: " + foodQuery + (alias is null ? "" : " (tên gọi khác của món bạn hỏi)")
                    + ". Kết quả có giới hạn; không có kết quả không có nghĩa toàn hệ thống không bán.";
                break;
            case "ward.slot_permit":
                var slotPermit = await lists.SlotPermitAsync(actor, slotCode!, ct);
                if (slotPermit is not null)
                    Add($"Ô {slotPermit.SlotCode}", "/ward/patrol",
                        F("Hiệu lực lúc tra cứu", slotPermit.EffectiveStatus), F("Người bán", slotPermit.VendorName),
                        F("Khu vực", slotPermit.ZoneName), F("Trạng thái hợp đồng", slotPermit.ContractStatus), F("Đến ngày", slotPermit.EndDate),
                        F("Lưu ý", "Kết quả tra cứu trực tiếp. Mở Tuần tra để quét QR tại chỗ; trợ lý không ghi vi phạm."));
                title = $"Hiệu lực giấy phép ô {slotCode} trong phường của bạn, tra cứu trực tiếp."
                    + (slotPermit is null ? " Không tìm thấy ô này trong phường được phân công." : "");
                break;
            case "public.vendor":
                var publicVendor = await mediator.Send(new GetPublicVendorProfileQuery(id), ct);
                Add(publicVendor.DisplayName, $"/customer/explore/vendors/{id}", F("Loại", publicVendor.VendorType), F("Ô", publicVendor.SlotCode), F("Phường", publicVendor.WardName), F("Lưu ý", "Mở Quét giấy phép để xác minh hiệu lực trực tiếp.")); break;
            case "vendor.registration":
                var registration = (await mediator.Send(new GetRegistrationQuery(id), ct)).Registration;
                Add($"Hồ sơ #{id}", $"/vendor/registrations/{id}", F("Loại", registration.VendorType), F("Trạng thái", registration.RegistrationStatus), F("Lý do đã ghi nhận", registration.ReviewDecisionReason)); break;
            case "vendor.rental":
                var rental = await mediator.Send(new GetApplicationQuery(id), ct);
                Add($"Đơn thuê #{id}", $"/vendor/slots/rental-applications/{id}", F("Trạng thái", rental.ApplicationStatus), F("Lý do", rental.ReviewDecisionReason)); break;
            case "vendor.contract":
                var contract = await mediator.Send(new GetContractQuery(id), ct);
                Add($"Hợp đồng #{id}", $"/vendor/slots/contracts/{id}", F("Ô", contract.SlotCode), F("Trạng thái", contract.ContractStatus), F("Bắt đầu", contract.StartDate), F("Kết thúc", contract.EndDate)); break;
            case "vendor.permit":
                var permit = await mediator.Send(new GetPermitQuery(id), ct);
                Add($"Giấy phép của hợp đồng #{id}", $"/vendor/slots/contracts/{id}/permit", F("Hiệu lực lúc tra cứu", permit.EffectiveStatus), F("Trạng thái hợp đồng", permit.ContractStatus), F("Đến ngày", permit.EndDate)); break;
            case "vendor.finance":
                var summary = await mediator.Send(new GetFinanceSummaryQuery(), ct);
                Add("Tài chính của bạn", "/vendor/finance", Money("Phí phải trả", summary.FeeDue), Money("Phạt phải trả", summary.PenaltyDue), Money("Tổng phải trả", summary.TotalDue), F("Số khoản quá hạn", summary.OverdueCount), F("Hạn gần nhất", summary.NextDueDate)); break;
            case "vendor.invoice":
                var invoice = await mediator.Send(new GetInvoiceQuery(id), ct);
                Add($"Hóa đơn #{id}", $"/vendor/finance/invoices/{id}", F("Số hóa đơn", invoice.InvoiceNumber), Money("Số tiền", invoice.Amount), F("Loại", invoice.Kind)); break;
            case "ward.dashboard":
                var dashboard = await mediator.Send(new GetWardDashboardQuery(), ct);
                Add("Tổng quan phường", "/ward/dashboard", F("Tổng ô", dashboard.SlotTotal), F("Ô đang thuê", dashboard.SlotRented), F("Hồ sơ chờ", dashboard.PendingRegistrations), F("Đơn thuê chờ", dashboard.PendingApplications), Money("Đã thu", dashboard.RevenueCollected), Money("Công nợ", dashboard.OutstandingDebt)); break;
            case "ward.collection":
                var report = await mediator.Send(new GetCollectionReportQuery(from, to), ct);
                Add("Báo cáo thu của phường", "/ward/reports", F("Từ ngày", report.From), F("Đến ngày", report.To), Money("Phí đã thu", report.FeeCollected), Money("Phí chờ thu", report.FeePending), Money("Phí quá hạn", report.FeeOverdue), Money("Phạt đã thu", report.PenaltyCollected)); break;
            case "ward.registrations":
                foreach (var x in (await mediator.Send(new ListWardEnrollmentsQuery(null, page), ct)).Take(20))
                    Add($"Hồ sơ #{x.Id}", $"/ward/inbox/registrations/{x.Id}", F("Mã hồ sơ", x.Id), F("Loại", x.VendorType), F("Trạng thái", x.Status)); break;
            case "ward.registration":
                var enrollment = await mediator.Send(new GetWardEnrollmentDetailQuery(id), ct);
                Add($"Hồ sơ #{id}", $"/ward/inbox/registrations/{id}", F("Loại", enrollment.VendorType), F("Trạng thái", enrollment.Status), F("Ý kiến đã ghi nhận", enrollment.ReviewReason), F("Xác minh thủ công", enrollment.IdentityVerified ? "Đã ghi nhận" : "Cần cán bộ đối chiếu")); break;
            case "ward.rentals":
                foreach (var x in (await mediator.Send(new ListWardRentalApplicationsQuery(null, page), ct)).Take(20))
                    Add($"Đơn thuê #{x.Id}", $"/ward/inbox/rental-applications/{x.Id}", F("Mã đơn", x.Id), F("Ô", x.SlotCode), F("Trạng thái", x.Status)); break;
            case "ward.rental":
                var wardRental = await mediator.Send(new GetWardRentalApplicationDetailQuery(id), ct);
                Add($"Đơn thuê #{id}", $"/ward/inbox/rental-applications/{id}", F("Trạng thái", wardRental.Status), F("Đăng ký", wardRental.RegistrationStatus), F("Điều cần kiểm tra", string.Join("; ", wardRental.Blockers))); break;
            case "ward.renewals":
                foreach (var x in (await mediator.Send(new ListWardRenewalsQuery(null, page), ct)).Take(20))
                    Add($"Gia hạn #{x.Id}", $"/ward/inbox/renewals/{x.Id}", F("Mã yêu cầu", x.Id), F("Trạng thái", x.Status)); break;
            case "ward.violations":
                foreach (var x in (await mediator.Send(new ListWardViolationsQuery(null, page), ct)).Take(20))
                    Add($"Vi phạm #{x.ViolationId}", "/ward/patrol", F("Mã biên bản", x.ViolationId), F("Trạng thái", x.Status)); break;
            case "admin.reports":
                var reported = await mediator.Send(new ListReportedContentQuery(null, page, 20), ct);
                foreach (var x in reported.Items)
                    Add($"Báo cáo #{x.ReportId}", $"/platform/moderation/content/{x.ReportId}", F("Mã báo cáo", x.ReportId), F("Loại", x.ContentType), F("Trạng thái", x.Status)); break;
            case "admin.report":
                var content = await mediator.Send(new GetReportedContentQuery(id), ct);
                Add($"Báo cáo #{id}", $"/platform/moderation/content/{id}", F("Loại", content.ContentType), F("Trạng thái", content.Status), F("Lý do", content.Reason)); break;
        }

        var source = new ChatbotSource($"live:{name}:{invocation}", title, name == "navigation.list" ? "PRODUCT_GUIDE" : "LIVE_DATA",
            clock.GetUtcNow().ToOffset(TimeSpan.FromHours(7)), null, actions.FirstOrDefault()?.Id);
        var json = ChatbotJson.Serialize(new { source.Id, observedAt = source.ObservedAt, page, limit = 20,
            note = "Danh sách có giới hạn, không suy ra tổng toàn hệ thống. Trạng thái chỉ đúng lúc tra cứu.", cards });
        while (System.Text.Encoding.UTF8.GetByteCount(json) > 12000 && cards.Count > 1)
        {
            cards.RemoveAt(cards.Count - 1);
            actions.RemoveAt(actions.Count - 1);
            json = ChatbotJson.Serialize(new { source.Id, observedAt = source.ObservedAt, page, truncated = true, cards });
        }
        return new(name, json, source, cards, actions, authoritative,
            publicImages.Where(image => cards.Any(card => card.ImageUrl == image.Url)).ToArray());
    }

    /// <summary>Read-only "today" digest built from the same scoped queries as the tools. Never creates reminders or records.</summary>
    public async Task<ChatbotBriefing> BriefingAsync(ChatbotActor actor, CancellationToken ct)
    {
        if (actor.UserId != 0 && !await actors.IsActiveAsync(actor, ct))
            throw new ChatbotException(401, "session_expired", "Phiên đăng nhập không còn hiệu lực.");
        var now = clock.GetUtcNow().ToOffset(TimeSpan.FromHours(7));
        var today = DateOnly.FromDateTime(now.DateTime);
        var items = new List<ChatbotBriefingItem>();
        string Vnd(decimal amount) => amount.ToString("N0", CultureInfo.GetCultureInfo("vi-VN")) + " ₫";
        if (actor.Role == "VENDOR")
        {
            var finance = await mediator.Send(new GetFinanceSummaryQuery(), ct);
            if (finance.TotalDue > 0)
                items.Add(new(finance.OverdueCount > 0 ? "warning" : "info",
                    finance.OverdueCount > 0 ? $"{finance.OverdueCount} khoản đã quá hạn" : "Có khoản cần thanh toán",
                    $"Tổng phải trả {Vnd(finance.TotalDue)}" + (finance.NextDueDate is { } due ? $", hạn gần nhất {due:dd/MM/yyyy}." : "."),
                    ChatbotNavigation.A("briefing:finance", "Mở Tài chính", "/vendor/finance")));
            foreach (var contract in (await lists.ReadAsync(actor, "vendor.contracts", ct))
                .Where(c => c.Status == "ACTIVE" && c.Date is { } end && end >= today && end.DayNumber - today.DayNumber <= 30).Take(2))
                items.Add(new("warning", $"Hợp đồng ô {contract.Label} sắp hết hạn",
                    $"Kết thúc ngày {contract.Date:dd/MM/yyyy} (còn {contract.Date!.Value.DayNumber - today.DayNumber} ngày). Xem chức năng gia hạn trên hợp đồng.",
                    ChatbotNavigation.A($"briefing:contract:{contract.Id}", "Mở hợp đồng", $"/vendor/slots/contracts/{contract.Id}")));
            foreach (var registration in (await lists.ReadAsync(actor, "vendor.registrations", ct))
                .Where(r => r.Status is "NEEDS_REVISION" or "MORE_INFORMATION_REQUIRED").Take(2))
                items.Add(new("warning", $"Hồ sơ #{registration.Id} cần bổ sung",
                    "Cán bộ phường đã yêu cầu bổ sung thông tin. Mở hồ sơ để xem nội dung cần sửa.",
                    ChatbotNavigation.A($"briefing:registration:{registration.Id}", "Mở hồ sơ", $"/vendor/registrations/{registration.Id}")));
        }
        else if (actor.Role == "WARD_AUTHORITY")
        {
            var dashboard = await mediator.Send(new GetWardDashboardQuery(), ct);
            if (dashboard.PendingRegistrations > 0)
                items.Add(new("info", $"{dashboard.PendingRegistrations} hồ sơ kinh doanh đang chờ", "Hồ sơ trong phường được phân công đang chờ cán bộ xem xét.",
                    ChatbotNavigation.A("briefing:registrations", "Mở hộp duyệt", "/ward/inbox")));
            if (dashboard.PendingApplications > 0)
                items.Add(new("info", $"{dashboard.PendingApplications} đơn thuê ô đang chờ", "Đơn thuê cần đối chiếu điều kiện trước khi quyết định.",
                    ChatbotNavigation.A("briefing:rentals", "Mở hộp duyệt", "/ward/inbox")));
            if (dashboard.OutstandingDebt > 0)
                items.Add(new("info", "Công nợ trong phường", $"Tổng công nợ ghi nhận {Vnd(dashboard.OutstandingDebt)}.",
                    ChatbotNavigation.A("briefing:reports", "Mở báo cáo thu", "/ward/reports")));
        }
        return new(now, items);
    }

    private const string Omit = "\u0000";
    private static ChatbotField Opt(string label, string? value) => new(label, value ?? Omit);
    private static ChatbotPlace? Place(StorefrontSummaryDto? shop, decimal? price) => shop is null ? null
        : new(shop.Latitude, shop.Longitude, shop.DistanceMeters, shop.IsOpenNow, shop.CommunityRating, shop.CommunityCount, price);
    public static string? Distance(double? meters) => meters switch
    {
        null => null,
        < 1000 => $"~{Math.Max(50, Math.Round(meters.Value / 50) * 50):0} m",
        _ => $"~{(meters.Value / 1000).ToString("0.0", CultureInfo.GetCultureInfo("vi-VN"))} km",
    };
    private static ChatbotException InvalidArguments() => new(400, "invalid_tool_arguments", "Tham số tra cứu không hợp lệ. Hãy chọn lại hồ sơ.");
    private static ChatbotField Money(string label, decimal amount) => new(label, amount.ToString("N0", CultureInfo.GetCultureInfo("vi-VN")) + " VND");
    private static ChatbotField F(string label, object? value) => new(label, value switch
    {
        null => "Chưa ghi nhận",
        DateOnly date => date.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture),
        string s => ChatbotPrivacy.Text(StatusLabel(s), 350),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
    });

    private static string StatusLabel(string value) => value switch
    {
        "PENDING" => "Chờ xử lý", "SUBMITTED" => "Đã gửi", "UNDER_REVIEW" => "Đang xem xét",
        "MORE_INFORMATION_REQUIRED" => "Cần bổ sung", "WITHDRAWN" => "Đã rút", "WAIVED" => "Được miễn",
        "APPROVED" => "Đã duyệt", "REJECTED" => "Đã từ chối", "NEEDS_REVISION" => "Cần bổ sung",
        "DRAFT" => "Bản nháp", "ACTIVE" => "Đang hiệu lực", "EXPIRED" => "Đã hết hạn",
        "SUSPENDED" => "Tạm đình chỉ", "CANCELLED" => "Đã hủy", "TERMINATED" => "Đã kết thúc",
        "PAID" => "Đã thanh toán", "UNPAID" => "Chưa thanh toán", "OVERDUE" => "Quá hạn",
        "SUCCESS" or "SUCCEEDED" => "Thành công", "FAILED" => "Không thành công",
        "AVAILABLE" => "Còn trống", "RENTED" => "Đang cho thuê", "HELD" => "Đang được giữ",
        "VALID" => "Hợp lệ", "INVALID" => "Không hợp lệ", "REVOKED" => "Đã thu hồi",
        "NOT_YET_VALID" => "Chưa đến ngày hiệu lực", "NO_PERMIT" => "Chưa có giấy phép",
        "FIXED_STOREFRONT" or "FIXED" => "Cửa hàng cố định", "ITINERANT" or "MOBILE" => "Lưu động",
        _ => value,
    };
}
