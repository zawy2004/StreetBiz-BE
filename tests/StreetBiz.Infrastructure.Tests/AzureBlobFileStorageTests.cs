using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Infrastructure.Storage;

namespace StreetBiz.Infrastructure.Tests;

public sealed class AzureBlobFileStorageTests
{
    [Theory]
    [InlineData("slots/5/abc.jpg", "slots/5/abc.jpg")]
    [InlineData("slots\\5\\abc.jpg", "slots/5/abc.jpg")]
    [InlineData("/slots//5/abc.jpg", "slots/5/abc.jpg")]
    public void Blob_name_is_the_relative_path_with_forward_slashes(string path, string expected)
    {
        AzureBlobFileStorage.BlobName(path).Should().Be(expected);
    }

    [Theory]
    [InlineData("../secret.jpg")]
    [InlineData("slots/../../secret.jpg")]
    [InlineData("./a.jpg")]
    [InlineData("")]
    [InlineData("///")]
    public void Blob_name_refuses_paths_that_leave_the_folder(string path)
    {
        var act = () => AzureBlobFileStorage.BlobName(path);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Container_client_needs_a_connection_string_or_an_account_url()
    {
        var act = () => AzureBlobFileStorage.CreateContainerClient(new AzureBlobSettings());

        act.Should().Throw<InvalidOperationException>().WithMessage("*ConnectionString or AccountUrl*");
    }

    [Fact]
    public void Container_client_needs_a_container_name()
    {
        var settings = new AzureBlobSettings { ConnectionString = "UseDevelopmentStorage=true", ContainerName = " " };

        var act = () => AzureBlobFileStorage.CreateContainerClient(settings);

        act.Should().Throw<InvalidOperationException>().WithMessage("*ContainerName*");
    }

    [Fact]
    public void Container_client_is_built_from_a_connection_string_without_touching_the_network()
    {
        var client = AzureBlobFileStorage.CreateContainerClient(
            new AzureBlobSettings { ConnectionString = "UseDevelopmentStorage=true", ContainerName = "uploads" });

        client.Name.Should().Be("uploads");
    }

    [Fact]
    public void Container_client_is_built_from_an_account_url_for_managed_identity()
    {
        var client = AzureBlobFileStorage.CreateContainerClient(
            new AzureBlobSettings { AccountUrl = "https://acct.blob.core.windows.net/", ContainerName = "uploads" });

        client.Uri.ToString().Should().Be("https://acct.blob.core.windows.net/uploads");
    }

    [Fact]
    public void Disk_is_the_default_provider()
    {
        var settings = new StorageSettings();

        settings.UsesAzureBlob.Should().BeFalse();
        settings.Validate();
    }

    [Fact]
    public void Validate_rejects_an_unknown_provider()
    {
        var act = () => new StorageSettings { Provider = "S3" }.Validate();

        act.Should().Throw<InvalidOperationException>().WithMessage("*Provider*");
    }

    [Fact]
    public void Validate_rejects_azure_without_credentials()
    {
        var act = () => new StorageSettings { Provider = "AzureBlob" }.Validate();

        act.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData("AzureBlob", typeof(AzureBlobFileStorage))]
    [InlineData("azureblob", typeof(AzureBlobFileStorage))]
    [InlineData("Local", typeof(LocalFileStorage))]
    public void Provider_setting_picks_the_storage_implementation(string provider, Type expected)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:StreetBiz"] = "Server=localhost;Database=x;Trusted_Connection=True;",
                ["Storage:Provider"] = provider,
                ["Storage:RootPath"] = Path.Combine(Path.GetTempPath(), "streetbiz-storage-test"),
                ["Storage:AzureBlob:ConnectionString"] = "UseDevelopmentStorage=true",
            })
            .Build();
        var services = new ServiceCollection();
        services.AddFileStorage(configuration);

        using var serviceProvider = services.BuildServiceProvider();
        serviceProvider.GetRequiredService<IFileStorage>().Should().BeOfType(expected);
    }
}
