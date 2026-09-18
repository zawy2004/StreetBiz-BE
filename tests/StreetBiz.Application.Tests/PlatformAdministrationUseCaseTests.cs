using FluentAssertions;
using Moq;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.Features.PlatformAdministration;

namespace StreetBiz.Application.Tests;

public sealed class PlatformAdministrationUseCaseTests
{
    [Theory]
    [InlineData(RoleCodes.Customer, AccountStatuses.Active)]
    [InlineData(RoleCodes.PlatformAdmin, AccountStatuses.Suspended)]
    public async Task Platform_context_rechecks_the_database_role_and_status(
        string storedRole,
        string storedStatus)
    {
        var currentUser = new Mock<ICurrentUser>();
        currentUser.SetupGet(x => x.UserId).Returns(7);
        currentUser.SetupGet(x => x.RoleCode).Returns(RoleCodes.PlatformAdmin);
        var users = new Mock<IUserAccountRepository>();
        users.Setup(x => x.GetByIdAsync(7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AppUser(
                7,
                "0900000007",
                "hash",
                "Admin",
                storedRole,
                null,
                storedStatus,
                DateTime.UtcNow));

        var action = () => new PlatformAdminContext(currentUser.Object, users.Object)
            .RequireAsync(CancellationToken.None);

        await action.Should().ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task Create_category_trims_the_name_and_returns_the_persisted_row()
    {
        var actor = AdminContext();
        var repository = new Mock<IPlatformAdministrationRepository>();
        repository.Setup(x => x.FoodCategoryNameExistsAsync(
                "Đồ uống",
                null,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        repository.Setup(x => x.CreateFoodCategoryAsync(
                "Đồ uống",
                7,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FoodCategoryRow(3, "Đồ uống", 0, "Admin"));

        var result = await new CreateFoodCategoryCommandHandler(actor.Object, repository.Object)
            .Handle(new CreateFoodCategoryCommand("  Đồ uống  "), CancellationToken.None);

        result.CategoryId.Should().Be(3);
        result.CategoryName.Should().Be("Đồ uống");
        repository.VerifyAll();
    }

    [Fact]
    public async Task Duplicate_category_is_rejected_before_an_insert()
    {
        var repository = new Mock<IPlatformAdministrationRepository>();
        repository.Setup(x => x.FoodCategoryNameExistsAsync(
                "Đồ uống",
                null,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var handler = new CreateFoodCategoryCommandHandler(AdminContext().Object, repository.Object);

        var action = () => handler.Handle(
            new CreateFoodCategoryCommand("Đồ uống"), CancellationToken.None);

        await action.Should().ThrowAsync<ConflictException>()
            .WithMessage(PlatformAdministrationMessages.CategoryDuplicate);
        repository.Verify(x => x.CreateFoodCategoryAsync(
            It.IsAny<string>(), It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Category_in_use_cannot_be_deleted()
    {
        var repository = new Mock<IPlatformAdministrationRepository>();
        repository.Setup(x => x.DeleteFoodCategoryAsync(3, 7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(CategoryDeleteOutcome.InUse);
        var handler = new DeleteFoodCategoryCommandHandler(AdminContext().Object, repository.Object);

        var action = () => handler.Handle(new DeleteFoodCategoryCommand(3), CancellationToken.None);

        await action.Should().ThrowAsync<DomainRuleException>()
            .WithMessage(PlatformAdministrationMessages.CategoryInUse);
    }

    [Fact]
    public async Task Hide_report_normalizes_the_decision_and_uses_the_expected_status()
    {
        var repository = new Mock<IPlatformAdministrationRepository>();
        repository.Setup(x => x.DecideReportedContentAsync(
                11,
                ReportedContentStatuses.Pending,
                ContentModerationDecisions.Hide,
                7,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ContentDecisionResult(ContentDecisionOutcome.Updated, Report(11)));

        var result = await new DecideReportedContentCommandHandler(
                AdminContext().Object,
                repository.Object)
            .Handle(
                new DecideReportedContentCommand(11, "hide", ReportedContentStatuses.Pending),
                CancellationToken.None);

        result.Status.Should().Be(ReportedContentStatuses.Hidden);
        repository.VerifyAll();
    }

    [Theory]
    [InlineData(ComplaintDecisionOutcome.Conflict, typeof(ConflictException))]
    [InlineData(ComplaintDecisionOutcome.RefundNotAllowed, typeof(DomainRuleException))]
    [InlineData(ComplaintDecisionOutcome.PaymentNotFound, typeof(DomainRuleException))]
    [InlineData(ComplaintDecisionOutcome.RefundExceedsLimit, typeof(DomainRuleException))]
    public async Task Complaint_decision_maps_repository_failures_to_domain_errors(
        ComplaintDecisionOutcome outcome,
        Type exceptionType)
    {
        var repository = new Mock<IPlatformAdministrationRepository>();
        repository.Setup(x => x.DecideOrderComplaintAsync(
                21,
                It.Is<ComplaintResolution>(resolution =>
                    resolution.Decision == ComplaintDecisions.Resolve
                    && resolution.Notes == "Đã xác minh"
                    && resolution.ExpectedStatus == ComplaintStatuses.Open
                    && resolution.ApprovedRefundAmount == 50_000),
                7,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ComplaintDecisionResult(outcome, null));
        var handler = new DecideOrderComplaintCommandHandler(
            AdminContext().Object,
            repository.Object);

        var action = () => handler.Handle(
            new DecideOrderComplaintCommand(
                21,
                "resolve",
                "  Đã xác minh  ",
                ComplaintStatuses.Open,
                50_000),
            CancellationToken.None);

        await action.Should().ThrowAsync<Exception>()
            .Where(exception => exception.GetType() == exceptionType);
        repository.VerifyAll();
    }

    private static Mock<IPlatformAdminContext> AdminContext()
    {
        var actor = new Mock<IPlatformAdminContext>();
        actor.Setup(x => x.RequireAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PlatformAdminActor(7, "Admin"));
        return actor;
    }

    private static ReportedContentRow Report(long reportId) => new(
        reportId,
        ReportedContentTypes.MenuItem,
        4,
        "Bánh mì",
        "Mô tả",
        "HIDDEN",
        true,
        "Khách hàng",
        "Nội dung vi phạm",
        ReportedContentStatuses.Hidden,
        "Admin",
        DateTime.UtcNow,
        DateTime.UtcNow);
}
