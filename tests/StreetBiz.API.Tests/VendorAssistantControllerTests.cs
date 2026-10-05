using Microsoft.AspNetCore.Mvc;
using Moq;
using StreetBiz.API.Controllers;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Features.WardCompliance;

namespace StreetBiz.API.Tests;

public sealed class VendorAssistantControllerTests
{
    private readonly Mock<ICurrentUser> currentUser = new();
    private readonly Mock<IAiComplianceService> ai = new();

    private VendorAssistantController Controller(string role = "VENDOR")
    {
        currentUser.SetupGet(c => c.RoleCode).Returns(role);
        ai.Setup(a => a.AnswerVendorAssistantAsync(It.IsAny<string>(), It.IsAny<string?>(), default)).ReturnsAsync("ok");
        return new VendorAssistantController(currentUser.Object, ai.Object);
    }

    [Theory]
    [InlineData("WARD_AUTHORITY")]
    [InlineData("CUSTOMER")]
    [InlineData("PLATFORM_ADMIN")]
    public async Task Only_vendors_may_use_the_assistant(string role)
    {
        await Assert.ThrowsAsync<ForbiddenException>(
            () => Controller(role).Ask(new VendorAssistantRequest("Cần giấy tờ gì?"), default));
        ai.Verify(a => a.AnswerVendorAssistantAsync(It.IsAny<string>(), It.IsAny<string?>(), default), Times.Never);
    }

    [Fact]
    public async Task A_vendor_gets_an_advisory_answer()
    {
        var result = await Controller().Ask(new VendorAssistantRequest("  Cần giấy tờ gì?  "), default);

        var response = Assert.IsType<VendorAssistantResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal("ok", response.Answer);
        ai.Verify(a => a.AnswerVendorAssistantAsync("Cần giấy tờ gì?", null, default), Times.Once);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task An_empty_question_is_refused_before_the_model_is_called(string question)
    {
        await Assert.ThrowsAsync<ValidationAppException>(
            () => Controller().Ask(new VendorAssistantRequest(question), default));
        ai.Verify(a => a.AnswerVendorAssistantAsync(It.IsAny<string>(), It.IsAny<string?>(), default), Times.Never);
    }

    [Fact]
    public async Task An_oversized_question_or_context_is_refused()
    {
        var controller = Controller();

        await Assert.ThrowsAsync<ValidationAppException>(() => controller.Ask(
            new VendorAssistantRequest(new string('a', VendorAssistantController.MaxQuestionLength + 1)), default));
        await Assert.ThrowsAsync<ValidationAppException>(() => controller.Ask(
            new VendorAssistantRequest("ok", new string('b', VendorAssistantController.MaxContextLength + 1)), default));
    }
}
