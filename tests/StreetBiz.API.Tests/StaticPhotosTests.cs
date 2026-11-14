using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StreetBiz.API.Extensions;

namespace StreetBiz.API.Tests;

/// <summary>The illustrative photos the web and the app load from the API instead of bundling them.</summary>
public sealed class StaticPhotosTests : IDisposable
{
    private const string Key = "test-signing-key-test-signing-key-test-signing-key-0123456789";
    private readonly WebApplicationFactory<Program> host = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:StreetBizDatabase", "Server=unused;Database=unused;Integrated Security=True");
        builder.UseSetting("Jwt:SigningKey", Key);
        builder.UseSetting("Jwt:Issuer", "StreetBiz");
        builder.UseSetting("Jwt:Audience", "StreetBiz");
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:StreetBizDatabase"] = "Server=unused;Database=unused;Integrated Security=True",
            ["Jwt:SigningKey"] = Key,
            ["Jwt:Issuer"] = "StreetBiz",
            ["Jwt:Audience"] = "StreetBiz",
        }));
        builder.ConfigureServices(services =>
        {
            foreach (var hosted in services.Where(d => d.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService)
                         && d.ImplementationType?.Assembly.GetName().Name?.StartsWith("StreetBiz", StringComparison.Ordinal) == true).ToList())
            {
                services.Remove(hosted);
            }
        });
    });

    public void Dispose() => host.Dispose();

    [Theory]
    [InlineData("/static/food/pho.jpg", "image/jpeg")]
    [InlineData("/static/food/banh-mi.jpg", "image/jpeg")]
    [InlineData("/static/hero/hanoi-street-960.webp", "image/webp")]
    public async Task Photos_are_public_cached_and_embeddable_from_another_origin(string path, string type)
    {
        using var client = host.CreateClient(new() { BaseAddress = new("https://localhost") });

        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(type, response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(StaticPhotos.CacheControl, response.Headers.CacheControl?.ToString());
        Assert.Equal("cross-origin", response.Headers.GetValues("Cross-Origin-Resource-Policy").Single());
        Assert.True((await response.Content.ReadAsByteArrayAsync()).Length > 1000);
    }

    [Theory]
    [InlineData("/static/food/CREDITS.md")]
    [InlineData("/static/food/missing.jpg")]
    [InlineData("/static/../appsettings.json")]
    public async Task Only_existing_image_files_are_served(string path)
    {
        using var client = host.CreateClient(new() { BaseAddress = new("https://localhost") });

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(path)).StatusCode);
    }
}
