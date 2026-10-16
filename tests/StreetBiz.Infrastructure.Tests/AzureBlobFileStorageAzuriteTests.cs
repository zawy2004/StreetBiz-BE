using System.Text;
using Azure;
using FluentAssertions;
using StreetBiz.Infrastructure.Storage;

namespace StreetBiz.Infrastructure.Tests;

/// <summary>
/// Opt-in round trip against the Azurite emulator (docker run -p 10000:10000
/// mcr.microsoft.com/azure-storage/azurite azurite-blob --blobHost 0.0.0.0 --skipApiVersionCheck), then
/// set STREETBIZ_AZURITE_TEST=1. It never talks to a real Azure account.
/// </summary>
public sealed class AzureBlobFileStorageAzuriteTests
{
    public sealed class AzuriteFactAttribute : FactAttribute
    {
        public AzuriteFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("STREETBIZ_AZURITE_TEST") != "1")
            {
                Skip = "Opt-in: needs the Azurite blob emulator on 127.0.0.1:10000.";
            }
        }
    }

    private static AzureBlobFileStorage NewStorage() =>
        new(AzureBlobFileStorage.CreateContainerClient(new AzureBlobSettings
        {
            ConnectionString = "UseDevelopmentStorage=true",
            ContainerName = $"test-{Guid.NewGuid():N}",
        }));

    private static MemoryStream Bytes(string text) => new(Encoding.UTF8.GetBytes(text));

    [AzuriteFact]
    public async Task Saved_file_can_be_found_and_read_back_and_a_missing_one_is_null()
    {
        var storage = NewStorage();
        var path = $"slots/5/{Guid.NewGuid():N}.jpg";

        (await storage.ExistsAsync(path, default)).Should().BeFalse();
        (await storage.OpenReadAsync(path, default)).Should().BeNull();

        await storage.SaveAsync(path, Bytes("photo"), default);

        (await storage.ExistsAsync(path, default)).Should().BeTrue();
        await using var read = await storage.OpenReadAsync(path, default);
        using var reader = new StreamReader(read!);
        (await reader.ReadToEndAsync()).Should().Be("photo");
    }

    [AzuriteFact]
    public async Task Saving_the_same_path_twice_does_not_overwrite()
    {
        var storage = NewStorage();
        var path = $"evidence/6/{Guid.NewGuid():N}.png";
        await storage.SaveAsync(path, Bytes("first"), default);

        var again = () => storage.SaveAsync(path, Bytes("second"), default);

        await again.Should().ThrowAsync<RequestFailedException>();
        await using var read = await storage.OpenReadAsync(path, default);
        using var reader = new StreamReader(read!);
        (await reader.ReadToEndAsync()).Should().Be("first");
    }
}
