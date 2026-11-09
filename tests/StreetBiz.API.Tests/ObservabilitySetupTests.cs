using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Trace;
using StreetBiz.API.Extensions;

namespace StreetBiz.API.Tests;

/// <summary>Application Insights is opt-in: nothing changes until a connection string is set.</summary>
public sealed class ObservabilitySetupTests
{
    // The documented placeholder format; the key is all zeros and the endpoint is never called.
    private const string FakeConnectionString =
        "InstrumentationKey=00000000-0000-0000-0000-000000000000;IngestionEndpoint=https://localhost/";

    private static IConfiguration Config(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Without_a_connection_string_nothing_is_registered(string? value)
    {
        var services = new ServiceCollection();

        var added = services.AddAzureMonitorIfConfigured(Config((ObservabilitySetup.ConnectionStringKey, value)));

        Assert.False(added);
        using var provider = services.BuildServiceProvider();
        Assert.Null(provider.GetService<TracerProvider>());
    }

    [Fact]
    public void A_connection_string_turns_tracing_on()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        var added = services.AddAzureMonitorIfConfigured(Config((ObservabilitySetup.ConnectionStringKey, FakeConnectionString)));

        Assert.True(added);
        using var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetService<TracerProvider>());
    }

    [Theory]
    [InlineData("-0.1")]
    [InlineData("1.5")]
    public void A_sampling_ratio_outside_zero_to_one_is_refused_at_startup(string ratio)
    {
        var services = new ServiceCollection();

        Assert.Throws<InvalidOperationException>(() => services.AddAzureMonitorIfConfigured(Config(
            (ObservabilitySetup.ConnectionStringKey, FakeConnectionString),
            (ObservabilitySetup.SamplingRatioKey, ratio))));
    }
}
