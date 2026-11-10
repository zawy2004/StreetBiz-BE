using Microsoft.Extensions.Configuration;
using StreetBiz.Infrastructure.Services;

namespace StreetBiz.Infrastructure.Tests;

/// <summary>The switches behind AIP-01..04 and AIB-01: Ai:Enabled for all, Ai:Features:{code} for one.</summary>
public sealed class AiFeatureGateTests
{
    private static AiFeatureGate Gate(params (string Key, string Value)[] settings) =>
        new(new ConfigurationBuilder()
            .AddInMemoryCollection(settings.ToDictionary(s => s.Key, s => (string?)s.Value))
            .Build());

    [Fact]
    public void Every_feature_is_on_until_switched_off() =>
        Assert.True(Gate().IsEnabled("AIP-03"));

    [Fact]
    public void The_master_switch_turns_every_feature_off() =>
        Assert.False(Gate(("Ai:Enabled", "false")).IsEnabled("AIB-01"));

    [Fact]
    public void One_feature_can_be_turned_off_on_its_own()
    {
        var gate = Gate(("Ai:Features:AIP-02", "false"));

        Assert.False(gate.IsEnabled("AIP-02"));
        Assert.True(gate.IsEnabled("AIP-01"));
    }

    [Fact]
    public void A_feature_stays_off_when_the_master_switch_is_off_even_if_it_is_listed_on() =>
        Assert.False(Gate(("Ai:Enabled", "false"), ("Ai:Features:AIP-01", "true")).IsEnabled("AIP-01"));
}
