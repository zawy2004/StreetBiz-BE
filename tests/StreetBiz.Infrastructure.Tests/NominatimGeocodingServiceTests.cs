using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using StreetBiz.Infrastructure.Geocoding;

namespace StreetBiz.Infrastructure.Tests;

public sealed class NominatimGeocodingServiceTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(respond(request));
        }
    }

    private static NominatimGeocodingService BuildService(StubHandler handler, IMemoryCache? cache = null)
    {
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://nominatim.example.test/") };
        return new NominatimGeocodingService(client, cache ?? new MemoryCache(new MemoryCacheOptions()));
    }

    [Fact]
    public async Task A_successful_response_is_parsed_into_a_GeoPoint()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""[{"lat":"16.013200","lon":"108.239800","display_name":"Le Van Hien, Da Nang"}]"""),
        });
        var service = BuildService(handler);

        var result = await service.ForwardAsync("12 Le Van Hien, Da Nang", CancellationToken.None);

        result.Should().NotBeNull();
        result!.Latitude.Should().Be(16.0132m);
        result.Longitude.Should().Be(108.2398m);
        result.DisplayName.Should().Be("Le Van Hien, Da Nang");
    }

    [Fact]
    public async Task An_empty_result_array_returns_null_instead_of_throwing()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("[]"),
        });
        var service = BuildService(handler);

        var result = await service.ForwardAsync("an address nobody has", CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task A_non_success_status_returns_null_instead_of_throwing()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var service = BuildService(handler);

        var result = await service.ForwardAsync("12 Le Van Hien", CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task Blank_input_short_circuits_without_calling_the_provider()
    {
        var handler = new StubHandler(_ => throw new InvalidOperationException("should not be called"));
        var service = BuildService(handler);

        var result = await service.ForwardAsync("   ", CancellationToken.None);

        result.Should().BeNull();
        handler.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task A_second_call_for_the_same_address_is_served_from_cache_not_the_provider()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""[{"lat":"16.013200","lon":"108.239800","display_name":"X"}]"""),
        });
        var cache = new MemoryCache(new MemoryCacheOptions());
        var service = BuildService(handler, cache);

        await service.ForwardAsync("12 Le Van Hien", CancellationToken.None);
        await service.ForwardAsync("12 Le Van Hien", CancellationToken.None);

        handler.CallCount.Should().Be(1);
    }
}
