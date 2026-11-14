using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StreetBiz.Application.Common.Interfaces;

namespace StreetBiz.Infrastructure.Storage;

public static class FileStorageRegistration
{
    /// <summary>
    /// Registers <see cref="IFileStorage"/> (uploads on disk under <c>Storage:RootPath</c>), the photo
    /// re-encoder and the sweep that deletes public photos nothing references.
    /// </summary>
    public static IServiceCollection AddFileStorage(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<StorageSettings>(configuration.GetSection(StorageSettings.SectionName));
        services.AddSingleton<IFileStorage, LocalFileStorage>();
        services.AddSingleton<IImageProcessor, ImageSharpImageProcessor>();

        services.Configure<OrphanImageCleanupOptions>(configuration.GetSection(OrphanImageCleanupOptions.SectionName));
        services.AddScoped<OrphanImageSweeper>();
        services.AddHostedService<OrphanImageCleanupService>();
        return services;
    }
}
