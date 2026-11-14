using FluentAssertions;
using Moq;
using StreetBiz.Application.Features.WardFinance;
using StreetBiz.Application.Features.WardSlots;

namespace StreetBiz.Application.Tests;

/// <summary>
/// WARD-14: the money corrections an officer may make by hand. Every one needs a reason, the
/// officer is always resolved on the server (never taken from the request), and the ward service
/// does the real work.
/// </summary>
public sealed class WardFinanceUseCaseTests
{
    private static readonly WardActor Officer = new(2, 10, "Nguyễn Thị Hồng Vân");
    private static readonly CancellationToken Ct = CancellationToken.None;

    private readonly Mock<IWardActorContext> actor = new();
    private readonly Mock<IWardFinanceService> service = new();

    public WardFinanceUseCaseTests()
    {
        actor.Setup(a => a.RequireAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Officer);
    }

    private static string Long(int n) => new('x', n);

    // ---- AdjustFeeItemCommand -----------------------------------------------------------

    [Fact]
    public void Adjusting_an_instalment_needs_a_reason_and_something_to_change()
    {
        var validator = new AdjustFeeItemCommandValidator();

        validator.Validate(new AdjustFeeItemCommand(1, new(500_000m, null, "Giảm theo biên bản"))).IsValid.Should().BeTrue();
        validator.Validate(new AdjustFeeItemCommand(1, new(null, new DateOnly(2026, 12, 1), "Gia hạn nộp"))).IsValid.Should().BeTrue();

        validator.Validate(new AdjustFeeItemCommand(1, new(500_000m, null, ""))).IsValid.Should().BeFalse();
        validator.Validate(new AdjustFeeItemCommand(1, new(null, null, "Không đổi gì"))).Errors
            .Should().Contain(e => e.ErrorMessage == "Nhập số tiền mới hoặc hạn nộp mới.");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1_000_000_001)]
    public void The_new_amount_must_be_above_zero_and_within_a_billion(long amount)
    {
        var result = new AdjustFeeItemCommandValidator()
            .Validate(new AdjustFeeItemCommand(1, new(amount, null, "Lý do")));

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Not_collecting_an_instalment_is_a_waiver_not_a_zero_amount()
    {
        var result = new AdjustFeeItemCommandValidator()
            .Validate(new AdjustFeeItemCommand(1, new(0m, null, "Lý do")));

        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("thao tác miễn"));
    }

    [Fact]
    public void The_reason_is_limited_to_500_characters()
    {
        var validator = new AdjustFeeItemCommandValidator();

        validator.Validate(new AdjustFeeItemCommand(1, new(1m, null, Long(500)))).IsValid.Should().BeTrue();
        validator.Validate(new AdjustFeeItemCommand(1, new(1m, null, Long(501)))).IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Adjusting_is_done_as_the_officer_resolved_from_the_session()
    {
        var request = new AdjustFeeItemRequest(400_000m, null, "Giảm theo biên bản");
        var done = new WardFinanceActionResultDto("Đã điều chỉnh");
        service.Setup(s => s.AdjustFeeItemAsync(Officer, 17, request, Ct)).ReturnsAsync(done);

        var result = await new AdjustFeeItemCommandHandler(actor.Object, service.Object)
            .Handle(new AdjustFeeItemCommand(17, request), Ct);

        result.Should().BeSameAs(done);
    }

    // ---- waivers ------------------------------------------------------------------------

    [Fact]
    public void A_waiver_of_a_fee_or_a_penalty_needs_a_reason()
    {
        new WaiveFeeItemCommandValidator().Validate(new WaiveFeeItemCommand(1, "Hoàn cảnh khó khăn")).IsValid.Should().BeTrue();
        new WaiveFeeItemCommandValidator().Validate(new WaiveFeeItemCommand(1, " ")).IsValid.Should().BeFalse();
        new WaiveFeeItemCommandValidator().Validate(new WaiveFeeItemCommand(1, Long(501))).IsValid.Should().BeFalse();

        new WaivePenaltyCommandValidator().Validate(new WaivePenaltyCommand(1, "Phạt nhầm hộ")).IsValid.Should().BeTrue();
        new WaivePenaltyCommandValidator().Validate(new WaivePenaltyCommand(1, "")).IsValid.Should().BeFalse();
        new WaivePenaltyCommandValidator().Validate(new WaivePenaltyCommand(1, Long(501))).IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Waiving_goes_to_the_service_with_the_officer_and_the_reason()
    {
        service.Setup(s => s.WaiveFeeItemAsync(Officer, 5, "Miễn", Ct)).ReturnsAsync(new WardFinanceActionResultDto("fee"));
        service.Setup(s => s.WaivePenaltyAsync(Officer, 6, "Huỷ", Ct)).ReturnsAsync(new WardFinanceActionResultDto("penalty"));

        var fee = await new WaiveFeeItemCommandHandler(actor.Object, service.Object).Handle(new WaiveFeeItemCommand(5, "Miễn"), Ct);
        var penalty = await new WaivePenaltyCommandHandler(actor.Object, service.Object).Handle(new WaivePenaltyCommand(6, "Huỷ"), Ct);

        fee.Message.Should().Be("fee");
        penalty.Message.Should().Be("penalty");
    }

    // ---- RecordOfflinePaymentCommand ----------------------------------------------------

    private static RecordOfflinePaymentCommand Offline(
        string target = "FEE", long id = 1, string method = "CASH", string? reference = "BL-001", string? note = null) =>
        new(new RecordOfflinePaymentRequest(target, id, method, reference, note));

    [Fact]
    public void An_offline_payment_is_valid_for_a_fee_or_a_penalty_by_cash_or_transfer()
    {
        var validator = new RecordOfflinePaymentCommandValidator();

        validator.Validate(Offline()).IsValid.Should().BeTrue();
        validator.Validate(Offline("PENALTY", method: "BANK_TRANSFER", reference: "FT2610270001")).IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("INVOICE", "CASH", "BL-1", 1)]
    [InlineData("fee", "CASH", "BL-1", 1)]
    [InlineData("FEE", "MOMO", "BL-1", 1)]
    [InlineData("FEE", "cash", "BL-1", 1)]
    [InlineData("FEE", "CASH", "", 1)]
    [InlineData("FEE", "CASH", "BL-1", 0)]
    public void An_offline_payment_with_a_wrong_target_method_receipt_or_id_is_refused(
        string target, string method, string reference, long id)
    {
        new RecordOfflinePaymentCommandValidator()
            .Validate(Offline(target, id, method, reference)).IsValid.Should().BeFalse();
    }

    [Fact]
    public void The_receipt_reference_is_limited_to_100_characters_and_the_note_to_500()
    {
        var validator = new RecordOfflinePaymentCommandValidator();

        validator.Validate(Offline(reference: Long(100), note: Long(500))).IsValid.Should().BeTrue();
        validator.Validate(Offline(reference: Long(101))).IsValid.Should().BeFalse();
        validator.Validate(Offline(note: Long(501))).IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Recording_an_offline_payment_goes_to_the_service_as_the_officer()
    {
        var command = Offline("PENALTY", 9, "CASH", "BL-9", "Thu tại quầy");
        service.Setup(s => s.RecordOfflinePaymentAsync(Officer, command.Request, Ct))
            .ReturnsAsync(new WardFinanceActionResultDto("ok"));

        var result = await new RecordOfflinePaymentCommandHandler(actor.Object, service.Object).Handle(command, Ct);

        result.Message.Should().Be("ok");
    }

    // ---- SetSlotPriceCommand ------------------------------------------------------------

    [Fact]
    public void A_slot_price_override_needs_a_reason_and_a_sensible_amount_or_null_to_reset()
    {
        var validator = new SetSlotPriceCommandValidator();

        validator.Validate(new SetSlotPriceCommand(1, new(1_200_000m, "Vị trí đắc địa"))).IsValid.Should().BeTrue();
        validator.Validate(new SetSlotPriceCommand(1, new(null, "Quay về giá khu vực"))).IsValid.Should().BeTrue();

        validator.Validate(new SetSlotPriceCommand(1, new(1_200_000m, ""))).IsValid.Should().BeFalse();
        validator.Validate(new SetSlotPriceCommand(1, new(0m, "Lý do"))).IsValid.Should().BeFalse();
        validator.Validate(new SetSlotPriceCommand(1, new(-5m, "Lý do"))).IsValid.Should().BeFalse();
        validator.Validate(new SetSlotPriceCommand(1, new(1_000_000_001m, "Lý do"))).IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Setting_a_slot_price_goes_to_the_service_as_the_officer()
    {
        var request = new SetSlotPriceRequest(900_000m, "Giá riêng");
        service.Setup(s => s.SetSlotPriceAsync(Officer, 31, request, Ct)).ReturnsAsync(new WardFinanceActionResultDto("ok"));

        var result = await new SetSlotPriceCommandHandler(actor.Object, service.Object).Handle(new SetSlotPriceCommand(31, request), Ct);

        result.Message.Should().Be("ok");
    }

    // ---- RefundDepositCommand -----------------------------------------------------------

    [Fact]
    public void A_deposit_refund_needs_a_valid_method_and_a_non_negative_amount()
    {
        var validator = new RefundDepositCommandValidator();

        validator.Validate(new RefundDepositCommand(1, new(1_000_000m, "CASH", null, null))).IsValid.Should().BeTrue();
        validator.Validate(new RefundDepositCommand(1, new(0m, "BANK_TRANSFER", "FT1", "Trừ hết vào phí nợ"))).IsValid.Should().BeTrue();

        validator.Validate(new RefundDepositCommand(1, new(-1m, "CASH", null, null))).IsValid.Should().BeFalse();
        validator.Validate(new RefundDepositCommand(1, new(1m, "CHEQUE", null, null))).IsValid.Should().BeFalse();
        validator.Validate(new RefundDepositCommand(1, new(1m, "CASH", Long(101), null))).IsValid.Should().BeFalse();
        validator.Validate(new RefundDepositCommand(1, new(1m, "CASH", null, Long(501)))).IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Refunding_a_deposit_goes_to_the_service_as_the_officer()
    {
        var request = new RefundDepositRequest(500_000m, "CASH", "BL-7", "Trừ tiền phạt");
        service.Setup(s => s.RefundDepositAsync(Officer, 3, request, Ct)).ReturnsAsync(new WardFinanceActionResultDto("ok"));

        var result = await new RefundDepositCommandHandler(actor.Object, service.Object).Handle(new RefundDepositCommand(3, request), Ct);

        result.Message.Should().Be("ok");
    }

    // ---- queries ------------------------------------------------------------------------

    [Fact]
    public async Task The_list_and_the_detail_are_read_for_the_resolved_officers_ward()
    {
        IReadOnlyList<WardFinanceContractListItemDto> list = [];
        var detail = new WardFinanceContractDetailDto(
            4, 1, "NVL-01", "Nguyễn Văn Linh", "STANDARD", "Phạm Thị Lan", "0905000101", "ACTIVE",
            new DateOnly(2026, 9, 11), new DateOnly(2026, 12, 10), 3_000_000m, 1_000_000m,
            [], [], 0m, null, false, null, null, 900_000m);
        service.Setup(s => s.ListContractsAsync(Officer, Ct)).ReturnsAsync(list);
        service.Setup(s => s.GetContractAsync(Officer, 4, Ct)).ReturnsAsync(detail);

        var listed = await new ListWardFinanceContractsQueryHandler(actor.Object, service.Object)
            .Handle(new ListWardFinanceContractsQuery(), Ct);
        var fetched = await new GetWardFinanceContractQueryHandler(actor.Object, service.Object)
            .Handle(new GetWardFinanceContractQuery(4), Ct);

        listed.Should().BeSameAs(list);
        fetched.Should().BeSameAs(detail);
    }

    [Fact]
    public async Task An_account_that_is_not_a_ward_officer_never_reaches_the_service()
    {
        actor.Setup(a => a.RequireAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new StreetBiz.Application.Common.Exceptions.ForbiddenException("Ward officers only."));

        var act = () => new WaiveFeeItemCommandHandler(actor.Object, service.Object).Handle(new WaiveFeeItemCommand(1, "x"), Ct);

        await act.Should().ThrowAsync<StreetBiz.Application.Common.Exceptions.ForbiddenException>();
        service.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("CASH", true)]
    [InlineData("BANK_TRANSFER", true)]
    [InlineData("MOMO", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_cash_and_bank_transfer_are_offline_methods(string? method, bool valid)
    {
        OfflinePaymentMethods.IsValid(method).Should().Be(valid);
    }
}
