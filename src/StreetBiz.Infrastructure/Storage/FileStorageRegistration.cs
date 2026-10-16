using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StreetBiz.Application.Common.Interfaces;

namespace StreetBiz.Infrastructure.Storage;

public static class FileStorageRegistration
{
    /// <summary>
    /// Registers <see cref="IFileStorage"/> for <c>Storage:Provider</c> (disk by default, Azure Blob
    /// on request). A bad or half-filled setting stops the app at startup, not at the first upload.
    /// </summary>
    public static IServiceCollection AddFileStorage(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(StorageSettings.SectionName);
        var settings = section.Get<StorageSettings>() ?? new StorageSettings();
        settings.Validate();

        services.Configure<StorageSettings>(section);
        if (settings.UsesAzureBlob)
        {
            services.AddSingleton<IFileStorage, AzureBlobFileStorage>();
        }
        else
        {
            services.AddSingleton<IFileStorage, LocalFileStorage>();
        }

        return services;
    }
}
