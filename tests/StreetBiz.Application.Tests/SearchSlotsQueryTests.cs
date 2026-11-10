using FluentAssertions;
using Moq;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Features.SidewalkSlots.SearchSlots;

namespace StreetBiz.Application.Tests;

/// <summary>SIDE-01: the slot map. SQL narrows by a box; the exact radius and the order are decided here.</summary>
public sealed class SearchSlotsQueryTests
{
    // Hải Châu centre. 0.001 degree of latitude is about 111 m.
    private const decimal CenterLat = 16.0600m;
    private const decimal CenterLng = 108.2200m;

    private readonly Mock<ISidewalkSlotRepository> slots = new();
    private SlotSearchArea? seen;

    public SearchSlotsQueryTests()
    {
        slots.Setup(s => s.SearchAsync(It.IsAny<SlotSearchArea>(), It.IsAny<CancellationToken>()))
            .Callback<SlotSearchArea, CancellationToken>((area, _) => seen = area);
    }

    private static SlotRow Row(long id, decimal lat, decimal lng, string status = "AVAILABLE") => new(
        id, $"NVL-{id:00}", 1, "Đường Nguyễn Văn Linh", 10, lat, lng, 2m, 3m, status, "WARD",
        30_000m, new TimeOnly(5, 0), new TimeOnly(22, 0), null, [], false, false, false, null, null, null);

    private void Returns(params SlotRow[] rows) =>
        slots.Setup(s => s.SearchAsync(It.IsAny<SlotSearchArea>(), It.IsAny<CancellationToken>()))
            .Callback<SlotSearchArea, CancellationToken>((area, _) => seen = area)
            .ReturnsAsync(rows);

    private SearchSlotsQueryHandler Handler() => new(slots.Object);

    private static SearchSlotsQuery Nearby(double radius, int take = 100) =>
        new(CenterLat, CenterLng, radius, null, null, null, null, null, null, Take: take);

    // ---- validation ---------------------------------------------------------------------

    [Fact]
    public void A_search_with_no_area_at_all_is_refused()
    {
        var query = new SearchSlotsQuery(null, null, null, null, null, null, null, null, null);

        new SearchSlotsQueryValidator().Validate(query).IsValid.Should().BeFalse();
    }

    [Fact]
    public void A_centre_without_a_radius_is_not_an_area()
    {
        var query = new SearchSlotsQuery(CenterLat, CenterLng, null, null, null, null, null, null, null);

        new SearchSlotsQueryValidator().Validate(query).IsValid.Should().BeFalse();
    }

    [Fact]
    public void A_centre_a_box_or_a_zone_each_make_a_valid_search()
    {
        var validator = new SearchSlotsQueryValidator();

        validator.Validate(Nearby(500)).IsValid.Should().BeTrue();
        validator.Validate(new SearchSlotsQuery(null, null, null, 16.0m, 16.1m, 108.1m, 108.3m, null, null))
            .IsValid.Should().BeTrue();
        validator.Validate(new SearchSlotsQuery(null, null, null, null, null, null, null, null, 4))
            .IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void The_radius_must_be_positive(double radius)
    {
        new SearchSlotsQueryValidator().Validate(Nearby(radius)).IsValid.Should().BeFalse();
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(500, true)]
    [InlineData(501, false)]
    public void Take_is_between_1_and_500(int take, bool valid)
    {
        new SearchSlotsQueryValidator().Validate(Nearby(500, take)).IsValid.Should().Be(valid);
    }

    [Fact]
    public void A_zone_id_must_be_positive()
    {
        var query = new SearchSlotsQuery(null, null, null, null, null, null, null, null, 0);

        new SearchSlotsQueryValidator().Validate(query).IsValid.Should().BeFalse();
    }

    // ---- handler ------------------------------------------------------------------------

    [Fact]
    public async Task Cuts_the_box_to_the_exact_radius_and_sorts_nearest_first_with_the_distance()
    {
        Returns(
            Row(1, CenterLat + 0.0027m, CenterLng),   // about 300 m
            Row(2, CenterLat + 0.0009m, CenterLng),   // about 100 m
            Row(3, CenterLat + 0.0045m, CenterLng),   // about 500 m: inside the box, outside the radius
            Row(4, CenterLat, CenterLng));            // on the spot

        var result = await Handler().Handle(Nearby(350), default);

        result.Select(r => r.SlotId).Should().Equal(4, 2, 1);
        result[0].DistanceMeters.Should().BeApproximately(0, 1);
        result[1].DistanceMeters.Should().BeApproximately(100, 5);
        result[2].DistanceMeters.Should().BeApproximately(300, 8);
    }

    [Fact]
    public async Task Asks_the_repository_for_a_box_around_the_centre_not_for_the_whole_ward()
    {
        Returns();

        await Handler().Handle(Nearby(1000), default);

        seen.Should().NotBeNull();
        seen!.MinLatitude.Should().BeLessThan(CenterLat);
        seen.MaxLatitude.Should().BeGreaterThan(CenterLat);
        seen.MinLongitude.Should().BeLessThan(CenterLng);
        seen.MaxLongitude.Should().BeGreaterThan(CenterLng);
        ((double)(seen.MaxLatitude!.Value - seen.MinLatitude!.Value) * 111_000).Should().BeApproximately(2000, 60);
    }

    [Fact]
    public async Task An_explicit_box_is_passed_through_and_nothing_is_sorted_by_distance()
    {
        Returns(Row(9, 16.02m, 108.20m), Row(3, 16.03m, 108.21m), Row(5, 16.04m, 108.22m));
        var query = new SearchSlotsQuery(null, null, null, 16.0m, 16.1m, 108.1m, 108.3m, null, null);

        var result = await Handler().Handle(query, default);

        seen.Should().Be(new SlotSearchArea(16.0m, 16.1m, 108.1m, 108.3m, null, null, false));
        result.Select(r => r.SlotId).Should().Equal(3, 5, 9); // ties break by slot id, so Take is stable
        result.Should().OnlyContain(r => r.DistanceMeters == null);
    }

    [Fact]
    public async Task A_zone_query_has_no_area_and_returns_the_whole_street()
    {
        Returns(Row(2, 16.01m, 108.2m), Row(1, 16.02m, 108.2m));
        var query = new SearchSlotsQuery(null, null, null, null, null, null, null, 10, 4, IncludeUnavailable: true);

        var result = await Handler().Handle(query, default);

        seen.Should().Be(new SlotSearchArea(null, null, null, null, 10, 4, true));
        result.Select(r => r.SlotId).Should().Equal(1, 2);
    }

    [Fact]
    public async Task Take_keeps_the_nearest_ones()
    {
        Returns(
            Row(1, CenterLat + 0.0030m, CenterLng),
            Row(2, CenterLat + 0.0010m, CenterLng),
            Row(3, CenterLat + 0.0020m, CenterLng));

        var result = await Handler().Handle(Nearby(1000, take: 2), default);

        result.Select(r => r.SlotId).Should().Equal(2, 3);
    }

    [Fact]
    public async Task A_slot_exactly_on_the_radius_edge_is_kept_and_an_empty_result_is_empty()
    {
        Returns();
        (await Handler().Handle(Nearby(200), default)).Should().BeEmpty();

        Returns(Row(1, CenterLat, CenterLng));
        (await Handler().Handle(Nearby(1), default)).Should().ContainSingle();
    }
}
