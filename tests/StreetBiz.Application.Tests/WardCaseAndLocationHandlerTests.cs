using FluentAssertions;
using Moq;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Features.WardSlots;
using ProviderPoint = StreetBiz.Application.Common.Models.GeoPoint;

namespace StreetBiz.Application.Tests;

/// <summary>WARD-16/17/18 decisions, the proposal pin and SYS-01/SYS-02 location tools.</summary>
public sealed class WardCaseAndLocationHandlerTests
{
    private static readonly WardActor Officer = new(2, 10, "Cán bộ");
    private readonly Mock<IWardActorContext> actor = new();
    private readonly Mock<IWardSlots> slots = new();

    public WardCaseAndLocationHandlerTests() => actor.Setup(a => a.RequireAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Officer);

    [Theory]
    [InlineData("proposals", "APPROVE", true)]
    [InlineData("proposals", "reject", true)]
    [InlineData("proposals", "QUEUE", false)]
    [InlineData("conflicts", "QUEUE", true)]
    [InlineData("conflicts", "APPROVE", false)]
    [InlineData("transfers", "APPROVE", true)]
    [InlineData("registrations", "APPROVE", false)]
    public void Each_case_kind_accepts_only_its_own_decisions(string kind, string decision, bool valid) =>
        new DecideWardCaseCommandValidator().Validate(new DecideWardCaseCommand(kind, 3, decision, "Lý do", "PENDING")).IsValid.Should().Be(valid);

    [Fact]
    public void A_case_decision_needs_a_reason_and_an_expected_status()
    {
        var validator = new DecideWardCaseCommandValidator();

        validator.Validate(new DecideWardCaseCommand("proposals", 3, "APPROVE", "", "PENDING")).IsValid.Should().BeFalse();
        validator.Validate(new DecideWardCaseCommand("proposals", 3, "APPROVE", new string('x', 501), "PENDING")).IsValid.Should().BeFalse();
        validator.Validate(new DecideWardCaseCommand("proposals", 3, "APPROVE", "ok", "")).IsValid.Should().BeFalse();
        validator.Validate(new DecideWardCaseCommand("proposals", 0, "APPROVE", "ok", "PENDING")).IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task A_decision_is_normalised_before_it_reaches_the_case_service()
    {
        await new DecideWardCaseCommandHandler(actor.Object, slots.Object)
            .Handle(new DecideWardCaseCommand("transfers", 8, "approve", "  Đủ điều kiện  ", "ACCEPTED_BY_RECEIVER"), default);

        slots.Verify(s => s.DecideAsync(Officer, "transfers", 8, new ReviewDecision("APPROVE", "Đủ điều kiện", "ACCEPTED_BY_RECEIVER"), It.IsAny<CancellationToken>()));
    }

    [Theory]
    [InlineData(16.06, 108.21, true)]
    [InlineData(91, 108.21, false)]
    [InlineData(16.06, -181, false)]
    [InlineData(double.NaN, 108.21, false)]
    [InlineData(16.06, double.PositiveInfinity, false)]
    public void Pins_and_geofence_checks_need_real_coordinates(double lat, double lng, bool valid)
    {
        new PinWardProposalCommandValidator().Validate(new PinWardProposalCommand(4, lat, lng)).IsValid.Should().Be(valid);
        new VerifyWardLocationQueryValidator().Validate(new VerifyWardLocationQuery(lat, lng)).IsValid.Should().Be(valid);
    }

    [Fact]
    public async Task Pinning_a_proposal_passes_the_point_through()
    {
        await new PinWardProposalCommandHandler(actor.Object, slots.Object).Handle(new PinWardProposalCommand(4, 16.06, 108.22), default);

        slots.Verify(s => s.PinAsync(Officer, 4, new GeoPoint(16.06, 108.22), It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task Address_search_trims_asks_for_five_candidates_and_labels_unnamed_points()
    {
        var geocoding = new Mock<IGeocodingService>();
        geocoding.Setup(g => g.SearchAsync("Nguyễn Văn Linh", 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new ProviderPoint(16.0610m, 108.2180m, "Công viên Nguyễn Văn Linh"), new ProviderPoint(16.07m, 108.22m, null)]);

        var results = await new SearchWardLocationsQueryHandler(actor.Object, geocoding.Object)
            .Handle(new SearchWardLocationsQuery("  Nguyễn Văn Linh "), default);

        results.Should().HaveCount(2);
        results[0].Label.Should().Be("Công viên Nguyễn Văn Linh");
        results[0].Point.Should().Be(new GeoPoint(16.061, 108.218));
        results[1].Label.Should().Contain("16.07").And.Contain("108.22");
    }

    [Fact]
    public async Task An_unavailable_geocoder_simply_returns_no_candidates()
    {
        var geocoding = new Mock<IGeocodingService>();
        geocoding.Setup(g => g.SearchAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);

        var results = await new SearchWardLocationsQueryHandler(actor.Object, geocoding.Object).Handle(new SearchWardLocationsQuery("xyz"), default);

        results.Should().BeEmpty();
    }

    [Fact]
    public void Address_search_needs_an_address_of_reasonable_length()
    {
        new SearchWardLocationsQueryValidator().Validate(new SearchWardLocationsQuery("")).IsValid.Should().BeFalse();
        new SearchWardLocationsQueryValidator().Validate(new SearchWardLocationsQuery(new string('a', 501))).IsValid.Should().BeFalse();
        new SearchWardLocationsQueryValidator().Validate(new SearchWardLocationsQuery("12 Lê Duẩn")).IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task The_geofence_is_checked_against_the_officers_own_ward()
    {
        var geolocation = new Mock<IGeolocation>();
        geolocation.Setup(g => g.Verify(10, new GeoPoint(16.06, 108.22))).Returns(new GeofenceResult(true, 10, "v1"));

        var result = await new VerifyWardLocationQueryHandler(actor.Object, geolocation.Object).Handle(new VerifyWardLocationQuery(16.06, 108.22), default);

        result.Inside.Should().BeTrue();
        result.WardId.Should().Be(10);
    }

    [Fact]
    public async Task Location_tools_are_ward_officer_only()
    {
        actor.Setup(a => a.RequireAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new ForbiddenException("x"));
        var geocoding = new Mock<IGeocodingService>();
        var geolocation = new Mock<IGeolocation>();

        await FluentActions.Invoking(() => new SearchWardLocationsQueryHandler(actor.Object, geocoding.Object).Handle(new SearchWardLocationsQuery("a"), default)).Should().ThrowAsync<ForbiddenException>();
        await FluentActions.Invoking(() => new VerifyWardLocationQueryHandler(actor.Object, geolocation.Object).Handle(new VerifyWardLocationQuery(16, 108), default)).Should().ThrowAsync<ForbiddenException>();
        await FluentActions.Invoking(() => new PinWardProposalCommandHandler(actor.Object, slots.Object).Handle(new PinWardProposalCommand(1, 16, 108), default)).Should().ThrowAsync<ForbiddenException>();

        geocoding.VerifyNoOtherCalls();
        geolocation.VerifyNoOtherCalls();
        slots.VerifyNoOtherCalls();
    }
}
