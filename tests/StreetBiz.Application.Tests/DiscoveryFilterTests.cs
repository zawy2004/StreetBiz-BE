using FluentAssertions;
using Moq;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Features.Commerce;

namespace StreetBiz.Application.Tests;

/// <summary>DISC-05: the five criteria of the SRS (category, price, rating, distance, open status) reach the repository.</summary>
public sealed class DiscoveryFilterTests
{
    private static readonly DateTime SaturdayMorning = new(2026, 9, 19, 3, 0, 0, DateTimeKind.Utc);

    private static MarketplaceStorefrontRow Store(long id, string name, decimal? minPrice, decimal? rating = null) => new(
        id, name, null, null, 100 + id, null, 1003, "Nam Dương", "NVL", $"S-{id}", 16.06m, 108.214m,
        rating, rating is null ? 0 : 3, 3, minPrice, ["Món chính"], []);

    private static (ListStorefrontsQueryHandler Handler, Func<MarketplaceStorefrontFilter?> Sent) Setup(params MarketplaceStorefrontRow[] rows)
    {
        MarketplaceStorefrontFilter? sent = null;
        var repository = new Mock<ICommerceRepository>();
        repository.Setup(x => x.ListStorefrontsAsync(It.IsAny<MarketplaceStorefrontFilter>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback<MarketplaceStorefrontFilter, int, CancellationToken>((f, _, _) => sent = f)
            .ReturnsAsync(rows);
        var clock = new Mock<IDateTimeProvider>();
        clock.SetupGet(x => x.UtcNow).Returns(SaturdayMorning);
        return (new ListStorefrontsQueryHandler(repository.Object, clock.Object), () => sent);
    }

    [Fact]
    public async Task The_minimum_rating_is_handed_to_the_repository_for_storefronts()
    {
        var (handler, sent) = Setup(Store(1, "A", 20_000, 4.5m));

        await handler.Handle(new ListStorefrontsQuery(MinRating: 4m), default);

        sent()!.MinRating.Should().Be(4m);
    }

    [Fact]
    public async Task The_minimum_rating_is_handed_to_the_repository_for_dishes()
    {
        MarketplaceMenuFilter? sent = null;
        var repository = new Mock<ICommerceRepository>();
        repository.Setup(x => x.SearchMenuItemsAsync(It.IsAny<MarketplaceMenuFilter>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback<MarketplaceMenuFilter, int, CancellationToken>((f, _, _) => sent = f)
            .ReturnsAsync([]);
        var clock = new Mock<IDateTimeProvider>();
        clock.SetupGet(x => x.UtcNow).Returns(SaturdayMorning);

        await new SearchMarketplaceMenuQueryHandler(repository.Object, clock.Object)
            .Handle(new SearchMarketplaceMenuQuery(null, MinRating: 4.5m), default);

        sent!.MinRating.Should().Be(4.5m);
    }

    [Fact]
    public async Task A_price_cap_keeps_storefronts_with_at_least_one_dish_at_or_under_it()
    {
        var (handler, _) = Setup(
            Store(1, "Cheap", 20_000),
            Store(2, "Exactly", 50_000),
            Store(3, "Dear", 80_000),
            Store(4, "No menu", null));

        var result = await handler.Handle(new ListStorefrontsQuery(MaxPrice: 50_000), default);

        result.Select(s => s.StorefrontName).Should().Equal("Cheap", "Exactly");
    }

    [Fact]
    public async Task Without_a_price_cap_every_storefront_stays()
    {
        var (handler, _) = Setup(Store(1, "Cheap", 20_000), Store(4, "No menu", null));

        (await handler.Handle(new ListStorefrontsQuery(), default)).Should().HaveCount(2);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(1.0, true)]
    [InlineData(4.5, true)]
    [InlineData(5.0, true)]
    [InlineData(0.0, false)]
    [InlineData(0.5, false)]
    [InlineData(5.1, false)]
    public void A_rating_filter_is_between_one_and_five_stars(double? rating, bool valid)
    {
        var value = rating is null ? (decimal?)null : (decimal)rating;

        new ListStorefrontsQueryValidator().Validate(new ListStorefrontsQuery(MinRating: value)).IsValid.Should().Be(valid);
        new SearchMarketplaceMenuQueryValidator().Validate(new SearchMarketplaceMenuQuery(null, MinRating: value)).IsValid.Should().Be(valid);
    }

    [Fact]
    public void A_negative_price_cap_is_refused()
    {
        new ListStorefrontsQueryValidator().Validate(new ListStorefrontsQuery(MaxPrice: -1)).IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task All_five_criteria_can_be_combined_in_one_request()
    {
        var (handler, sent) = Setup(Store(1, "Match", 30_000, 4.8m));

        var result = await handler.Handle(
            new ListStorefrontsQuery(
                CategoryId: 3, OpenNow: true, Latitude: 16.06m, Longitude: 108.214m, RadiusMeters: 2_000,
                MinRating: 4m, MaxPrice: 50_000),
            default);

        result.Should().ContainSingle();
        var filter = sent()!;
        filter.CategoryId.Should().Be(3);        // category
        filter.OpenAt.Should().NotBeNull();      // open status
        filter.Area.Should().NotBeNull();        // distance
        filter.MinRating.Should().Be(4m);        // rating
        // price is applied to the rows the repository returned (30k <= 50k).
    }
}
