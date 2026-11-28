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

    [Fact]
    public async Task Restore_report_passes_the_hidden_status_and_the_actor_to_the_repository()
    {
        var repository = new Mock<IPlatformAdministrationRepository>();
        repository.Setup(x => x.RestoreReportedContentAsync(
                11,
                ReportedContentStatuses.Hidden,
                7,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ContentDecisionResult(ContentDecisionOutcome.Updated, Report(11)));

        var result = await new RestoreReportedContentCommandHandler(AdminContext().Object, repository.Object)
            .Handle(new RestoreReportedContentCommand(11, ReportedContentStatuses.Hidden), CancellationToken.None);

        result.ReportId.Should().Be(11);
        repository.VerifyAll();
    }

    [Theory]
    [InlineData(ContentDecisionOutcome.NotFound, typeof(NotFoundException))]
    [InlineData(ContentDecisionOutcome.ContentNotFound, typeof(DomainRuleException))]
    [InlineData(ContentDecisionOutcome.Conflict, typeof(ConflictException))]
    public async Task Restore_report_maps_repository_failures_to_domain_errors(
        ContentDecisionOutcome outcome,
        Type exceptionType)
    {
        var repository = new Mock<IPlatformAdministrationRepository>();
        repository.Setup(x => x.RestoreReportedContentAsync(
                It.IsAny<long>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ContentDecisionResult(outcome, null));

        var action = () => new RestoreReportedContentCommandHandler(AdminContext().Object, repository.Object)
            .Handle(new RestoreReportedContentCommand(11, ReportedContentStatuses.Hidden), CancellationToken.None);

        (await action.Should().ThrowAsync<Exception>()).Which.Should().BeOfType(exceptionType);
    }

    [Theory]
    [InlineData(ReportedContentStatuses.Pending)]
    [InlineData(ReportedContentStatuses.Dismissed)]
    [InlineData("")]
    public void Only_a_hidden_report_can_be_restored(string expectedStatus)
    {
        new RestoreReportedContentCommandValidator()
            .Validate(new RestoreReportedContentCommand(11, expectedStatus))
            .IsValid.Should().BeFalse();
        new RestoreReportedContentCommandValidator()
            .Validate(new RestoreReportedContentCommand(11, ReportedContentStatuses.Hidden))
            .IsValid.Should().BeTrue();
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

    [Fact]
    public async Task Renaming_a_category_trims_the_name_and_records_the_admin()
    {
        var repository = new Mock<IPlatformAdministrationRepository>();
        repository.Setup(x => x.FoodCategoryNameExistsAsync("Bún, phở", 3, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        repository.Setup(x => x.RenameFoodCategoryAsync(3, "Bún, phở", 7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FoodCategoryRow(3, "Bún, phở", 12, "Admin"));

        var result = await new RenameFoodCategoryCommandHandler(AdminContext().Object, repository.Object)
            .Handle(new RenameFoodCategoryCommand(3, "  Bún, phở  "), CancellationToken.None);

        (result.CategoryId, result.CategoryName).Should().Be((3, "Bún, phở"));
        repository.VerifyAll();
    }

    [Fact]
    public async Task Renaming_a_category_to_another_categorys_name_is_a_conflict()
    {
        var repository = new Mock<IPlatformAdministrationRepository>();
        repository.Setup(x => x.FoodCategoryNameExistsAsync("Đồ uống", 3, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var act = () => new RenameFoodCategoryCommandHandler(AdminContext().Object, repository.Object)
            .Handle(new RenameFoodCategoryCommand(3, "Đồ uống"), CancellationToken.None);

        await act.Should().ThrowAsync<ConflictException>().WithMessage(PlatformAdministrationMessages.CategoryDuplicate);
        repository.Verify(x => x.RenameFoodCategoryAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Renaming_an_unknown_category_is_not_found()
    {
        var repository = new Mock<IPlatformAdministrationRepository>();
        repository.Setup(x => x.FoodCategoryNameExistsAsync("Món mới", 99, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        repository.Setup(x => x.RenameFoodCategoryAsync(99, "Món mới", 7, It.IsAny<CancellationToken>())).ReturnsAsync((FoodCategoryRow?)null);

        var act = () => new RenameFoodCategoryCommandHandler(AdminContext().Object, repository.Object)
            .Handle(new RenameFoodCategoryCommand(99, "Món mới"), CancellationToken.None);

        await act.Should().ThrowAsync<NotFoundException>().WithMessage(PlatformAdministrationMessages.CategoryNotFound);
    }

    [Theory]
    [InlineData(0, "Đồ uống", false)]
    [InlineData(3, "", false)]
    [InlineData(3, "Đồ uống", true)]
    public void A_rename_needs_a_category_and_a_name(int id, string name, bool valid) =>
        new RenameFoodCategoryCommandValidator().Validate(new RenameFoodCategoryCommand(id, name)).IsValid.Should().Be(valid);

    [Fact]
    public void A_category_name_is_at_most_100_characters() =>
        new RenameFoodCategoryCommandValidator().Validate(new RenameFoodCategoryCommand(3, new string('a', 101)))
            .IsValid.Should().BeFalse();

    [Fact]
    public async Task The_reported_content_list_reads_the_status_case_insensitively_and_keeps_the_page()
    {
        var repository = new Mock<IPlatformAdministrationRepository>();
        var row = new ReportedContentRow(5, "MENU_ITEM", 11, "Bánh mì", null, "VISIBLE", true, "Khách", "Ảnh sai món",
            "PENDING", null, DateTime.UtcNow, null);
        repository.Setup(x => x.ListReportedContentAsync("PENDING", 2, 20, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PlatformPage<ReportedContentRow>([row], 2, 20, 21));

        var page = await new ListReportedContentQueryHandler(AdminContext().Object, repository.Object)
            .Handle(new ListReportedContentQuery("pending", 2, 20), CancellationToken.None);

        (page.Page, page.PageSize, page.TotalCount).Should().Be((2, 20, 21));
        page.Items.Should().ContainSingle().Which.ReportId.Should().Be(5);
    }

    [Fact]
    public async Task The_reported_content_list_without_a_status_lists_every_status()
    {
        var repository = new Mock<IPlatformAdministrationRepository>();
        repository.Setup(x => x.ListReportedContentAsync(null, 1, 50, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PlatformPage<ReportedContentRow>([], 1, 50, 0));

        var page = await new ListReportedContentQueryHandler(AdminContext().Object, repository.Object)
            .Handle(new ListReportedContentQuery("  "), CancellationToken.None);

        page.Items.Should().BeEmpty();
        repository.VerifyAll();
    }

    [Theory]
    [InlineData("PENDING", 1, 50, true)]
    [InlineData("NOT_A_STATUS", 1, 50, false)]
    [InlineData(null, 0, 50, false)]
    [InlineData(null, 1, 101, false)]
    public void The_reported_content_list_checks_status_and_paging(string? status, int pageNo, int size, bool valid) =>
        new ListReportedContentQueryValidator().Validate(new ListReportedContentQuery(status, pageNo, size)).IsValid.Should().Be(valid);

    // ---------------- read-only admin queries ----------------

    private static OrderComplaintRow Complaint(long id, string status = ComplaintStatuses.Open) => new(
        id, 20, "ORD-20", "COMPLETED", "Khách", "Quán", ComplaintTypes.RefundRequest, "Món hỏng", 50_000,
        status, null, null, DateTime.UtcNow, null, 100_000, "MOMO", 10_000, 3, "PENDING");

    [Theory]
    [InlineData(null, null)]
    [InlineData(" ", null)]
    [InlineData(" open ", ComplaintStatuses.Open)]
    public async Task The_complaint_list_normalises_its_filter_and_keeps_paging(string? status, string? expected)
    {
        var repository = new Mock<IPlatformAdministrationRepository>();
        repository.Setup(x => x.ListOrderComplaintsAsync(expected, 2, 10, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PlatformPage<OrderComplaintRow>([Complaint(1), Complaint(2)], 2, 10, 12));

        var page = await new ListOrderComplaintsQueryHandler(AdminContext().Object, repository.Object)
            .Handle(new ListOrderComplaintsQuery(status, 2, 10), default);

        page.Items.Should().HaveCount(2);
        page.TotalCount.Should().Be(12);
        page.Items[0].RefundedAmount.Should().Be(10_000);
        repository.VerifyAll();
    }

    [Fact]
    public async Task One_complaint_or_report_is_found_or_not_found()
    {
        var repository = new Mock<IPlatformAdministrationRepository>();
        repository.Setup(x => x.GetOrderComplaintAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(Complaint(1));
        repository.Setup(x => x.GetReportedContentAsync(5, It.IsAny<CancellationToken>())).ReturnsAsync(Report(5));

        (await new GetOrderComplaintQueryHandler(AdminContext().Object, repository.Object)
            .Handle(new GetOrderComplaintQuery(1), default)).ComplaintId.Should().Be(1);
        (await new GetReportedContentQueryHandler(AdminContext().Object, repository.Object)
            .Handle(new GetReportedContentQuery(5), default)).ReportId.Should().Be(5);

        var missingComplaint = () => new GetOrderComplaintQueryHandler(AdminContext().Object, repository.Object)
            .Handle(new GetOrderComplaintQuery(2), default);
        await missingComplaint.Should().ThrowAsync<NotFoundException>();
        var missingReport = () => new GetReportedContentQueryHandler(AdminContext().Object, repository.Object)
            .Handle(new GetReportedContentQuery(6), default);
        await missingReport.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task The_profile_and_the_category_list_come_from_the_signed_in_admin()
    {
        (await new GetPlatformAdminProfileQueryHandler(AdminContext().Object).Handle(new GetPlatformAdminProfileQuery(), default))
            .Should().BeEquivalentTo(new { UserId = 7L, Name = "Admin" });

        var repository = new Mock<IPlatformAdministrationRepository>();
        repository.Setup(x => x.ListFoodCategoriesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new FoodCategoryRow(1, "Bánh mì", 3, "Admin"), new FoodCategoryRow(2, "Chè", 0, null)]);
        var list = await new ListFoodCategoriesQueryHandler(AdminContext().Object, repository.Object)
            .Handle(new ListFoodCategoriesQuery(), default);
        list.Select(c => c.CategoryName).Should().Equal("Bánh mì", "Chè");
    }

    [Fact]
    public async Task A_non_admin_is_stopped_before_any_read()
    {
        var actor = new Mock<IPlatformAdminContext>();
        actor.Setup(x => x.RequireAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new ForbiddenException("Chỉ quản trị viên."));
        var repository = new Mock<IPlatformAdministrationRepository>(MockBehavior.Strict);

        var action = () => new ListOrderComplaintsQueryHandler(actor.Object, repository.Object)
            .Handle(new ListOrderComplaintsQuery(null), default);

        await action.Should().ThrowAsync<ForbiddenException>();
    }

    [Theory]
    [InlineData("open", 1, 50, true)]
    [InlineData("closed", 1, 50, false)]
    [InlineData(null, 0, 50, false)]
    [InlineData(null, 1, 101, false)]
    public void The_complaint_list_query_is_validated(string? status, int page, int size, bool valid) =>
        new ListOrderComplaintsQueryValidator().Validate(new ListOrderComplaintsQuery(status, page, size)).IsValid.Should().Be(valid);

    [Theory]
    [InlineData("RESOLVE", "OPEN", null, true)]
    [InlineData("reject", "UNDER_REVIEW", null, true)]
    [InlineData("RESOLVE", "OPEN", 20_000.0, true)]
    [InlineData("REJECT", "OPEN", 20_000.0, false)]
    [InlineData("RESOLVE", "RESOLVED", null, false)]
    [InlineData("MAYBE", "OPEN", null, false)]
    [InlineData("RESOLVE", "OPEN", -1.0, false)]
    [InlineData("RESOLVE", "OPEN", 0.5, false)]
    public void A_complaint_decision_is_validated(string decision, string expected, double? refund, bool valid) =>
        new DecideOrderComplaintCommandValidator()
            .Validate(new DecideOrderComplaintCommand(1, decision, "Ghi chú", expected, refund is null ? null : (decimal)refund))
            .IsValid.Should().Be(valid);

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
