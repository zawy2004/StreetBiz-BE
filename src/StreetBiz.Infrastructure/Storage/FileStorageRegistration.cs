using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StreetBiz.Application.Common.Interfaces;

namespace StreetBiz.Infrastructure.Storage;

public static class FileStorageRegistration
{
    /// <summary>Registers <see cref="IFileStorage"/>: uploads are kept on disk under <c>Storage:RootPath</c>.</summary>
    public static IServiceCollection AddFileStorage(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<StorageSettings>(configuration.GetSection(StorageSettings.SectionName));
        services.AddSingleton<IFileStorage, LocalFileStorage>();
        return services;
    }
}
