using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using MediatR;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;
using Moq;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;

namespace StreetBiz.API.Tests;

/// <summary>
/// HTTP contract of the vendor-registration, vendor-finance and platform-administration
/// controllers: every route reaches MediatR with the route, query and body values in the right
/// command fields, file endpoints stream with the right content type, and the bearer token is
/// only honoured while its session and account are live.
/// </summary>
public sealed class ControllerHttpMappingTests : IDisposable
{
    private const string Key = "controller-mapping-tests-signing-key-0123456789";
    private const long UserId = 5;
    private const long SessionId = 40;
    private readonly RecordingSender sender = new();
    private readonly Mock<ISessionRepository> sessions = new();
    private readonly Mock<IUserAccountRepository> users = new();
    private readonly WebApplicationFactory<Program> host;
    private readonly HttpClient client;

    public ControllerHttpMappingTests()
    {
        var now = DateTime.UtcNow;
        sessions.Setup(s => s.GetByIdAsync(SessionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AppSession(SessionId, UserId, "test", "127.0.0.1", now.AddHours(-1), now, now.AddDays(1), null));
        users.Setup(u => u.GetByIdAsync(UserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AppUser(UserId, "0905000101", "x", "Test", RoleCodes.Vendor, 10, AccountStatuses.Active, now));
        host = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:StreetBizDatabase", "Server=unused;Database=unused;Integrated Security=True");
            builder.UseSetting("Jwt:SigningKey", Key);
            builder.UseSetting("Jwt:Issuer", "StreetBiz");
            builder.UseSetting("Jwt:Audience", "StreetBiz");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:StreetBizDatabase"] = "Server=unused;Database=unused;Integrated Security=True",
                ["Jwt:SigningKey"] = Key,
                ["Jwt:Issuer"] = "StreetBiz",
                ["Jwt:Audience"] = "StreetBiz",
            }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ISender>();
                services.AddSingleton<ISender>(sender);
                services.RemoveAll<ISessionRepository>();
                services.AddSingleton(sessions.Object);
                services.RemoveAll<IUserAccountRepository>();
                services.AddSingleton(users.Object);
                // The fee/tax/slot sweepers also go through ISender; they are not under test here.
                foreach (var sweeper in services.Where(d => d.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService)
                             && d.ImplementationType?.Assembly.GetName().Name?.StartsWith("StreetBiz", StringComparison.Ordinal) == true).ToList())
                {
                    services.Remove(sweeper);
                }
            });
        });
        client = host.CreateClient(new() { BaseAddress = new("https://localhost") });
        client.DefaultRequestHeaders.Authorization = new("Bearer", Token());
    }

    public void Dispose()
    {
        client.Dispose();
        host.Dispose();
    }

    private static string Token(long sid = SessionId) =>
        new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken("StreetBiz", "StreetBiz",
            [new Claim("sub", UserId.ToString()), new Claim("sid", sid.ToString()), new Claim("role", RoleCodes.Vendor)],
            DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(10),
            new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Key)), SecurityAlgorithms.HmacSha256)));

    private object Sent(string typeName) =>
        sender.Requests.LastOrDefault(r => r.GetType().Name == typeName)
        ?? throw new Xunit.Sdk.XunitException($"{typeName} was not sent; sent: {string.Join(", ", sender.Requests.Select(r => r.GetType().Name))}");

    private static object? P(object request, string property) => request.GetType().GetProperty(property)!.GetValue(request);

    // ---------------- session gate ----------------

    [Fact]
    public async Task Without_a_token_every_controller_answers_401_and_nothing_is_sent()
    {
        using var anonymous = host.CreateClient(new() { BaseAddress = new("https://localhost") });

        foreach (var url in new[] { "/api/vendor/registrations", "/api/vendor/finance/summary", "/api/platform/me", "/api/ward/enrollments" })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(url)).StatusCode);
        }

        Assert.True(sender.Requests.Count == 0, "sent: " + string.Join(", ", sender.Requests.Select(r => r.GetType().Name)));
    }

    [Fact]
    public async Task A_signed_out_session_or_a_suspended_account_loses_access_at_once()
    {
        var now = DateTime.UtcNow;
        sessions.Setup(s => s.GetByIdAsync(41, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AppSession(41, UserId, null, null, now.AddHours(-1), now, now.AddDays(1), now.AddMinutes(-1)));
        using var revoked = host.CreateClient(new() { BaseAddress = new("https://localhost") });
        revoked.DefaultRequestHeaders.Authorization = new("Bearer", Token(sid: 41));
        Assert.Equal(HttpStatusCode.Unauthorized, (await revoked.GetAsync("/api/vendor/registrations")).StatusCode);

        users.Setup(u => u.GetByIdAsync(UserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AppUser(UserId, "0905000101", "x", "Test", RoleCodes.Vendor, 10, "SUSPENDED", now));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/vendor/registrations")).StatusCode);
        Assert.Empty(sender.Requests);
    }

    // ---------------- REG-01..05 ----------------

    [Fact]
    public async Task Registration_routes_reach_their_commands()
    {
        var draft = await client.PostAsJsonAsync("/api/vendor/registrations", new
        {
            vendorType = "FIXED_STOREFRONT", displayName = "Quán A", declaredAddress = "12 Lê Duẩn", wardUnitId = 10,
            addressLatitude = 16.06m, businessLine = "Bánh mì", capitalAmount = 20000000m, laborCount = 1, foodSafetyCommitment = true,
        });
        Assert.Equal(HttpStatusCode.OK, draft.StatusCode);
        var submit = Sent("SubmitRegistrationCommand");
        Assert.Equal("FIXED_STOREFRONT", P(submit, "VendorType"));
        Assert.Equal("Quán A", P(submit, "DisplayName"));
        Assert.Equal(10, P(submit, "WardUnitId"));
        Assert.Equal(16.06m, P(submit, "AddressLatitude"));
        Assert.Equal(true, P(submit, "FoodSafetyCommitment"));

        await client.PostAsJsonAsync("/api/vendor/registrations", new { });
        Assert.Equal("", P(Sent("SubmitRegistrationCommand"), "VendorType")); // missing values reach the validator as empty, not as a null crash
        Assert.Equal(0, P(Sent("SubmitRegistrationCommand"), "WardUnitId"));

        Assert.True((await client.GetAsync("/api/vendor/registrations")).IsSuccessStatusCode);
        Sent("TrackRegistrationsQuery");
        Assert.True((await client.GetAsync("/api/vendor/registrations/7")).IsSuccessStatusCode);
        Assert.Equal(7L, P(Sent("GetRegistrationQuery"), "RegistrationId"));

        await client.PostAsJsonAsync("/api/vendor/registrations/7/evidence", new { evidenceType = "BUSINESS_LICENSE", fileUrl = "/api/uploads/evidence/5/a.jpg", biometricConsent = true });
        var evidence = Sent("SubmitEvidenceCommand");
        Assert.Equal(7L, P(evidence, "RegistrationId"));
        Assert.Equal("BUSINESS_LICENSE", P(evidence, "EvidenceType"));
        Assert.Equal(true, P(evidence, "BiometricConsent"));

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync("/api/vendor/registrations/7/evidence/3")).StatusCode);
        Assert.Equal(3L, P(Sent("RemoveEvidenceCommand"), "EvidenceId"));

        Assert.True((await client.PostAsync("/api/vendor/registrations/7/submit", null)).IsSuccessStatusCode);
        Assert.Equal(7L, P(Sent("FileRegistrationCommand"), "RegistrationId"));

        await client.PutAsJsonAsync("/api/vendor/registrations/7", new { vendorType = "ITINERANT", displayName = "Xe A", wardUnitId = 10 });
        var update = Sent("UpdateRegistrationCommand");
        Assert.Equal(7L, P(update, "RegistrationId"));
        Assert.Equal("ITINERANT", P(update, "VendorType"));

        Assert.True((await client.PostAsync("/api/vendor/registrations/7/withdraw", null)).IsSuccessStatusCode);
        Assert.Equal(7L, P(Sent("WithdrawRegistrationCommand"), "RegistrationId"));
    }

    [Theory]
    [InlineData("pdf", "application/pdf")]
    [InlineData("docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document")]
    public async Task The_registration_form_downloads_in_the_requested_format(string format, string contentType)
    {
        sender.Respond = r => r.GetType().Name == "GenerateRegistrationDocumentQuery" ? (new byte[] { 1, 2, 3 }, "Mau01.x") : null;

        var response = await client.GetAsync($"/api/vendor/registrations/7/document?format={format}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(contentType, response.Content.Headers.ContentType!.MediaType);
        Assert.Equal(new byte[] { 1, 2, 3 }, await response.Content.ReadAsByteArrayAsync());
        Assert.Equal(format, P(Sent("GenerateRegistrationDocumentQuery"), "Format"));
    }

    // ---------------- FEE-01..05 ----------------

    [Fact]
    public async Task Finance_routes_reach_their_queries_with_route_and_query_values()
    {
        foreach (var (url, type, property, expected) in new (string, string, string?, object?)[]
        {
            ("/api/vendor/finance/summary", "GetFinanceSummaryQuery", null, null),
            ("/api/vendor/finance/fees?status=OVERDUE", "ListFeeItemsQuery", "Status", "OVERDUE"),
            ("/api/vendor/finance/penalties?status=UNPAID", "ListPenaltiesQuery", "Status", "UNPAID"),
            ("/api/vendor/finance/payments", "ListPaymentTransactionsQuery", null, null),
            ("/api/vendor/finance/violations", "ListVendorViolationsQuery", null, null),
            ("/api/vendor/finance/penalties/9/treasury", "GetPenaltyTreasuryQuery", "PenaltyId", 9L),
            ("/api/vendor/finance/payments/11/bank-transfer", "GetFinanceBankTransferQuery", "TransactionId", 11L),
            ("/api/vendor/finance/invoices", "ListInvoicesQuery", null, null),
            ("/api/vendor/finance/invoices/12", "GetInvoiceQuery", "InvoiceId", 12L),
            ("/api/vendor/finance/contracts", "ListVendorContractsQuery", null, null),
            ("/api/vendor/finance/contracts/3/schedule", "GetContractScheduleQuery", "ContractId", 3L),
            ("/api/vendor/finance/fees/4", "GetFeeItemDetailQuery", "FeeItemId", 4L),
        })
        {
            var response = await client.GetAsync(url);
            Assert.True(response.IsSuccessStatusCode, $"{url} -> {(int)response.StatusCode}");
            var request = Sent(type);
            if (property is not null)
            {
                Assert.Equal(expected, P(request, property));
            }
        }
    }

    [Fact]
    public async Task Paying_a_fee_carries_the_provider_and_the_idempotency_key()
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, "/api/vendor/finance/fees/4/checkout") { Content = JsonContent.Create(new { provider = "PAYOS" }) };
        message.Headers.Add("Idempotency-Key", "key-123");

        Assert.True((await client.SendAsync(message)).IsSuccessStatusCode);

        var command = Sent("PayFeeCommand");
        Assert.Equal(4L, P(command, "FeeItemId"));
        Assert.Equal("PAYOS", P(command, "Provider"));
        Assert.Equal("key-123", P(command, "IdempotencyKey"));
    }

    [Fact]
    public async Task Receipts_sync_and_sandbox_routes()
    {
        await client.PostAsJsonAsync("/api/vendor/finance/penalties/9/receipts", new { receiptUrl = "/api/uploads/evidence/5/r.jpg", reference = "KB-1", paidOn = "2026-11-01" });
        var receipt = Sent("SubmitPenaltyReceiptCommand");
        Assert.Equal(9L, P(receipt, "PenaltyId"));
        Assert.Equal(new DateOnly(2026, 11, 1), P(P(receipt, "Request")!, "PaidOn"));

        Assert.True((await client.PostAsync("/api/vendor/finance/payments/11/sync", null)).IsSuccessStatusCode);
        Assert.Equal(11L, P(Sent("SyncFinancePaymentCommand"), "TransactionId"));

        // Outside Development the sandbox confirmation does not exist at all.
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/api/vendor/finance/payments/11/sandbox-confirm", null)).StatusCode);
        Assert.DoesNotContain(sender.Requests, r => r.GetType().Name == "ConfirmSandboxFinancePaymentCommand");
    }

    [Fact]
    public async Task Invoice_pdf_and_annual_statement_stream_as_files()
    {
        sender.Respond = r => r.GetType().Name is "GetInvoicePdfQuery" or "GetVendorStatementQuery"
            ? new FinanceFile("HD-1.pdf", "application/pdf", [9, 9]) : null;

        var pdf = await client.GetAsync("/api/vendor/finance/invoices/12/pdf");
        var statement = await client.GetAsync("/api/vendor/finance/statement?year=2026");

        Assert.Equal("application/pdf", pdf.Content.Headers.ContentType!.MediaType);
        Assert.Equal("HD-1.pdf", pdf.Content.Headers.ContentDisposition!.FileNameStar ?? pdf.Content.Headers.ContentDisposition.FileName);
        Assert.Equal(12L, P(Sent("GetInvoicePdfQuery"), "InvoiceId"));
        Assert.Equal(HttpStatusCode.OK, statement.StatusCode);
        Assert.Equal(2026, P(Sent("GetVendorStatementQuery"), "Year"));
    }

    // ---------------- ADM-01..07 ----------------

    [Fact]
    public async Task Food_category_routes()
    {
        Assert.True((await client.GetAsync("/api/platform/me")).IsSuccessStatusCode);
        Sent("GetPlatformAdminProfileQuery");
        Assert.True((await client.GetAsync("/api/platform/food-categories")).IsSuccessStatusCode);
        Sent("ListFoodCategoriesQuery");

        var created = await client.PostAsJsonAsync("/api/platform/food-categories", new { name = "Đồ nướng" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("Đồ nướng", P(Sent("CreateFoodCategoryCommand"), "Name"));

        await client.PutAsJsonAsync("/api/platform/food-categories/8", new { name = "Đồ nướng & lẩu" });
        var rename = Sent("RenameFoodCategoryCommand");
        Assert.Equal(8, P(rename, "CategoryId"));
        Assert.Equal("Đồ nướng & lẩu", P(rename, "Name"));

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync("/api/platform/food-categories/8")).StatusCode);
        Assert.Equal(8, P(Sent("DeleteFoodCategoryCommand"), "CategoryId"));
    }

    [Fact]
    public async Task Moderation_routes_carry_the_decision_and_expected_status()
    {
        await client.GetAsync("/api/platform/reported-content?status=PENDING&page=2&pageSize=10");
        var list = Sent("ListReportedContentQuery");
        Assert.Equal("PENDING", P(list, "Status"));
        Assert.Equal(2, P(list, "Page"));
        Assert.Equal(10, P(list, "PageSize"));

        await client.GetAsync("/api/platform/reported-content/3");
        Assert.Equal(3L, P(Sent("GetReportedContentQuery"), "ReportId"));

        foreach (var (action, decision) in new[] { ("dismiss", "DISMISS"), ("hide", "HIDE") })
        {
            await client.PostAsJsonAsync($"/api/platform/reported-content/3/{action}", new { expectedStatus = "PENDING" });
            var command = Sent("DecideReportedContentCommand");
            Assert.Equal(decision, P(command, "Decision"));
            Assert.Equal(3L, P(command, "ReportId"));
            Assert.Equal("PENDING", P(command, "ExpectedStatus"));
        }

        await client.PostAsJsonAsync("/api/platform/reported-content/3/restore", new { expectedStatus = "HIDDEN" });
        var restore = Sent("RestoreReportedContentCommand");
        Assert.Equal(3L, P(restore, "ReportId"));
        Assert.Equal("HIDDEN", P(restore, "ExpectedStatus"));
    }

    [Fact]
    public async Task Complaint_dashboard_export_and_account_routes()
    {
        await client.GetAsync("/api/platform/order-complaints?status=OPEN");
        Assert.Equal("OPEN", P(Sent("ListOrderComplaintsQuery"), "Status"));
        await client.GetAsync("/api/platform/order-complaints/4");
        Assert.Equal(4L, P(Sent("GetOrderComplaintQuery"), "ComplaintId"));
        await client.PostAsJsonAsync("/api/platform/order-complaints/4/decision", new { decision = "RESOLVE", notes = "Hoàn 5.000đ", expectedStatus = "OPEN", approvedRefundAmount = 5000 });
        var decision = Sent("DecideOrderComplaintCommand");
        Assert.Equal("RESOLVE", P(decision, "Decision"));
        Assert.Equal(5000m, P(decision, "ApprovedRefundAmount"));

        await client.GetAsync("/api/platform/dashboard?from=2026-10-01&to=2026-10-31");
        var dashboard = Sent("GetPlatformDashboardQuery");
        Assert.Equal(new DateOnly(2026, 10, 1), P(dashboard, "From"));
        Assert.Equal(new DateOnly(2026, 10, 31), P(dashboard, "To"));

        sender.Respond = r => r.GetType().Name is "ExportPlatformReportCommand" or "DownloadPlatformExportQuery"
            ? new PlatformReportFile([1], "bao-cao.xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", 77) : null;
        var export = await client.PostAsJsonAsync("/api/platform/reports/export", new { reportType = "ORDERS", from = "2026-10-01", to = "2026-10-31", format = "xlsx" });
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        Assert.Equal("77", export.Headers.GetValues("X-Export-Id").Single());
        Assert.Equal("ORDERS", P(Sent("ExportPlatformReportCommand"), "ReportType"));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/platform/reports/exports/77/download")).StatusCode);
        Assert.Equal(77L, P(Sent("DownloadPlatformExportQuery"), "ExportId"));
        await client.GetAsync("/api/platform/reports/exports?page=2&pageSize=5");
        Assert.Equal(5, P(Sent("ListPlatformExportsQuery"), "PageSize"));

        await client.GetAsync("/api/platform/accounts?role=VENDOR&status=ACTIVE&q=lan&page=1&pageSize=20");
        var accounts = Sent("ListPlatformAccountsQuery");
        Assert.Equal("VENDOR", P(accounts, "Role"));
        Assert.Equal("lan", P(accounts, "Search"));
        await client.GetAsync("/api/platform/accounts/9");
        Assert.Equal(9L, P(Sent("GetPlatformAccountQuery"), "UserId"));
        await client.PostAsJsonAsync("/api/platform/accounts/9/suspend", new { reason = "Lạm dụng", expectedStatus = "ACTIVE" });
        Assert.Equal("Lạm dụng", P(Sent("SuspendPlatformAccountCommand"), "Reason"));
        await client.PostAsJsonAsync("/api/platform/accounts/9/reactivate", new { note = "Đã xác minh", expectedStatus = "SUSPENDED" });
        Assert.Equal("SUSPENDED", P(Sent("ReactivatePlatformAccountCommand"), "ExpectedStatus"));
    }

    // ---------------- WARD-04..13 ----------------

    [Fact]
    public async Task Ward_compliance_read_routes_reach_their_queries()
    {
        foreach (var (url, type, property, expected) in new (string, string, string?, object?)[]
        {
            ("/api/ward/enrollments?status=SUBMITTED&page=2&vendorType=ITINERANT", "ListWardEnrollmentsQuery", "VendorType", "ITINERANT"),
            ("/api/ward/enrollments/6", "GetWardEnrollmentDetailQuery", "Id", 6L),
            ("/api/ward/enrollments/6/fast-track-check", "CheckWardFastTrackQuery", "Id", 6L),
            ("/api/ward/rental-applications?status=PENDING&page=3", "ListWardRentalApplicationsQuery", "Page", 3),
            ("/api/ward/rental-applications/8", "GetWardRentalApplicationDetailQuery", "Id", 8L),
            ("/api/ward/renewals?status=PENDING", "ListWardRenewalsQuery", "Status", "PENDING"),
            ("/api/ward/renewals/9", "GetWardRenewalDetailQuery", "Id", 9L),
            ("/api/ward/penalty-schedules?asOf=2026-11-01", "ListWardPenaltySchedulesQuery", "AsOf", new DateOnly(2026, 11, 1)),
            ("/api/ward/violations?status=UNPAID&page=1", "ListWardViolationsQuery", "Status", "UNPAID"),
            ("/api/ward/violations/4", "GetWardViolationDetailQuery", "Id", 4L),
            ("/api/ward/insights/risk-queue", "GetWardRiskQueueQuery", null, null),
            ("/api/ward/insights/patrol-heatmap", "GetWardPatrolHeatmapQuery", null, null),
            ("/api/ward/insights/geofence-drift", "GetGeofenceDriftQuery", null, null),
        })
        {
            var response = await client.GetAsync(url);
            Assert.True(response.IsSuccessStatusCode, $"{url} -> {(int)response.StatusCode}");
            var request = Sent(type);
            if (property is not null)
            {
                Assert.Equal(expected, P(request, property));
            }
        }
    }

    [Fact]
    public async Task Ward_compliance_decisions_carry_route_id_and_body()
    {
        await client.PostAsJsonAsync("/api/ward/enrollments/6/decision", new { decision = "APPROVE", reason = "Đủ hồ sơ", expectedStatus = "SUBMITTED" });
        var enrollment = Sent("DecideWardEnrollmentCommand");
        Assert.Equal(6L, P(enrollment, "Id"));
        Assert.Equal("APPROVE", P(P(enrollment, "Decision")!, "Decision"));

        await client.PostAsync("/api/ward/enrollments/6/claim", null);
        Assert.Equal(6L, P(Sent("ClaimWardEnrollmentCommand"), "Id"));
        await client.PostAsJsonAsync("/api/ward/enrollments/6/confirm-identity", new { note = "Đã đối chiếu" });
        Assert.Equal("Đã đối chiếu", P(P(Sent("ConfirmEnrollmentIdentityCommand"), "Request")!, "Note"));

        await client.PostAsJsonAsync("/api/ward/rental-applications/8/decision", new { decision = "REJECT", reason = "Sát trụ cứu hỏa", expectedStatus = "PENDING" });
        Assert.Equal("REJECT", P(P(Sent("DecideWardRentalApplicationCommand"), "Decision")!, "Decision"));
        await client.PostAsJsonAsync("/api/ward/renewals/9/decision", new { decision = "APPROVE", reason = "ok", expectedStatus = "PENDING" });
        Assert.Equal(9L, P(Sent("DecideWardRenewalCommand"), "Id"));
        await client.PostAsJsonAsync("/api/ward/renewals/batch-decision", new { items = new[] { new { renewalId = 9, expectedStatus = "PENDING" } }, decision = "APPROVE", reason = "ok" });
        Sent("BatchDecideWardRenewalsCommand");

        await client.PostAsJsonAsync("/api/ward/permits/inspect", new { permitCodeOrPayload = "SBP1.x", latitude = 16.06, longitude = 108.21 });
        Assert.Equal("SBP1.x", P(P(Sent("InspectWardPermitQuery"), "Request")!, "PermitCodeOrPayload"));
        await client.PostAsJsonAsync("/api/ward/permits/3/action", new { action = "SUSPEND", reason = "Vi phạm" });
        Assert.Equal(3L, P(Sent("ExecuteWardPermitActionCommand"), "PermitId"));

        await client.PostAsJsonAsync("/api/ward/violations", new { contractId = 1, violationType = "OUTSIDE_SLOT", description = "Lấn lối đi" });
        Assert.Equal("OUTSIDE_SLOT", P(P(Sent("RecordWardViolationCommand"), "Request")!, "ViolationType"));
        await client.PostAsJsonAsync("/api/ward/violations/4/explanation", new { content = "Giải trình" });
        Assert.Equal(4L, P(Sent("RecordWardExplanationCommand"), "ViolationId"));
        await client.PostAsJsonAsync("/api/ward/violations/4/deliver", new { deliveredToName = "Chủ hộ", refused = false });
        Assert.Equal(4L, P(Sent("DeliverWardViolationCommand"), "ViolationId"));
        await client.PostAsJsonAsync("/api/ward/violations/4/sanction", new { penaltyScheduleId = 2, decisionNumber = "QĐ-1" });
        Assert.Equal("QĐ-1", P(P(Sent("SanctionWardViolationCommand"), "Request")!, "DecisionNumber"));
    }

    [Fact]
    public async Task Ward_documents_stream_as_files()
    {
        sender.Respond = r => r.GetType().Name == "GenerateEnrollmentDocumentQuery" ? (new byte[] { 7 }, "Mau01.pdf") : null;
        var form = await client.GetAsync("/api/ward/enrollments/6/document?format=pdf");
        Assert.Equal("application/pdf", form.Content.Headers.ContentType!.MediaType);
        Assert.Equal("pdf", P(Sent("GenerateEnrollmentDocumentQuery"), "Format"));
    }

    /// <summary>Records every request; answers with whatever <see cref="Respond"/> returns (default otherwise).</summary>
    // ---------------- authentication (AUTH-02..09) ----------------

    [Fact]
    public async Task Anonymous_auth_routes_carry_the_device_and_reach_their_commands()
    {
        client.DefaultRequestHeaders.Authorization = null;
        client.DefaultRequestHeaders.UserAgent.ParseAdd("StreetBizTest/1.0");

        var otp = await client.PostAsJsonAsync("/api/auth/send-otp", new { phoneNumber = "0905000101", purpose = "SIGNUP" });
        Assert.Equal(HttpStatusCode.OK, otp.StatusCode);
        Assert.Equal("SIGNUP", P(Sent("SendOtpCommand"), "Purpose"));

        await client.PostAsJsonAsync("/api/auth/register", new
        {
            phoneNumber = "0905000101", password = "Str0ng!Pass", fullName = "Chị Lan", roleCode = "VENDOR", wardUnitId = 10, otp = "123456",
        });
        var register = Sent("RegisterCommand");
        Assert.Equal("Chị Lan", P(register, "FullName"));
        Assert.Equal(10, P(register, "WardUnitId"));
        Assert.Equal("StreetBizTest/1.0", P(register, "DeviceInfo"));

        await client.PostAsJsonAsync("/api/auth/login", new { phoneNumber = "0905000101", password = "Str0ng!Pass" });
        Assert.Equal("StreetBizTest/1.0", P(Sent("LoginCommand"), "DeviceInfo"));

        await client.PostAsJsonAsync("/api/auth/login-otp", new { phoneNumber = "0905000101", otp = "654321" });
        Assert.Equal("654321", P(Sent("LoginWithOtpCommand"), "Otp"));

        await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = "refresh-1" });
        Assert.Equal("refresh-1", P(Sent("RefreshTokenCommand"), "RefreshToken"));

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/auth/forgot-password", new { phoneNumber = "0905000101" })).StatusCode);
        Assert.Equal("0905000101", P(Sent("RequestPasswordResetCommand"), "PhoneNumber"));

        await client.PostAsJsonAsync("/api/auth/reset-password", new { phoneNumber = "0905000101", otp = "111222", newPassword = "N3w!Passw0rd" });
        var reset = Sent("ResetPasswordCommand");
        Assert.Equal("111222", P(reset, "Otp"));
        Assert.Equal("N3w!Passw0rd", P(reset, "NewPassword"));
    }

    [Fact]
    public async Task Signed_in_auth_routes_need_a_token_and_reach_their_commands()
    {
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/auth/logout", null)).StatusCode);
        Sent("LogoutCommand");

        await client.PostAsJsonAsync("/api/auth/change-password", new { currentPassword = "Old!Pass1", newPassword = "N3w!Passw0rd" });
        Assert.Equal("Old!Pass1", P(Sent("ChangePasswordCommand"), "CurrentPassword"));

        Assert.True((await client.GetAsync("/api/auth/sessions")).IsSuccessStatusCode);
        Sent("ListSessionsQuery");

        Assert.Equal(HttpStatusCode.OK, (await client.DeleteAsync("/api/auth/sessions")).StatusCode);
        Sent("RevokeOtherSessionsCommand");

        Assert.Equal(HttpStatusCode.OK, (await client.DeleteAsync("/api/auth/sessions/41")).StatusCode);
        Assert.Equal(41L, P(Sent("RevokeSessionCommand"), "SessionId"));
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync("/api/auth/sessions/abc")).StatusCode);

        Assert.True((await client.GetAsync("/api/auth/login-history?take=5")).IsSuccessStatusCode);
        Assert.Equal(5, P(Sent("LoginHistoryQuery"), "Take"));

        var before = sender.Requests.Count;
        client.DefaultRequestHeaders.Authorization = null;
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/api/auth/logout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/sessions")).StatusCode);
        Assert.Equal(before, sender.Requests.Count);
    }

    private static MultipartFormDataContent PhotoForm(byte[] bytes)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new("image/png");
        form.Add(file, "file", "photo.png");
        return form;
    }

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13];

    [Fact]
    public async Task Avatar_upload_and_removal_reach_their_commands()
    {
        sender.Respond = request => request switch
        {
            StreetBiz.Application.Features.Authentication.Avatar.SetAvatarCommand =>
                new StreetBiz.Application.Features.Authentication.Avatar.AvatarDto("/api/uploads/avatars/7/x.jpg"),
            StreetBiz.Application.Features.Authentication.Avatar.RemoveAvatarCommand =>
                new StreetBiz.Application.Features.Authentication.Avatar.AvatarDto(null),
            _ => null,
        };

        var uploaded = await client.PostAsync("/api/account/avatar", PhotoForm([.. PngSignature, 1, 2, 3]));
        Assert.Equal(HttpStatusCode.OK, uploaded.StatusCode);
        Assert.Contains("/api/uploads/avatars/7/x.jpg", await uploaded.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, (await client.DeleteAsync("/api/account/avatar")).StatusCode);
        Assert.Contains(sender.Requests, r => r is StreetBiz.Application.Features.Authentication.Avatar.SetAvatarCommand);
        Assert.Contains(sender.Requests, r => r is StreetBiz.Application.Features.Authentication.Avatar.RemoveAvatarCommand);
    }

    [Fact]
    public async Task Avatar_upload_refuses_a_non_image_before_any_command()
    {
        var response = await client.PostAsync("/api/account/avatar", PhotoForm("<?php echo 1; ?>"u8.ToArray()));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(MenuImageFiles.InvalidType, await response.Content.ReadAsStringAsync());
        Assert.DoesNotContain(sender.Requests, r => r is StreetBiz.Application.Features.Authentication.Avatar.SetAvatarCommand);
    }

    [Fact]
    public async Task An_upload_over_the_size_limit_answers_413_in_vietnamese()
    {
        var response = await client.PostAsync("/api/account/avatar", PhotoForm(new byte[6 * 1024 * 1024]));

        Assert.True((int)response.StatusCode == 413, await response.Content.ReadAsStringAsync());
        Assert.Contains(EvidenceFiles.TooLarge, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Avatar_files_are_public_and_only_valid_names_are_looked_up()
    {
        using var anonymous = host.CreateClient(new() { BaseAddress = new("https://localhost") });

        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync("/api/uploads/avatars/7/..%2Fsecret.jpg")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync("/api/uploads/avatars/7/" + new string('a', 32) + ".jpg")).StatusCode);
    }

    private sealed class RecordingSender : ISender
    {
        public List<object> Requests { get; } = [];
        public Func<object, object?> Respond { get; set; } = _ => null;

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            lock (Requests) Requests.Add(request);
            return Task.FromResult(Respond(request) is TResponse value ? value : default!);
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest
        {
            lock (Requests) Requests.Add(request!);
            return Task.CompletedTask;
        }

        public Task<object?> Send(object request, CancellationToken cancellationToken = default)
        {
            lock (Requests) Requests.Add(request);
            return Task.FromResult(Respond(request));
        }

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            Empty<TResponse>();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) => Empty<object?>();

        private static async IAsyncEnumerable<T> Empty<T>()
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
