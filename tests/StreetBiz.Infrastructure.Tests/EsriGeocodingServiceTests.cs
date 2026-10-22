using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using StreetBiz.Infrastructure.Geocoding;

namespace StreetBiz.Infrastructure.Tests;

/// <summary>Esri address search (limited to Đà Nẵng) and the Esri/Nominatim fallback order.</summary>
public sealed class EsriGeocodingServiceTests
{
    private sealed class Stub(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> Calls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls.Add(request.RequestUri!);
            return Task.FromResult(respond(request));
        }
    }

    private const string ChoCon = """
        {"candidates":[
          {"address":"Chợ Cồn","score":97.5,"location":{"x":108.2141,"y":16.0679},"attributes":{"LongLabel":"Chợ Cồn, Hải Châu, Đà Nẵng, 50206, VNM"}},
          {"address":"Chợ Cồn, Hải Châu, Đà Nẵng, 50207","score":96,"location":{"x":108.2142,"y":16.068}},
          {"address":"Cồn Dầu","score":41,"location":{"x":108.2,"y":16.0}}]}
        """;

    private static HttpResponseMessage Ok(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json) };

    private static EsriGeocodingService Esri(HttpMessageHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri(EsriGeocodingService.BaseUrl) }, new MemoryCache(new MemoryCacheOptions()));

    [Fact]
    public async Task Searches_inside_Da_Nang_drops_postcodes_duplicates_and_weak_matches()
    {
        var handler = new Stub(_ => Ok(ChoCon));

        var points = await Esri(handler).SearchAsync("chợ Cồn, Đà Nẵng", 3, CancellationToken.None);

        points.Should().ContainSingle();
        points[0].DisplayName.Should().Be("Chợ Cồn, Hải Châu, Đà Nẵng");
        points[0].Latitude.Should().Be(16.0679m);
        var query = Uri.UnescapeDataString(handler.Calls.Single().Query);
        query.Should().Contain("searchExtent=107.85,15.9,108.35,16.2").And.Contain("countryCode=VNM");
    }

    [Fact]
    public async Task A_failed_search_is_an_empty_list_not_an_error()
    {
        var points = await Esri(new Stub(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError))).SearchAsync("x", 3, CancellationToken.None);
        points.Should().BeEmpty();
    }

    [Fact]
    public async Task Interactive_search_asks_Esri_first_and_Nominatim_only_when_Esri_finds_nothing()
    {
        var esriHandler = new Stub(_ => Ok("""{"candidates":[]}"""));
        var nominatimHandler = new Stub(_ => Ok("""[{"lat":"16.068","lon":"108.214","display_name":"Chợ Cồn"}]"""));
        var nominatim = new NominatimGeocodingService(
            new HttpClient(nominatimHandler) { BaseAddress = new Uri("https://nominatim.test/") }, new MemoryCache(new MemoryCacheOptions()));
        var geocoder = new FallbackGeocodingService(nominatim, Esri(esriHandler));

        var points = await geocoder.SearchAsync("chợ Cồn", 3, CancellationToken.None);

        points.Single().DisplayName.Should().Be("Chợ Cồn");
        esriHandler.Calls.Should().ContainSingle();
        nominatimHandler.Calls.Should().ContainSingle();
    }

    [Fact]
    public async Task A_stored_forward_lookup_keeps_Nominatim_first()
    {
        var esriHandler = new Stub(_ => Ok(ChoCon));
        var nominatimHandler = new Stub(_ => Ok("""[{"lat":"16.1","lon":"108.1","display_name":"OSM"}]"""));
        var nominatim = new NominatimGeocodingService(
            new HttpClient(nominatimHandler) { BaseAddress = new Uri("https://nominatim.test/") }, new MemoryCache(new MemoryCacheOptions()));

        var point = await new FallbackGeocodingService(nominatim, Esri(esriHandler)).ForwardAsync("12 Lê Duẩn", CancellationToken.None);

        point!.DisplayName.Should().Be("OSM");
        esriHandler.Calls.Should().BeEmpty();
    }
}
