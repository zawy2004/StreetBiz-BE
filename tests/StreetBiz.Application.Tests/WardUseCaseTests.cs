using FluentAssertions;
using Moq;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.Features.WardSlots;

namespace StreetBiz.Application.Tests;

public sealed class WardUseCaseTests
{
    [Fact]
    public async Task Actor_resolver_uses_the_active_database_account_not_token_role_or_ward_claims()
    {
        var users = new Mock<IUserAccountRepository>();
        users.Setup(x => x.GetByIdAsync(42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AppUser(
                42,
                "0900000042",
                "hash",
                "Ward Officer",
                RoleCodes.WardAuthority,
                7,
                AccountStatuses.Active,
                DateTime.UtcNow));

        var actor = await new WardActorResolver(users.Object)
            .ResolveAsync(42, CancellationToken.None);

        actor.Should().Be(new WardActor(42, 7, "Ward Officer"));
    }

    [Theory]
    [InlineData(RoleCodes.Vendor, AccountStatuses.Active, 7)]
    [InlineData(RoleCodes.WardAuthority, AccountStatuses.Suspended, 7)]
    [InlineData(RoleCodes.WardAuthority, AccountStatuses.Active, null)]
    public async Task Actor_resolver_rejects_accounts_without_current_ward_authority(
        string role,
        string status,
        int? wardId)
    {
        var users = new Mock<IUserAccountRepository>();
        users.Setup(x => x.GetByIdAsync(42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AppUser(
                42,
                "0900000042",
                "hash",
                "User",
                role,
                wardId,
                status,
                DateTime.UtcNow));

        var actor = await new WardActorResolver(users.Object)
            .ResolveAsync(42, CancellationToken.None);

        actor.Should().BeNull();
    }

    [Theory]
    [InlineData(WardCaseKinds.Proposals, "APPROVE", true)]
    [InlineData(WardCaseKinds.Conflicts, "QUEUE", true)]
    [InlineData(WardCaseKinds.Transfers, "APPROVE", true)]
    [InlineData(WardCaseKinds.Conflicts, "APPROVE", false)]
    [InlineData(WardCaseKinds.Proposals, "QUEUE", false)]
    public void Decision_validator_only_accepts_actions_owned_by_the_use_case(
        string kind,
        string decision,
        bool valid)
    {
        var validator = new DecideWardCaseCommandValidator();

        var result = validator.Validate(
            new DecideWardCaseCommand(kind, 1, decision, "Reviewed", "PENDING"));

        result.IsValid.Should().Be(valid);
    }

    [Fact]
    public async Task List_handler_passes_the_database_scoped_actor_to_the_transaction_service()
    {
        var actor = new WardActor(42, 7, "Ward Officer");
        var actorContext = new Mock<IWardActorContext>();
        actorContext.Setup(x => x.RequireAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(actor);
        var wardSlots = new Mock<IWardSlots>();
        wardSlots.Setup(x => x.ListAsync(
                actor,
                WardCaseKinds.Proposals,
                2,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CasePage([], 2, false));

        var result = await new ListWardCasesQueryHandler(actorContext.Object, wardSlots.Object)
            .Handle(new ListWardCasesQuery(WardCaseKinds.Proposals, 2), CancellationToken.None);

        result.Page.Should().Be(2);
        wardSlots.VerifyAll();
    }
}
