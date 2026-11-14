using System.Security.Cryptography;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using StreetBiz.Infrastructure.Payments;
using StreetBiz.Infrastructure.Security;

namespace StreetBiz.Infrastructure.Tests;

/// <summary>The payees' payOS keys are encrypted at rest (AES-256-GCM) and any tampering is caught.</summary>
public sealed class CredentialProtectorTests
{
    private static readonly byte[] Key = [.. Enumerable.Range(1, 32).Select(i => (byte)i)];

    [Fact]
    public void A_key_comes_back_exactly_and_is_never_stored_in_clear()
    {
        var protector = new AesGcmCredentialProtector(Key);

        var stored = protector.Protect("checksum-key-123");

        Assert.StartsWith("v1:", stored);
        Assert.DoesNotContain("checksum-key-123", stored);
        Assert.Equal("checksum-key-123", protector.Unprotect(stored));
    }

    [Fact]
    public void The_same_key_encrypts_differently_every_time()
    {
        var protector = new AesGcmCredentialProtector(Key);

        Assert.NotEqual(protector.Protect("same"), protector.Protect("same"));
    }

    [Fact]
    public void A_changed_byte_or_another_key_is_refused()
    {
        var stored = new AesGcmCredentialProtector(Key).Protect("checksum-key-123");
        var bytes = Convert.FromBase64String(stored[3..]);
        bytes[^1] ^= 0x01;
        var tampered = "v1:" + Convert.ToBase64String(bytes);
        var otherKey = new AesGcmCredentialProtector([.. Enumerable.Repeat((byte)7, 32)]);

        Assert.ThrowsAny<CryptographicException>(() => new AesGcmCredentialProtector(Key).Unprotect(tampered));
        Assert.ThrowsAny<CryptographicException>(() => otherKey.Unprotect(stored));
        Assert.ThrowsAny<CryptographicException>(() => new AesGcmCredentialProtector(Key).Unprotect("plain-text"));
    }

    [Fact]
    public void Outside_development_nothing_is_protected_until_a_key_is_configured()
    {
        var production = new AesGcmCredentialProtector(Options.Create(new PaymentGatewaySettings()), new Env("Production"));
        var development = new AesGcmCredentialProtector(Options.Create(new PaymentGatewaySettings()), new Env("Development"));

        Assert.False(production.IsConfigured);
        Assert.Throws<InvalidOperationException>(() => production.Protect("x"));
        Assert.True(development.IsConfigured);
        Assert.Equal("x", development.Unprotect(development.Protect("x")));
    }

    [Fact]
    public void A_configured_key_is_used_and_must_be_32_bytes_of_base64()
    {
        var configured = new PaymentGatewaySettings { PayOs = new PayOsSettings { CredentialKey = Convert.ToBase64String(Key) } };
        var fromConfig = new AesGcmCredentialProtector(Options.Create(configured), new Env("Production"));

        Assert.Equal("x", new AesGcmCredentialProtector(Key).Unprotect(fromConfig.Protect("x")));
        Assert.Throws<InvalidOperationException>(() => new AesGcmCredentialProtector(
            Options.Create(new PaymentGatewaySettings { PayOs = new PayOsSettings { CredentialKey = "short" } }), new Env("Production")));
        Assert.Throws<InvalidOperationException>(() => new AesGcmCredentialProtector(
            Options.Create(new PaymentGatewaySettings { PayOs = new PayOsSettings { CredentialKey = Convert.ToBase64String(new byte[16]) } }),
            new Env("Production")));
    }

    private sealed class Env(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "test";
        public string ContentRootPath { get; set; } = ".";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
