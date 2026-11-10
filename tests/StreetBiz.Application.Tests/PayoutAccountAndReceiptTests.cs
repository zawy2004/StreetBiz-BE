using FluentAssertions;
using Moq;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.Features.PayoutAccounts;
using StreetBiz.Application.Features.PenaltyReceipts;
using StreetBiz.Application.Features.WardFinance;
using StreetBiz.Application.Features.WardSlots;

namespace StreetBiz.Application.Tests;

/// <summary>Connecting a receiving account, and the Treasury receipts of fines.</summary>
public sealed class PayoutAccountAndReceiptTests
{
    private static readonly DateTime Now = new(2026, 11, 2, 3, 0, 0, DateTimeKind.Utc);
    private static readonly PayoutOwner Household = new(PayeeOwnerTypes.Vendor, 4, null, 5);
    private static readonly PayeeChannelInfo Channel = new("MB Bank", "0123456789", "NGUYEN THI LAN", true);

    // ------------------------------------------------------------------------ payout accounts

    private sealed class Wiring
    {
        public Mock<IPayeeAccountRepository> Payees { get; } = new();
        public Mock<IPayeeChannelGateway> Gateway { get; } = new();
        public Mock<ICredentialProtector> Protector { get; } = new();

        public Wiring()
        {
            Protector.SetupGet(x => x.IsConfigured).Returns(true);
            Gateway.Setup(x => x.WebhookUrlFor(It.IsAny<string>())).Returns<string>(token => $"https://api.example/api/payments/payos/callback/{token}");
            Gateway.Setup(x => x.VerifyChannelAsync(It.IsAny<PayeeCredentials>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(Channel);
            Payees.Setup(x => x.SaveAsync(It.IsAny<SavePayeeAccount>(), It.IsAny<long>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((SavePayeeAccount a, long _, DateTime _, CancellationToken _) => Row(a.WebhookToken));
        }

        public PayoutAccountService Service => new(Payees.Object, Gateway.Object, Protector.Object, new FixedClock(Now));
    }

    private static PayeeAccountRow Row(string token = "tok", string status = "ACTIVE", string? error = null) =>
        new(9, "VENDOR", 4, null, "client-123456", "MB Bank", "0123456789", "NGUYEN THI LAN", token, status, error, Now, Now, Now, Now, null);

    private static ConnectPayoutAccountRequest Keys => new(" client-123456 ", " api-key ", " checksum-key ");

    [Fact]
    public async Task Connecting_checks_the_keys_with_payos_registers_the_webhook_and_stores_what_it_reported()
    {
        var w = new Wiring();
        SavePayeeAccount? saved = null;
        w.Payees.Setup(x => x.SaveAsync(It.IsAny<SavePayeeAccount>(), 5, Now, It.IsAny<CancellationToken>()))
            .Callback<SavePayeeAccount, long, DateTime, CancellationToken>((a, _, _, _) => saved = a)
            .ReturnsAsync(Row());

        var dto = await w.Service.ConnectAsync(Household, Keys, default);

        saved!.ClientId.Should().Be("client-123456");
        saved.ApiKey.Should().Be("api-key");
        saved.ChecksumKey.Should().Be("checksum-key");
        saved.WebhookToken.Should().MatchRegex("^[A-Za-z0-9_-]{43}$"); // 32 random bytes, base64url
        saved.Channel.Should().Be(Channel);
        w.Gateway.Verify(x => x.VerifyChannelAsync(
            It.Is<PayeeCredentials>(c => c.ApiKey == "api-key"),
            $"https://api.example/api/payments/payos/callback/{saved.WebhookToken}",
            It.IsAny<CancellationToken>()), Times.Once);
        dto.Connected.Should().BeTrue();
        dto.WebhookConfirmed.Should().BeTrue();
        dto.AccountNumberMasked.Should().Be("••••••6789");
        dto.ClientIdMasked.Should().Be("••••••3456");
    }

    [Fact]
    public async Task Keys_that_payos_refuses_are_not_stored()
    {
        var w = new Wiring();
        w.Gateway.Setup(x => x.VerifyChannelAsync(It.IsAny<PayeeCredentials>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new PayeeChannelException("payOS từ chối khoá", unauthorized: true));

        var act = () => w.Service.ConnectAsync(Household, Keys, default);

        await act.Should().ThrowAsync<PayeeChannelException>();
        w.Payees.Verify(x => x.SaveAsync(It.IsAny<SavePayeeAccount>(), It.IsAny<long>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Without_an_encryption_key_nothing_can_be_connected()
    {
        var w = new Wiring();
        w.Protector.SetupGet(x => x.IsConfigured).Returns(false);

        var act = () => w.Service.ConnectAsync(Household, Keys, default);

        await act.Should().ThrowAsync<DomainRuleException>().WithMessage("*CredentialKey*");
        w.Gateway.Verify(x => x.VerifyChannelAsync(It.IsAny<PayeeCredentials>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Reconnecting_keeps_the_webhook_url_registered_on_payos()
    {
        var w = new Wiring();
        w.Payees.Setup(x => x.GetForVendorAsync(4, It.IsAny<CancellationToken>())).ReturnsAsync(Row(token: "kept-token"));

        await w.Service.ConnectAsync(Household, Keys, default);

        w.Gateway.Verify(x => x.VerifyChannelAsync(It.IsAny<PayeeCredentials>(), "https://api.example/api/payments/payos/callback/kept-token", It.IsAny<CancellationToken>()), Times.Once);
        w.Payees.Verify(x => x.SaveAsync(It.Is<SavePayeeAccount>(a => a.WebhookToken == "kept-token"), 5, Now, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Checking_with_refused_keys_marks_the_account_in_error_and_shows_it_without_throwing()
    {
        var w = new Wiring();
        w.Payees.SetupSequence(x => x.GetForVendorAsync(4, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Row())
            .ReturnsAsync(Row(status: "ERROR", error: "payOS từ chối khoá"));
        w.Payees.Setup(x => x.GetCredentialsAsync(9, It.IsAny<CancellationToken>())).ReturnsAsync(new PayeeCredentials(9, "c", "a", "k"));
        w.Gateway.Setup(x => x.VerifyChannelAsync(It.IsAny<PayeeCredentials>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new PayeeChannelException("payOS từ chối khoá", unauthorized: true));

        var dto = await w.Service.CheckAsync(Household, default);

        w.Payees.Verify(x => x.MarkErrorAsync(9, "payOS từ chối khoá", Now, It.IsAny<CancellationToken>()), Times.Once);
        dto.Status.Should().Be("ERROR");
        dto.LastError.Should().Be("payOS từ chối khoá");
    }

    [Fact]
    public async Task A_check_that_passes_brings_the_account_back_and_a_disabled_one_cannot_be_checked()
    {
        var w = new Wiring();
        w.Payees.Setup(x => x.GetForVendorAsync(4, It.IsAny<CancellationToken>())).ReturnsAsync(Row());
        w.Payees.Setup(x => x.GetCredentialsAsync(9, It.IsAny<CancellationToken>())).ReturnsAsync(new PayeeCredentials(9, "c", "a", "k"));

        await w.Service.CheckAsync(Household, default);

        w.Payees.Verify(x => x.MarkCheckedAsync(9, Channel, Now, It.IsAny<CancellationToken>()), Times.Once);

        w.Payees.Setup(x => x.GetForVendorAsync(4, It.IsAny<CancellationToken>())).ReturnsAsync(Row(status: "DISABLED"));
        var act = () => w.Service.CheckAsync(Household, default);
        await act.Should().ThrowAsync<DomainRuleException>();
    }

    [Fact]
    public async Task A_disconnected_account_is_shown_as_not_connected_and_nothing_secret_ever_leaves()
    {
        var w = new Wiring();
        w.Payees.SetupSequence(x => x.GetForVendorAsync(4, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Row())
            .ReturnsAsync(Row(status: "DISABLED"));

        var dto = await w.Service.DisconnectAsync(Household, default);

        w.Payees.Verify(x => x.DisableAsync(9, 5, Now, It.IsAny<CancellationToken>()), Times.Once);
        dto.Connected.Should().BeFalse();
        dto.AccountNumberMasked.Should().BeNull();
        dto.ClientIdMasked.Should().BeNull();
        typeof(PayoutAccountDto).GetProperties().Select(p => p.Name)
            .Should().NotContain(["ApiKey", "ChecksumKey", "WebhookToken", "ClientId", "AccountNumber"]);
    }

    [Fact]
    public async Task Checking_or_disconnecting_with_nothing_connected_is_not_found()
    {
        var w = new Wiring();
        w.Payees.Setup(x => x.GetForVendorAsync(4, It.IsAny<CancellationToken>())).ReturnsAsync((PayeeAccountRow?)null);

        await ((Func<Task>)(() => w.Service.CheckAsync(Household, default))).Should().ThrowAsync<NotFoundException>();
        await ((Func<Task>)(() => w.Service.DisconnectAsync(Household, default))).Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public void The_three_keys_are_required()
    {
        var validator = new ConnectPayoutAccountRequestValidator();

        validator.Validate(new ConnectPayoutAccountRequest("", "a", "k")).IsValid.Should().BeFalse();
        validator.Validate(new ConnectPayoutAccountRequest("c", " ", "k")).IsValid.Should().BeFalse();
        validator.Validate(new ConnectPayoutAccountRequest("c", "a", "")).IsValid.Should().BeFalse();
        validator.Validate(new ConnectPayoutAccountRequest("c", "a", "k")).IsValid.Should().BeTrue();
    }

    // ---------------------------------------------------------------------- treasury receipts

    private static readonly PenaltyReceiptRow Waiting =
        new(31, 8, "SUBMITTED", "/api/uploads/evidence/5/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.jpg", "KB-1", new DateOnly(2026, 11, 2), null, Now, null, null);

    private static PenaltyTreasuryRow Penalty(string status = "UNPAID", PenaltyReceiptRow? latest = null) =>
        new(8, 500_000m, "QĐXP-2026/0001", "Cản trở lối đi bộ", "NVL-01", "Phường Hải Châu", status, Now.AddDays(-2), latest);

    private static SubmitPenaltyReceiptRequest ReceiptRequest(string? url = null, DateOnly? paidOn = null) =>
        new(url ?? "/api/uploads/evidence/5/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.jpg", "KB-1", paidOn ?? new DateOnly(2026, 11, 2), null);

    private static IVendorContext VendorContext()
    {
        var context = new Mock<IVendorContext>();
        context.Setup(x => x.RequireVendorIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(4);
        return context.Object;
    }

    private static ICurrentUser User(long id)
    {
        var user = new Mock<ICurrentUser>();
        user.SetupGet(x => x.UserId).Returns(id);
        return user.Object;
    }

    private static SubmitPenaltyReceiptCommandHandler SubmitHandler(Mock<IPenaltyReceiptRepository> receipts) =>
        new(VendorContext(), User(5), receipts.Object, new FixedClock(Now));

    [Fact]
    public async Task A_household_sees_how_to_pay_the_Treasury_and_whether_it_can_send_a_receipt()
    {
        var receipts = new Mock<IPenaltyReceiptRepository>();
        receipts.Setup(x => x.GetTreasuryAsync(4, 8, It.IsAny<CancellationToken>())).ReturnsAsync(Penalty());

        var dto = await new GetPenaltyTreasuryQueryHandler(VendorContext(), receipts.Object)
            .Handle(new GetPenaltyTreasuryQuery(8), default);

        dto.CanSubmit.Should().BeTrue();
        dto.WardName.Should().Be("Phường Hải Châu");
        dto.Instructions.Should().HaveCount(4);
        dto.Instructions[0].Should().Contain("Kho bạc Nhà nước").And.Contain("QĐXP-2026/0001");
        dto.Instructions.Should().Contain(line => line.Contains("không thu hộ"));
    }

    [Theory]
    [InlineData("UNPAID", "SUBMITTED", false)]  // one receipt is already waiting
    [InlineData("UNPAID", "REJECTED", true)]    // refused: it may send another
    [InlineData("PAID", "ACCEPTED", false)]
    [InlineData("WAIVED", null, false)]
    public async Task A_receipt_can_be_sent_only_for_an_unpaid_fine_with_none_waiting(string penaltyStatus, string? receiptStatus, bool canSubmit)
    {
        var receipts = new Mock<IPenaltyReceiptRepository>();
        var latest = receiptStatus is null ? null : Waiting with { Status = receiptStatus };
        receipts.Setup(x => x.GetTreasuryAsync(4, 8, It.IsAny<CancellationToken>())).ReturnsAsync(Penalty(penaltyStatus, latest));

        var dto = await new GetPenaltyTreasuryQueryHandler(VendorContext(), receipts.Object).Handle(new GetPenaltyTreasuryQuery(8), default);

        dto.CanSubmit.Should().Be(canSubmit);
    }

    [Fact]
    public async Task A_fine_that_is_not_the_callers_is_not_found()
    {
        var receipts = new Mock<IPenaltyReceiptRepository>();
        receipts.Setup(x => x.GetTreasuryAsync(4, 99, It.IsAny<CancellationToken>())).ReturnsAsync((PenaltyTreasuryRow?)null);

        var act = () => new GetPenaltyTreasuryQueryHandler(VendorContext(), receipts.Object).Handle(new GetPenaltyTreasuryQuery(99), default);

        await act.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task A_receipt_is_submitted_with_the_households_own_file()
    {
        var receipts = new Mock<IPenaltyReceiptRepository>();
        receipts.Setup(x => x.GetTreasuryAsync(4, 8, It.IsAny<CancellationToken>())).ReturnsAsync(Penalty());
        receipts.Setup(x => x.SubmitAsync(4, 5, 8, It.IsAny<SubmitPenaltyReceipt>(), Now, It.IsAny<CancellationToken>())).ReturnsAsync(Waiting);

        var dto = await SubmitHandler(receipts).Handle(new SubmitPenaltyReceiptCommand(8, ReceiptRequest()), default);

        dto.Status.Should().Be("SUBMITTED");
        receipts.Verify(x => x.SubmitAsync(4, 5, 8, It.Is<SubmitPenaltyReceipt>(r => r.Reference == "KB-1"), Now, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("/api/uploads/evidence/6/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.jpg")] // someone else's file
    [InlineData("https://example.com/receipt.jpg")]
    [InlineData("/api/uploads/evidence/5/not-a-valid-name.jpg")]
    public async Task Only_a_file_the_household_uploaded_itself_counts_as_a_receipt(string url)
    {
        var receipts = new Mock<IPenaltyReceiptRepository>(MockBehavior.Strict);

        var act = () => SubmitHandler(receipts).Handle(new SubmitPenaltyReceiptCommand(8, ReceiptRequest(url)), default);

        await act.Should().ThrowAsync<DomainRuleException>().WithMessage(PenaltyReceiptMessages.ReceiptNotYours);
    }

    [Theory]
    [InlineData(2026, 11, 3)]  // tomorrow
    [InlineData(2026, 10, 30)] // before the fine was issued (31 Oct)
    public async Task The_payment_date_lies_between_the_fine_and_today(int y, int m, int d)
    {
        var receipts = new Mock<IPenaltyReceiptRepository>();
        receipts.Setup(x => x.GetTreasuryAsync(4, 8, It.IsAny<CancellationToken>())).ReturnsAsync(Penalty());

        var act = () => SubmitHandler(receipts).Handle(new SubmitPenaltyReceiptCommand(8, ReceiptRequest(paidOn: new DateOnly(y, m, d))), default);

        await act.Should().ThrowAsync<DomainRuleException>().WithMessage(PenaltyReceiptMessages.PaidOnRange);
    }

    [Fact]
    public async Task A_fine_that_is_no_longer_unpaid_takes_no_receipt()
    {
        var receipts = new Mock<IPenaltyReceiptRepository>();
        receipts.Setup(x => x.GetTreasuryAsync(4, 8, It.IsAny<CancellationToken>())).ReturnsAsync(Penalty("PAID"));

        var act = () => SubmitHandler(receipts).Handle(new SubmitPenaltyReceiptCommand(8, ReceiptRequest()), default);

        await act.Should().ThrowAsync<DomainRuleException>().WithMessage(PenaltyReceiptMessages.NotPayable);
    }

    // ----------------------------------------------------------------------------- the ward

    private static IWardActorContext Officer(int wardId = 10)
    {
        var context = new Mock<IWardActorContext>();
        context.Setup(x => x.RequireAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new WardActor(3, wardId, "Cán bộ"));
        return context.Object;
    }

    private static PenaltyReceiptReview Review(string status = "SUBMITTED", string penaltyStatus = "UNPAID") =>
        new(31, 8, status, "KB-1", penaltyStatus, 500_000m, 5);

    [Fact]
    public async Task Accepting_a_receipt_records_the_fine_as_paid_to_the_Treasury_then_closes_the_receipt()
    {
        var receipts = new Mock<IPenaltyReceiptRepository>();
        receipts.Setup(x => x.GetForReviewAsync(10, 31, It.IsAny<CancellationToken>())).ReturnsAsync(Review());
        var finance = new Mock<IFinanceRepository>();
        var order = new List<string>();
        finance.Setup(x => x.RecordOfflinePaymentAsync(10, 3, null, 8, "TREASURY", "KB-1", "Đúng biên lai", It.IsAny<CancellationToken>()))
            .Callback(() => order.Add("record")).ReturnsAsync(1);
        receipts.Setup(x => x.DecideAsync(10, 3, 31, true, "Đúng biên lai", Now, It.IsAny<CancellationToken>()))
            .Callback(() => order.Add("decide")).ReturnsAsync(Waiting with { Status = "ACCEPTED" });

        var result = await new AcceptPenaltyReceiptCommandHandler(Officer(), receipts.Object, finance.Object, new FixedClock(Now))
            .Handle(new AcceptPenaltyReceiptCommand(31, " Đúng biên lai "), default);

        result.Message.Should().Contain("Kho bạc");
        order.Should().Equal("record", "decide");
    }

    [Fact]
    public async Task Accepting_again_after_a_partial_failure_finishes_the_receipt_without_recording_the_fine_twice()
    {
        var receipts = new Mock<IPenaltyReceiptRepository>();
        receipts.Setup(x => x.GetForReviewAsync(10, 31, It.IsAny<CancellationToken>())).ReturnsAsync(Review(penaltyStatus: "PAID"));
        var finance = new Mock<IFinanceRepository>(MockBehavior.Strict);

        await new AcceptPenaltyReceiptCommandHandler(Officer(), receipts.Object, finance.Object, new FixedClock(Now))
            .Handle(new AcceptPenaltyReceiptCommand(31, null), default);

        receipts.Verify(x => x.DecideAsync(10, 3, 31, true, null, Now, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("ACCEPTED", "PAID")]
    [InlineData("REJECTED", "UNPAID")]
    public async Task A_receipt_already_decided_is_not_decided_again(string receiptStatus, string penaltyStatus)
    {
        var receipts = new Mock<IPenaltyReceiptRepository>();
        receipts.Setup(x => x.GetForReviewAsync(10, 31, It.IsAny<CancellationToken>())).ReturnsAsync(Review(receiptStatus, penaltyStatus));

        var act = () => new AcceptPenaltyReceiptCommandHandler(Officer(), receipts.Object, Mock.Of<IFinanceRepository>(), new FixedClock(Now))
            .Handle(new AcceptPenaltyReceiptCommand(31, null), default);

        await act.Should().ThrowAsync<DomainRuleException>().WithMessage(PenaltyReceiptMessages.ReceiptNotWaiting);
    }

    [Fact]
    public async Task A_receipt_of_another_ward_is_not_found_and_a_waived_fine_cannot_be_accepted()
    {
        var receipts = new Mock<IPenaltyReceiptRepository>();
        receipts.Setup(x => x.GetForReviewAsync(11, 31, It.IsAny<CancellationToken>())).ReturnsAsync((PenaltyReceiptReview?)null);
        receipts.Setup(x => x.GetForReviewAsync(10, 31, It.IsAny<CancellationToken>())).ReturnsAsync(Review(penaltyStatus: "WAIVED"));
        var finance = Mock.Of<IFinanceRepository>();

        await ((Func<Task>)(() => new AcceptPenaltyReceiptCommandHandler(Officer(11), receipts.Object, finance, new FixedClock(Now))
            .Handle(new AcceptPenaltyReceiptCommand(31, null), default))).Should().ThrowAsync<NotFoundException>();
        await ((Func<Task>)(() => new AcceptPenaltyReceiptCommandHandler(Officer(10), receipts.Object, finance, new FixedClock(Now))
            .Handle(new AcceptPenaltyReceiptCommand(31, null), default))).Should().ThrowAsync<DomainRuleException>();
    }

    [Fact]
    public async Task Rejecting_needs_a_reason_and_tells_the_household_through_the_repository()
    {
        var receipts = new Mock<IPenaltyReceiptRepository>();
        receipts.Setup(x => x.GetForReviewAsync(10, 31, It.IsAny<CancellationToken>())).ReturnsAsync(Review());
        receipts.Setup(x => x.DecideAsync(10, 3, 31, false, "Ảnh mờ", Now, It.IsAny<CancellationToken>())).ReturnsAsync(Waiting with { Status = "REJECTED" });

        await new RejectPenaltyReceiptCommandHandler(Officer(), receipts.Object, new FixedClock(Now))
            .Handle(new RejectPenaltyReceiptCommand(31, " Ảnh mờ "), default);

        receipts.Verify(x => x.DecideAsync(10, 3, 31, false, "Ảnh mờ", Now, It.IsAny<CancellationToken>()), Times.Once);
        new RejectPenaltyReceiptCommandValidator().Validate(new RejectPenaltyReceiptCommand(31, " ")).IsValid.Should().BeFalse();
        new AnnulPaidPenaltyCommandValidator().Validate(new AnnulPaidPenaltyCommand(8, "")).IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Annulling_a_paid_fine_hands_the_refund_to_the_Treasury()
    {
        var receipts = new Mock<IPenaltyReceiptRepository>();
        receipts.Setup(x => x.AnnulPaidPenaltyAsync(10, 3, 8, "Huỷ sau khiếu nại", Now, It.IsAny<CancellationToken>())).ReturnsAsync("Kho bạc hoàn trả");

        var result = await new AnnulPaidPenaltyCommandHandler(Officer(), receipts.Object, new FixedClock(Now))
            .Handle(new AnnulPaidPenaltyCommand(8, " Huỷ sau khiếu nại "), default);

        result.Message.Should().Be("Kho bạc hoàn trả");
    }

    [Fact]
    public void Officers_cannot_record_a_Treasury_payment_by_hand_and_the_list_filter_is_checked()
    {
        OfflinePaymentMethods.IsValid(OfflinePaymentMethods.Treasury).Should().BeFalse();
        OfflinePaymentMethods.IsValid("CASH").Should().BeTrue();
        new ListPenaltyReceiptsQueryValidator().Validate(new ListPenaltyReceiptsQuery("SUBMITTED")).IsValid.Should().BeTrue();
        new ListPenaltyReceiptsQueryValidator().Validate(new ListPenaltyReceiptsQuery("WHATEVER")).IsValid.Should().BeFalse();
        new ListPenaltyReceiptsQueryValidator().Validate(new ListPenaltyReceiptsQuery(null)).IsValid.Should().BeTrue();
    }

    private sealed class FixedClock(DateTime now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(now, TimeSpan.Zero);
    }
}
