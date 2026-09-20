using FluentAssertions;
using Moq;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.Features.Commerce;

namespace StreetBiz.Application.Tests;

/// <summary>Customer discovery: opening hours, service areas, storefront list/detail and menu search (DISC-02..06).</summary>
public sealed class DiscoveryUseCaseTests
{
    // Saturday 10:00 in Vietnam (UTC+7).
    private static readonly DateTime SaturdayMorning = new(2026, 9, 19, 3, 0, 0, DateTimeKind.Utc);

    private const decimal NearLat = 16.0600m;
    private const decimal NearLng = 108.2140m;
    private const decimal FarLat = 16.0120m; // about 5.4 km south of the customer
    private const decimal FarLng = 108.2400m;

    [Fact]
    public void Local_time_uses_the_vietnam_offset_and_iso_weekdays()
    {
        StorefrontHours.LocalNow(SaturdayMorning).Should().Be(new MarketplaceOpenAt(6, new TimeOnly(10, 0)));

        // 17:30 UTC on Saturday is already 00:30 on Sunday in Vietnam; Sunday is day 7, not 0.
        StorefrontHours.LocalNow(new DateTime(2026, 9, 19, 17, 30, 0, DateTimeKind.Utc))
            .Should().Be(new MarketplaceOpenAt(7, new TimeOnly(0, 30)));
    }

    [Fact]
    public void A_storefront_is_open_only_inside_a_window_of_the_current_day()
    {
        var hours = new[] { new StorefrontHourRow(6, new TimeOnly(6, 0), new TimeOnly(11, 0)) };

        StorefrontHours.IsOpen(hours, new(6, new TimeOnly(10, 59))).Should().BeTrue();
        StorefrontHours.IsOpen(hours, new(6, new TimeOnly(6, 0))).Should().BeTrue("opening time is inclusive");
        StorefrontHours.IsOpen(hours, new(6, new TimeOnly(11, 0))).Should().BeFalse("closing time is exclusive");
        StorefrontHours.IsOpen(hours, new(5, new TimeOnly(8, 0))).Should().BeFalse("no window for that day");
    }

    [Fact]
    public void A_storefront_with_no_published_hours_is_governed_by_its_open_switch_alone()
    {
        StorefrontHours.IsOpen([], new(3, new TimeOnly(3, 0))).Should().BeTrue();
    }

