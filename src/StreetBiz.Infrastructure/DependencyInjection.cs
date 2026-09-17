using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Infrastructure.Common;
using StreetBiz.Infrastructure.Identity;
using StreetBiz.Infrastructure.Notifications;
using StreetBiz.Infrastructure.Persistence;
using StreetBiz.Infrastructure.Persistence.Repositories;
using StreetBiz.Infrastructure.Security;
using StreetBiz.Infrastructure.Storage;

namespace StreetBiz.Infrastructure;

public static class DependencyInjection
{
    private const string EnvironmentVariableName = "STREETBIZ_DB_CONNECTION";
    private const string ConnectionStringName = "StreetBizDB";

    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetStreetBizDatabaseConnectionString();

        services.AddDbContext<StreetBizDbContext>(options =>
        {
            options.UseSqlServer(
                connectionString,
                sqlOptions =>
                {
                    sqlOptions.EnableRetryOnFailure();
                });
        });

        services.Configure<JwtSettings>(configuration.GetSection(JwtSettings.SectionName));
        services.Configure<StorageSettings>(configuration.GetSection(StorageSettings.SectionName));

        services.AddSingleton<IDateTimeProvider, DateTimeProvider>();
        services.AddSingleton<IPasswordHasher, PasswordHasher>();
        services.AddSingleton<IJwtTokenService, JwtTokenService>();
        services.AddSingleton<ISmsSender, LoggingSmsSender>();
        services.AddSingleton<IFileStorage, LocalFileStorage>();

        services.AddScoped<IOtpService, OtpService>();
        services.AddScoped<IUserAccountRepository, UserAccountRepository>();
        services.AddScoped<ISessionRepository, SessionRepository>();
        services.AddScoped<IVendorRepository, VendorRepository>();
        services.AddScoped<IBusinessRegistrationRepository, BusinessRegistrationRepository>();
        services.AddScoped<IAdministrativeUnitRepository, AdministrativeUnitRepository>();
        services.AddScoped<ISidewalkSlotRepository, SidewalkSlotRepository>();
        services.AddScoped<IRentalApplicationRepository, RentalApplicationRepository>();
        services.AddScoped<IRentalContractRepository, RentalContractRepository>();
        services.AddScoped<IRenewalRequestRepository, RenewalRequestRepository>();

        services.Configure<Sidewalk.SidewalkSettings>(configuration.GetSection(Sidewalk.SidewalkSettings.SectionName));
        services.AddSingleton<ISidewalkPolicy, Sidewalk.SidewalkPolicy>();

        return services;
    }

    public static string GetStreetBizDatabaseConnectionString(
        this IConfiguration configuration)
    {
        var connectionString =
            Environment.GetEnvironmentVariable(EnvironmentVariableName);

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            connectionString = configuration.GetConnectionString(ConnectionStringName);
        }

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"Database connection is not configured. Set {EnvironmentVariableName} " +
                $"or ConnectionStrings:{ConnectionStringName}.");
        }

        return connectionString;
    }
}
