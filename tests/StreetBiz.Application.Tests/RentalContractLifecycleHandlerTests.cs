using FluentAssertions;
using Moq;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.Features.RentalContracts.CancelContract;
using StreetBiz.Application.Features.RentalContracts.RequestRenewal;
using StreetBiz.Application.Features.RentalContracts.WithdrawRenewal;

namespace StreetBiz.Application.Tests;

public sealed class RentalContractLifecycleHandlerTests
{
    private const long ContractId = 500;
    private const long VendorId = 70;
    private const long OtherVendorId = 71;
    private const long UserId = 7;

    private readonly Mock<IVendorContext> vendorContext = new();
    private readonly Mock<IRentalContractRepository> contracts = new();
    private readonly Mock<IRenewalRequestRepository> renewals = new();
    private readonly Mock<ICurrentUser> currentUser = new();

    public RentalContractLifecycleHandlerTests()
    {
        vendorContext.Setup(v => v.RequireVendorIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(VendorId);
        currentUser.Setup(u => u.UserId).Returns(UserId);
    }

    private static RentalContractRow Contract(string status, long vendorId = VendorId) => new(
        ContractId, 900, 1, "HQ-DH-01", "Khu vuc gan truong dai hoc", vendorId,
        DateOnly.FromDateTime(DateTime.UtcNow), DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30)),
        status, null, null, null, DateTime.UtcNow, null);

    // ---- SIDE-06: renewal ----

    [Fact]
    public async Task Requesting_renewal_on_another_vendors_contract_is_forbidden()
    {
        contracts.Setup(c => c.GetByIdAsync(ContractId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Contract(ContractStatuses.Active, vendorId: OtherVendorId));

        var handler = new RequestRenewalCommandHandler(vendorContext.Object, contracts.Object, renewals.Object);

        await FluentActions.Awaiting(() => handler.Handle(new RequestRenewalCommand(ContractId, 30), CancellationToken.None))
            .Should().ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task Requesting_renewal_on_a_non_active_contract_is_a_domain_rule_violation()
    {
        contracts.Setup(c => c.GetByIdAsync(ContractId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Contract(ContractStatuses.Expired));

        var handler = new RequestRenewalCommandHandler(vendorContext.Object, contracts.Object, renewals.Object);

        await FluentActions.Awaiting(() => handler.Handle(new RequestRenewalCommand(ContractId, 30), CancellationToken.None))
            .Should().ThrowAsync<DomainRuleException>().WithMessage(SideMessages.ContractNotActive);
    }

    [Fact]
    public async Task Requesting_a_second_renewal_while_one_is_open_is_a_conflict()
    {
        contracts.Setup(c => c.GetByIdAsync(ContractId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Contract(ContractStatuses.Active));
        renewals.Setup(r => r.HasOpenAsync(ContractId, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var handler = new RequestRenewalCommandHandler(vendorContext.Object, contracts.Object, renewals.Object);

        await FluentActions.Awaiting(() => handler.Handle(new RequestRenewalCommand(ContractId, 30), CancellationToken.None))
            .Should().ThrowAsync<ConflictException>().WithMessage(SideMessages.RenewalAlreadyOpen);

        renewals.Verify(r => r.CreateAsync(It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Owner_can_request_a_renewal_on_an_active_contract()
    {
        contracts.Setup(c => c.GetByIdAsync(ContractId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Contract(ContractStatuses.Active));
        renewals.Setup(r => r.HasOpenAsync(ContractId, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        renewals.Setup(r => r.CreateAsync(ContractId, 30, It.IsAny<CancellationToken>())).ReturnsAsync(1);
        renewals.Setup(r => r.GetByIdAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RenewalRequestRow(1, ContractId, 30, RenewalStatuses.Pending, null, null, null, DateTime.UtcNow));

        var handler = new RequestRenewalCommandHandler(vendorContext.Object, contracts.Object, renewals.Object);
        var result = await handler.Handle(new RequestRenewalCommand(ContractId, 30), CancellationToken.None);

        result.RenewalStatus.Should().Be(RenewalStatuses.Pending);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(365, true)]
    [InlineData(366, false)]
    public void Requested_term_days_must_stay_within_the_same_1_to_365_bound_as_a_first_application(int days, bool expectedValid)
    {
        var validator = new RequestRenewalCommandValidator();
        var result = validator.Validate(new RequestRenewalCommand(ContractId, days));

        result.IsValid.Should().Be(expectedValid);
    }

    // ---- SIDE-06: withdraw renewal ----

    [Fact]
    public async Task Withdrawing_a_renewal_on_another_vendors_contract_is_forbidden()
    {
        renewals.Setup(r => r.GetByIdAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RenewalRequestRow(1, ContractId, 30, RenewalStatuses.Pending, null, null, null, DateTime.UtcNow));
        contracts.Setup(c => c.GetByIdAsync(ContractId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Contract(ContractStatuses.Active, vendorId: OtherVendorId));

        var handler = new WithdrawRenewalCommandHandler(vendorContext.Object, contracts.Object, renewals.Object);

        await FluentActions.Awaiting(() => handler.Handle(new WithdrawRenewalCommand(1), CancellationToken.None))
            .Should().ThrowAsync<ForbiddenException>();

        renewals.Verify(r => r.WithdrawAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Withdrawing_an_already_decided_renewal_is_a_conflict()
    {
        renewals.Setup(r => r.GetByIdAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RenewalRequestRow(1, ContractId, 30, RenewalStatuses.Pending, null, null, null, DateTime.UtcNow));
        contracts.Setup(c => c.GetByIdAsync(ContractId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Contract(ContractStatuses.Active));
        renewals.Setup(r => r.WithdrawAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var handler = new WithdrawRenewalCommandHandler(vendorContext.Object, contracts.Object, renewals.Object);

        await FluentActions.Awaiting(() => handler.Handle(new WithdrawRenewalCommand(1), CancellationToken.None))
            .Should().ThrowAsync<ConflictException>().WithMessage(SideMessages.RenewalNotWithdrawable);
    }

    [Fact]
    public async Task Owner_can_withdraw_their_own_open_renewal()
    {
        renewals.Setup(r => r.GetByIdAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RenewalRequestRow(1, ContractId, 30, RenewalStatuses.Pending, null, null, null, DateTime.UtcNow));
        contracts.Setup(c => c.GetByIdAsync(ContractId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Contract(ContractStatuses.Active));
        renewals.Setup(r => r.WithdrawAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var handler = new WithdrawRenewalCommandHandler(vendorContext.Object, contracts.Object, renewals.Object);
        await handler.Handle(new WithdrawRenewalCommand(1), CancellationToken.None);

        renewals.Verify(r => r.WithdrawAsync(1, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ---- SIDE-07: cancel/return ----

    [Fact]
    public async Task Cancelling_another_vendors_contract_is_forbidden()
    {
        contracts.Setup(c => c.GetByIdAsync(ContractId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Contract(ContractStatuses.Active, vendorId: OtherVendorId));

        var handler = new CancelContractCommandHandler(vendorContext.Object, currentUser.Object, contracts.Object, renewals.Object);

        await FluentActions.Awaiting(() => handler.Handle(new CancelContractCommand(ContractId, "no longer needed"), CancellationToken.None))
            .Should().ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task Cancelling_with_outstanding_debt_is_a_domain_rule_violation()
    {
        contracts.Setup(c => c.GetByIdAsync(ContractId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Contract(ContractStatuses.Active));
        contracts.Setup(c => c.HasOutstandingDebtAsync(ContractId, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var handler = new CancelContractCommandHandler(vendorContext.Object, currentUser.Object, contracts.Object, renewals.Object);

        await FluentActions.Awaiting(() => handler.Handle(new CancelContractCommand(ContractId, "no longer needed"), CancellationToken.None))
            .Should().ThrowAsync<DomainRuleException>().WithMessage(SideMessages.CannotReturnWithDebt);

        contracts.Verify(c => c.CancelAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        renewals.Verify(r => r.CloseOpenForContractAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Owner_can_cancel_a_debt_free_active_contract_with_the_authenticated_users_id()
    {
        contracts.Setup(c => c.GetByIdAsync(ContractId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Contract(ContractStatuses.Active));
        contracts.Setup(c => c.HasOutstandingDebtAsync(ContractId, It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var handler = new CancelContractCommandHandler(vendorContext.Object, currentUser.Object, contracts.Object, renewals.Object);
        await handler.Handle(new CancelContractCommand(ContractId, "no longer needed"), CancellationToken.None);

        // cancelled_by must be the UserAccounts.user_id, not the vendor_id.
        contracts.Verify(c => c.CancelAsync(ContractId, UserId, "no longer needed", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Cancelling_a_contract_withdraws_any_renewal_request_still_open_on_it()
    {
        contracts.Setup(c => c.GetByIdAsync(ContractId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Contract(ContractStatuses.Active));
        contracts.Setup(c => c.HasOutstandingDebtAsync(ContractId, It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var handler = new CancelContractCommandHandler(vendorContext.Object, currentUser.Object, contracts.Object, renewals.Object);
        await handler.Handle(new CancelContractCommand(ContractId, "no longer needed"), CancellationToken.None);

        renewals.Verify(r => r.CloseOpenForContractAsync(ContractId, It.IsAny<CancellationToken>()), Times.Once);
    }
}