    [Fact]
    public async Task Storefronts_are_sorted_by_distance_when_a_position_is_sent()
    {
        var repository = new Mock<ICommerceRepository>();
        repository.Setup(x => x.ListStorefrontsAsync(It.IsAny<MarketplaceStorefrontFilter>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([Store(1, "Far", FarLat, FarLng), Store(2, "Near", NearLat, NearLng)]);

        var result = await Handler(repository).Handle(
            new ListStorefrontsQuery(Latitude: NearLat, Longitude: NearLng), CancellationToken.None);

        result.Select(s => s.StorefrontName).Should().Equal("Near", "Far");
        result[0].DistanceMeters.Should().BeLessThan(50);
        result[1].DistanceMeters.Should().BeGreaterThan(5_000);
    }

    [Fact]
    public async Task The_radius_drops_storefronts_outside_the_circle_and_asks_for_a_bounding_box()
    {
        MarketplaceStorefrontFilter? sent = null;
        var repository = new Mock<ICommerceRepository>();
        repository.Setup(x => x.ListStorefrontsAsync(It.IsAny<MarketplaceStorefrontFilter>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback<MarketplaceStorefrontFilter, int, CancellationToken>((f, _, _) => sent = f)
            .ReturnsAsync([Store(1, "Far", FarLat, FarLng), Store(2, "Near", NearLat, NearLng)]);

        var result = await Handler(repository).Handle(
            new ListStorefrontsQuery(Latitude: NearLat, Longitude: NearLng, RadiusMeters: 2_000), CancellationToken.None);

        result.Select(s => s.StorefrontName).Should().Equal("Near");
        sent!.Area.Should().NotBeNull();
    }

    [Fact]
    public async Task Open_now_sends_the_local_moment_and_each_storefront_reports_its_own_state()
    {
        MarketplaceStorefrontFilter? sent = null;
        var morning = new StorefrontHourRow(6, new TimeOnly(6, 0), new TimeOnly(11, 0));
        var evening = new StorefrontHourRow(6, new TimeOnly(16, 0), new TimeOnly(22, 0));
        var repository = new Mock<ICommerceRepository>();
        repository.Setup(x => x.ListStorefrontsAsync(It.IsAny<MarketplaceStorefrontFilter>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback<MarketplaceStorefrontFilter, int, CancellationToken>((f, _, _) => sent = f)
            .ReturnsAsync([Store(1, "Breakfast", NearLat, NearLng, hours: [morning]), Store(2, "Dinner", NearLat, NearLng, hours: [evening])]);

        var result = await Handler(repository).Handle(new ListStorefrontsQuery(OpenNow: true), CancellationToken.None);

        sent!.OpenAt.Should().Be(new MarketplaceOpenAt(6, new TimeOnly(10, 0)));
        result.Single(s => s.StorefrontName == "Breakfast").IsOpenNow.Should().BeTrue();
        result.Single(s => s.StorefrontName == "Dinner").IsOpenNow.Should().BeFalse();
        result.Single(s => s.StorefrontName == "Dinner").TodayHours.Single().OpensAt.Should().Be("16:00");
    }

    [Fact]
    public async Task Without_a_position_storefronts_are_sorted_by_name_and_rating_sort_puts_unrated_last()
    {
        var repository = new Mock<ICommerceRepository>();
        repository.Setup(x => x.ListStorefrontsAsync(It.IsAny<MarketplaceStorefrontFilter>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                Store(1, "Cháo", NearLat, NearLng, rating: 3.5m, ratings: 4),
                Store(2, "Bún", NearLat, NearLng, rating: null),
                Store(3, "Xôi", NearLat, NearLng, rating: 4.8m, ratings: 9)]);

        var byName = await Handler(repository).Handle(new ListStorefrontsQuery(), CancellationToken.None);
        var byRating = await Handler(repository).Handle(
            new ListStorefrontsQuery(Sort: MarketplaceStorefrontSorts.Rating), CancellationToken.None);

        byName.Select(s => s.StorefrontName).Should().Equal("Bún", "Cháo", "Xôi");
        byRating.Select(s => s.StorefrontName).Should().Equal("Xôi", "Cháo", "Bún");
    }

    [Fact]
    public void The_storefront_query_rejects_inconsistent_position_sort_and_paging_arguments()
    {
        var validator = new ListStorefrontsQueryValidator();

        validator.Validate(new ListStorefrontsQuery(Latitude: NearLat)).IsValid.Should().BeFalse("longitude is missing");
        validator.Validate(new ListStorefrontsQuery(RadiusMeters: 500)).IsValid.Should().BeFalse("radius needs a position");
        validator.Validate(new ListStorefrontsQuery(Sort: MarketplaceStorefrontSorts.Distance)).IsValid.Should().BeFalse("distance needs a position");
        validator.Validate(new ListStorefrontsQuery(Sort: "cheapest")).IsValid.Should().BeFalse();
        validator.Validate(new ListStorefrontsQuery(Take: 101)).IsValid.Should().BeFalse();
        validator.Validate(new ListStorefrontsQuery(
            Latitude: NearLat, Longitude: NearLng, RadiusMeters: 1_000, Sort: MarketplaceStorefrontSorts.Distance)).IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Service_areas_count_storefronts_per_ward_and_rank_by_the_nearest_one()
    {
        var repository = new Mock<ICommerceRepository>();
        repository.Setup(x => x.ListStorefrontLocationsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new StorefrontLocationRow(3, "Hòa Quý", "Ngũ Hành Sơn", FarLat, FarLng),
                new StorefrontLocationRow(3, "Hòa Quý", "Ngũ Hành Sơn", 16.0130m, 108.2410m),
                new StorefrontLocationRow(1003, "Nam Dương", "Hải Châu", NearLat, NearLng)]);
        var handler = new ListServiceAreasQueryHandler(repository.Object);

        var located = await handler.Handle(new ListServiceAreasQuery(NearLat, NearLng), CancellationToken.None);
        var unlocated = await handler.Handle(new ListServiceAreasQuery(), CancellationToken.None);

        located.Select(a => a.WardName).Should().Equal("Nam Dương", "Hòa Quý");
        located.Single(a => a.WardId == 3).StorefrontCount.Should().Be(2);
        located[0].DistanceMeters.Should().BeLessThan(50);
        unlocated.Select(a => a.WardName).Should().Equal("Hòa Quý", "Nam Dương");
        unlocated.Should().OnlyContain(a => a.DistanceMeters == null);
    }

    [Fact]
    public async Task A_missing_or_unlisted_storefront_is_not_found()
    {
        var repository = new Mock<ICommerceRepository>();
        repository.Setup(x => x.GetStorefrontAsync(9, It.IsAny<CancellationToken>()))
            .ReturnsAsync((MarketplaceStorefrontDetailRow?)null);

        var act = () => new GetStorefrontQueryHandler(repository.Object, Clock()).Handle(new GetStorefrontQuery(9), CancellationToken.None);

        await act.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task Storefront_detail_groups_the_menu_by_category_and_lists_the_whole_week()
    {
        var repository = new Mock<ICommerceRepository>();
        repository.Setup(x => x.GetStorefrontAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MarketplaceStorefrontDetailRow(
                Store(1, "Xôi Bà Tư", NearLat, NearLng, hours: [
                    new StorefrontHourRow(7, new TimeOnly(6, 0), new TimeOnly(10, 0)),
                    new StorefrontHourRow(1, new TimeOnly(6, 0), new TimeOnly(10, 0))]),
                [Item(11, "Xôi gà", 2, "Món chính", 30_000), Item(12, "Trà đá", 3, "Đồ uống", 5_000), Item(13, "Xôi mặn", 2, "Món chính", 25_000)]));

        var detail = await new GetStorefrontQueryHandler(repository.Object, Clock())
            .Handle(new GetStorefrontQuery(1, NearLat, NearLng), CancellationToken.None);

        detail.Menu.Select(c => c.CategoryName).Should().Equal("Đồ uống", "Món chính");
        detail.Menu[1].Items.Select(i => i.ItemName).Should().Equal("Xôi gà", "Xôi mặn");
        detail.WeeklyHours.Select(h => h.DayOfWeek).Should().Equal(1, 7);
        detail.Storefront.DistanceMeters.Should().BeLessThan(50);
    }

    [Fact]
    public async Task Menu_search_passes_every_filter_through_and_defaults_the_sort_to_name()
    {
        MarketplaceMenuFilter? sent = null;
        var repository = new Mock<ICommerceRepository>();
        repository.Setup(x => x.SearchMenuItemsAsync(It.IsAny<MarketplaceMenuFilter>(), 20, It.IsAny<CancellationToken>()))
            .Callback<MarketplaceMenuFilter, int, CancellationToken>((f, _, _) => sent = f)
            .ReturnsAsync([]);

        await new SearchMarketplaceMenuQueryHandler(repository.Object, Clock()).Handle(
            new SearchMarketplaceMenuQuery("  xôi  ", 20, WardId: 3, CategoryId: 2, MinPrice: 10_000, MaxPrice: 40_000, OpenNow: true),
            CancellationToken.None);

        sent.Should().Be(new MarketplaceMenuFilter(
            "xôi", 3, 2, 10_000, 40_000, new MarketplaceOpenAt(6, new TimeOnly(10, 0)), MarketplaceMenuSorts.Name));
    }

    [Fact]
    public void Menu_search_rejects_a_reversed_price_range_and_an_unknown_sort()
    {
        var validator = new SearchMarketplaceMenuQueryValidator();

        validator.Validate(new SearchMarketplaceMenuQuery(null, MinPrice: 50_000, MaxPrice: 10_000)).IsValid.Should().BeFalse();
        validator.Validate(new SearchMarketplaceMenuQuery(null, Sort: "random")).IsValid.Should().BeFalse();
        validator.Validate(new SearchMarketplaceMenuQuery(null, MinPrice: -1)).IsValid.Should().BeFalse();
        validator.Validate(new SearchMarketplaceMenuQuery("xôi", Sort: MarketplaceMenuSorts.PriceDesc)).IsValid.Should().BeTrue();
    }

    private static ListStorefrontsQueryHandler Handler(Mock<ICommerceRepository> repository) =>
        new(repository.Object, Clock());

    private static IDateTimeProvider Clock()
    {
        var clock = new Mock<IDateTimeProvider>();
        clock.Setup(x => x.UtcNow).Returns(SaturdayMorning);
        return clock.Object;
    }

    private static MarketplaceStorefrontRow Store(
        long id,
        string name,
        decimal latitude,
        decimal longitude,
        decimal? rating = null,
        int ratings = 0,
        IReadOnlyList<StorefrontHourRow>? hours = null) => new(
            id, name, null, null, 100 + id, null, 1003, "Nam Dương", "NVL", $"S-{id}", latitude, longitude,
            rating, ratings, 3, 5_000, ["Món chính"], hours ?? []);

    private static MarketplaceMenuItemRow Item(long id, string name, int categoryId, string category, decimal price) =>
        new(id, 1, "Xôi Bà Tư", name, null, null, price, "AVAILABLE", categoryId, category);
}
