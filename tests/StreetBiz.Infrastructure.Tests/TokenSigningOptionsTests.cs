using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using StreetBiz.Infrastructure;
using StreetBiz.Infrastructure.Security;

namespace StreetBiz.Infrastructure.Tests;

public sealed class TokenSigningOptionsTests
{
    private const string GoodKey = "0123456789abcdef0123456789abcdef";

    private static ServiceProvider Build(string environment, string? permitKey, string? pickupKey)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Permit:SigningKey"] = permitKey,
                ["OrderPickup:SigningKey"] = pickupKey,
            })
            .Build();
        var services = new ServiceCollection();
        services.AddSingleton<IHostEnvironment>(new Env(environment));
        services.AddTokenSigningOptions(configuration);
        return services.BuildServiceProvider();
    }

    [Theory]
    [InlineData(null, GoodKey, "Permit:SigningKey")]
    [InlineData("", GoodKey, "Permit:SigningKey")]
    [InlineData("too-short", GoodKey, "Permit:SigningKey")]
    [InlineData(GoodKey, null, "OrderPickup:SigningKey")]
    public void Production_refuses_a_missing_or_short_signing_key(string? permit, string? pickup, string named)
    {
        using var provider = Build(Environments.Production, permit, pickup);
        var error = Assert.Throws<InvalidOperationException>(() =>
        {
            _ = provider.GetRequiredService<IOptions<PermitSettings>>().Value;
            _ = provider.GetRequiredService<IOptions<OrderPickupSettings>>().Value;
        });
        Assert.Contains(named, error.Message);
    }

    [Fact]
    public void Production_accepts_real_keys()
    {
        using var provider = Build(Environments.Production, GoodKey, GoodKey);
        Assert.Equal(GoodKey, provider.GetRequiredService<IOptions<PermitSettings>>().Value.SigningKey);
        Assert.Equal(GoodKey, provider.GetRequiredService<IOptions<OrderPickupSettings>>().Value.SigningKey);
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Testing")]
    public void Local_runs_still_start_without_keys(string environment)
    {
        using var provider = Build(environment, null, null);
        Assert.Equal(string.Empty, provider.GetRequiredService<IOptions<PermitSettings>>().Value.SigningKey);
    }

    [Fact]
    public void The_check_runs_at_startup_not_on_the_first_scan()
    {
        // ValidateOnStart registers a startup validator; the host runs it before serving.
        using var provider = Build(Environments.Production, null, GoodKey);
        var validator = provider.GetRequiredService<IStartupValidator>();
        Assert.Throws<InvalidOperationException>(validator.Validate);
    }

    private sealed class Env(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "StreetBiz.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
