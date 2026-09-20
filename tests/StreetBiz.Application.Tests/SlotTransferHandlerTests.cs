using FluentAssertions;
using Moq;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.Features.SlotTransfers.AcceptTransfer;
using StreetBiz.Application.Features.SlotTransfers.DeclineTransfer;
using StreetBiz.Application.Features.SlotTransfers.RequestTransfer;

namespace StreetBiz.Application.Tests;

public sealed class SlotTransferHandlerTests
{
    private const long VendorId = 80;
    private const long OtherVendorId = 81;
    private const long ReceiverVendorId = 82;
    private const long ContractId = 600;
    private const long TransferId = 900;
    private const string ReceiverPhone = "0905123456";
    private static readonly DateOnly ContractStart = new(2026, 9, 1);
    private static readonly DateOnly ContractEnd = new(2026, 12, 1);

    private readonly Mock<IVendorContext> vendorContext = new();
    private readonly Mock<IVendorRepository> vendors = new();
    private readonly Mock<IBusinessRegistrationRepository> registrations = new();
    private readonly Mock<IRentalContractRepository> contracts = new();
    private readonly Mock<ISlotTransferRequestRepository> transfers = new();
    private readonly Mock<IDateTimeProvider> clock = new();

    public SlotTransferHandlerTests()
    {
        vendorContext.Setup(v => v.RequireVendorIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(VendorId);
        clock.Setup(c => c.UtcNow).Returns(DateTime.UtcNow);
    }

    private static RentalContractRow ActiveContract(long vendorId) => new(
        ContractId, 700, 1, "HQ-DH-01", "Zone", vendorId,
        DateOnly.FromDateTime(DateTime.UtcNow), DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30)),
        ContractStatuses.Active, null, null, null, DateTime.UtcNow, null);

    private RequestTransferCommandHandler RequestHandler() => new(
        vendorContext.Object, vendors.Object, registrations.Object, contracts.Object, transfers.Object);

    private void HappyPathSetup()
    {
        contracts.Setup(c => c.GetByIdAsync(ContractId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ActiveContract(VendorId));
        vendors.Setup(v => v.GetVendorIdByPhoneAsync(ReceiverPhone, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ReceiverVendorId);
        registrations.Setup(r => r.HasApprovedRegistrationAsync(ReceiverVendorId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        contracts.Setup(c => c.HasOutstandingDebtAsync(ContractId, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        transfers.Setup(t => t.HasOpenForContractAsync(ContractId, It.IsAny<CancellationToken>())).ReturnsAsync(false);
    }

    [Fact]
    public async Task Transferring_to_your_own_phone_number_is_rejected_as_a_validation_error()
    {
        contracts.Setup(c => c.GetByIdAsync(ContractId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ActiveContract(VendorId));
        vendors.Setup(v => v.GetVendorIdByPhoneAsync(ReceiverPhone, It.IsAny<CancellationToken>()))
            .ReturnsAsync(VendorId);

        var command = new RequestTransferCommand(ContractId, ReceiverPhone);

        await FluentActions.Awaiting(() => RequestHandler().Handle(command, CancellationToken.None))
            .Should().ThrowAsync<ValidationAppException>();

        transfers.Verify(t => t.CreateAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_receiver_without_an_approved_registration_fails_BR26()
    {
        contracts.Setup(c => c.GetByIdAsync(ContractId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ActiveContract(VendorId));
        vendors.Setup(v => v.GetVendorIdByPhoneAsync(ReceiverPhone, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ReceiverVendorId);
        registrations.Setup(r => r.HasApprovedRegistrationAsync(ReceiverVendorId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var command = new RequestTransferCommand(ContractId, ReceiverPhone);

        await FluentActions.Awaiting(() => RequestHandler().Handle(command, CancellationToken.None))
            .Should().ThrowAsync<DomainRuleException>().WithMessage(SideMessages.ReceiverNotApproved);
    }

    [Fact]
    public async Task Outstanding_debt_on_the_contract_fails_BR27()
    {
        contracts.Setup(c => c.GetByIdAsync(ContractId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ActiveContract(VendorId));
        vendors.Setup(v => v.GetVendorIdByPhoneAsync(ReceiverPhone, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ReceiverVendorId);
        registrations.Setup(r => r.HasApprovedRegistrationAsync(ReceiverVendorId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        contracts.Setup(c => c.HasOutstandingDebtAsync(ContractId, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var command = new RequestTransferCommand(ContractId, ReceiverPhone);

        await FluentActions.Awaiting(() => RequestHandler().Handle(command, CancellationToken.None))
            .Should().ThrowAsync<DomainRuleException>().WithMessage(SideMessages.TransferBlockedByDebt);
    }

    [Fact]
    public async Task A_contract_that_belongs_to_another_vendor_is_forbidden()
    {
        contracts.Setup(c => c.GetByIdAsync(ContractId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ActiveContract(OtherVendorId));

        var command = new RequestTransferCommand(ContractId, ReceiverPhone);

        await FluentActions.Awaiting(() => RequestHandler().Handle(command, CancellationToken.None))
            .Should().ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task An_unknown_phone_number_is_not_found()
    {
        contracts.Setup(c => c.GetByIdAsync(ContractId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ActiveContract(VendorId));
        vendors.Setup(v => v.GetVendorIdByPhoneAsync(ReceiverPhone, It.IsAny<CancellationToken>()))
            .ReturnsAsync((long?)null);

        var command = new RequestTransferCommand(ContractId, ReceiverPhone);

        await FluentActions.Awaiting(() => RequestHandler().Handle(command, CancellationToken.None))
            .Should().ThrowAsync<NotFoundException>().WithMessage(SideMessages.ReceiverNotFound);
    }

    [Fact]
    public async Task A_second_open_transfer_for_the_same_contract_is_a_conflict()
    {
        HappyPathSetup();
        transfers.Setup(t => t.HasOpenForContractAsync(ContractId, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var command = new RequestTransferCommand(ContractId, ReceiverPhone);

        await FluentActions.Awaiting(() => RequestHandler().Handle(command, CancellationToken.None))
            .Should().ThrowAsync<ConflictException>().WithMessage(SideMessages.TransferAlreadyOpen);
    }

    [Fact]
    public async Task A_valid_request_is_created_and_returned()
    {
        HappyPathSetup();
        transfers.Setup(t => t.CreateAsync(ContractId, VendorId, ReceiverVendorId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(TransferId);
        transfers.Setup(t => t.GetByIdAsync(TransferId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SlotTransferRequestRow(TransferId, ContractId, VendorId, ReceiverVendorId,
                TransferStatuses.Pending, DateTime.UtcNow, null, null, null, "HQ-DH-01", "Zone", ContractStart, ContractEnd));

        var command = new RequestTransferCommand(ContractId, ReceiverPhone);
        var result = await RequestHandler().Handle(command, CancellationToken.None);

        result.TransferId.Should().Be(TransferId);
        result.TransferStatus.Should().Be(TransferStatuses.Pending);
        result.SlotCode.Should().Be("HQ-DH-01");
        result.ZoneName.Should().Be("Zone");
        result.ContractStartDate.Should().Be(ContractStart);
        result.ContractEndDate.Should().Be(ContractEnd);
    }

    [Fact]
    public async Task Only_the_receiving_vendor_may_accept()
    {
        vendorContext.Setup(v => v.RequireVendorIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(OtherVendorId);
        transfers.Setup(t => t.GetByIdAsync(TransferId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SlotTransferRequestRow(TransferId, ContractId, VendorId, ReceiverVendorId,
                TransferStatuses.Pending, DateTime.UtcNow, null, null, null, "HQ-DH-01", "Zone", ContractStart, ContractEnd));

        var handler = new AcceptTransferCommandHandler(vendorContext.Object, transfers.Object, clock.Object);

        await FluentActions.Awaiting(() => handler.Handle(new AcceptTransferCommand(TransferId), CancellationToken.None))
            .Should().ThrowAsync<ForbiddenException>().WithMessage(SideMessages.NotTheReceivingVendor);

        transfers.Verify(t => t.SetStatusAsync(It.IsAny<long>(), It.IsAny<string>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Accepting_an_already_decided_transfer_is_a_conflict()
    {
        vendorContext.Setup(v => v.RequireVendorIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(ReceiverVendorId);
        transfers.Setup(t => t.GetByIdAsync(TransferId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SlotTransferRequestRow(TransferId, ContractId, VendorId, ReceiverVendorId,
                TransferStatuses.AcceptedByReceiver, DateTime.UtcNow, DateTime.UtcNow, null, null, "HQ-DH-01", "Zone", ContractStart, ContractEnd));

        var handler = new AcceptTransferCommandHandler(vendorContext.Object, transfers.Object, clock.Object);

        await FluentActions.Awaiting(() => handler.Handle(new AcceptTransferCommand(TransferId), CancellationToken.None))
            .Should().ThrowAsync<ConflictException>().WithMessage(SideMessages.TransferNotPending);
    }

    [Fact]
    public async Task The_receiving_vendor_can_accept_a_pending_transfer()
    {
        vendorContext.Setup(v => v.RequireVendorIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(ReceiverVendorId);
        transfers.Setup(t => t.GetByIdAsync(TransferId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SlotTransferRequestRow(TransferId, ContractId, VendorId, ReceiverVendorId,
                TransferStatuses.Pending, DateTime.UtcNow, null, null, null, "HQ-DH-01", "Zone", ContractStart, ContractEnd));

        var handler = new AcceptTransferCommandHandler(vendorContext.Object, transfers.Object, clock.Object);
        await handler.Handle(new AcceptTransferCommand(TransferId), CancellationToken.None);

        transfers.Verify(t => t.SetStatusAsync(TransferId, TransferStatuses.AcceptedByReceiver, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task The_receiving_vendor_can_decline_a_pending_transfer()
    {
        vendorContext.Setup(v => v.RequireVendorIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(ReceiverVendorId);
        transfers.Setup(t => t.GetByIdAsync(TransferId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SlotTransferRequestRow(TransferId, ContractId, VendorId, ReceiverVendorId,
                TransferStatuses.Pending, DateTime.UtcNow, null, null, null, "HQ-DH-01", "Zone", ContractStart, ContractEnd));

        var handler = new DeclineTransferCommandHandler(vendorContext.Object, transfers.Object);
        await handler.Handle(new DeclineTransferCommand(TransferId), CancellationToken.None);

        transfers.Verify(t => t.SetStatusAsync(TransferId, TransferStatuses.Rejected, null, It.IsAny<CancellationToken>()), Times.Once);
    }
}
