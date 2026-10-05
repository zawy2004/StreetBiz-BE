using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.Features.VendorKyc;
using StreetBiz.Application.Features.WardCompliance;
using StreetBiz.Application.Features.WardSlots;
using StreetBiz.Infrastructure.Common;
using StreetBiz.Infrastructure.Geocoding;
using StreetBiz.Infrastructure.Identity;
using StreetBiz.Infrastructure.Notifications;
using StreetBiz.Infrastructure.Payments;
using StreetBiz.Infrastructure.Persistence;
using StreetBiz.Infrastructure.Persistence.Repositories;
using StreetBiz.Infrastructure.Security;
using StreetBiz.Infrastructure.Services;
using StreetBiz.Infrastructure.Storage;

namespace StreetBiz.Infrastructure;

public static class DependencyInjection
{
    private const string EnvironmentVariableName = "STREETBIZ_DB_CONNECTION";
    private const string ConnectionStringName = "StreetBizDB";
    private const string LegacyConnectionStringName = "StreetBizDatabase";

    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetStreetBizDatabaseConnectionString();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IGeolocation, WardGeolocation>();
        services.AddScoped<WardSlots>();
        services.AddScoped<IWardSlots>(provider => provider.GetRequiredService<WardSlots>());
        services.AddScoped<IWardComplianceService, WardComplianceService>();
        services.AddScoped<StreetBiz.Application.Features.WardConfiguration.IWardConfigurationService, WardConfigurationService>();

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
        services.Configure<PaymentGatewaySettings>(
            configuration.GetSection(PaymentGatewaySettings.SectionName));

        services.AddSingleton<IDateTimeProvider, DateTimeProvider>();
        services.AddSingleton<IPasswordHasher, PasswordHasher>();
        services.AddSingleton<IJwtTokenService, JwtTokenService>();
        services.Configure<SmsSettings>(configuration.GetSection(SmsSettings.SectionName));
        services.AddHttpClient<HttpSmsSender>(client => client.Timeout = TimeSpan.FromSeconds(15));
        // OTP codes must never reach production logs: the logging sender is Development-only,
        // and any other environment refuses to start without a real provider and an OTP hash key.
        services.AddSingleton<ISmsSender>(sp =>
        {
            var env = sp.GetRequiredService<IHostEnvironment>();
            var sms = sp.GetRequiredService<IOptions<SmsSettings>>().Value;
            if (env.IsDevelopment() && !string.Equals(sms.Provider, "Http", StringComparison.OrdinalIgnoreCase))
            {
                return ActivatorUtilities.CreateInstance<LoggingSmsSender>(sp);
            }

            if (!string.Equals(sms.Provider, "Http", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(sms.Endpoint))
            {
                throw new InvalidOperationException(
                    "Sms:Provider must be \"Http\" with Sms:Endpoint set outside Development.");
            }

            return sp.GetRequiredService<HttpSmsSender>();
        });
        services.AddOptions<OtpSettings>()
            .Bind(configuration.GetSection(OtpSettings.SectionName))
            .PostConfigure<IHostEnvironment>((settings, env) =>
            {
                if (!string.IsNullOrWhiteSpace(settings.HashKey))
                {
                    return;
                }

                if (!env.IsDevelopment())
                {
                    throw new InvalidOperationException("Otp:HashKey must be configured (user-secrets / env var).");
                }

                settings.HashKey = "dev-only-otp-hash-key";
            });
        services.AddSingleton<IFileStorage, LocalFileStorage>();
        services.AddHttpClient(MomoGateway.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(30));
        services.AddSingleton<IPaymentGateway, ConfiguredPaymentGateway>();
        services.AddSingleton<IRefundGateway, ConfiguredRefundGateway>();

        services.AddScoped<IOtpService, OtpService>();
        services.AddScoped<ISecurityEvents, SecurityEvents>();
        services.AddScoped<IUserAccountRepository, UserAccountRepository>();
        services.AddScoped<ISessionRepository, SessionRepository>();
        services.AddScoped<IVendorRepository, VendorRepository>();
        services.AddScoped<IBusinessRegistrationRepository, BusinessRegistrationRepository>();
        services.AddScoped<IKycResultRepository, KycResultRepository>();
        services.AddScoped<IAdministrativeUnitRepository, AdministrativeUnitRepository>();
        services.AddScoped<ISidewalkSlotRepository, SidewalkSlotRepository>();
        services.AddScoped<ISidewalkZoneRepository, SidewalkZoneRepository>();
        services.AddScoped<ISlotHoldRepository, SlotHoldRepository>();
        services.AddScoped<IRentalApplicationRepository, RentalApplicationRepository>();
        services.AddScoped<IRentalContractRepository, RentalContractRepository>();
        services.AddScoped<IRenewalRequestRepository, RenewalRequestRepository>();
        services.AddScoped<IDigitalPermitRepository, DigitalPermitRepository>();
        services.AddScoped<IAddressChangeRequestRepository, AddressChangeRequestRepository>();
        services.AddScoped<ISlotTransferRequestRepository, SlotTransferRequestRepository>();
        services.AddScoped<ICommunityVendorRepository, CommunityVendorRepository>();
        services.AddScoped<IChatRepository, ChatRepository>();
        services.AddScoped<INotificationRepository, NotificationRepository>();
        services.AddScoped<IPlatformAdministrationRepository, PlatformAdministrationRepository>();
        services.AddScoped<IFinanceRepository, FinanceRepository>();
        services.AddScoped<IWardReportRepository, WardReportRepository>();
        services.Configure<FeeReminderOptions>(configuration.GetSection(FeeReminderOptions.SectionName));
        services.AddHostedService<FeeReminderHostedService>();
        services.AddScoped<ICommerceRepository, CommerceRepository>();
        services.AddScoped<StreetBiz.Application.Features.Commerce.ICommerceManagement, CommerceManagement>();
        services.AddScoped<StreetBiz.Application.Features.FoodSafety.IFoodSafetyService, FoodSafetyService>();
        services.AddScoped<StreetBiz.Application.Features.Commerce.IOrderPaymentTesting, OrderPaymentTesting>();

        services.Configure<PermitSettings>(configuration.GetSection(PermitSettings.SectionName));
        services.AddSingleton<IPermitTokenService, PermitTokenService>();
        services.Configure<OrderPickupSettings>(
            configuration.GetSection(OrderPickupSettings.SectionName));
        services.AddSingleton<IOrderPickupTokenService, OrderPickupTokenService>();

        services.Configure<Sidewalk.SidewalkSettings>(configuration.GetSection(Sidewalk.SidewalkSettings.SectionName));
        services.AddSingleton<ISidewalkPolicy, Sidewalk.SidewalkPolicy>();

        services.Configure<NominatimSettings>(configuration.GetSection(NominatimSettings.SectionName));
        services.AddMemoryCache();
        services.AddHttpClient<IGeocodingService, NominatimGeocodingService>((sp, client) =>
        {
            var settings = sp.GetRequiredService<IOptions<NominatimSettings>>().Value;
            client.BaseAddress = new Uri(settings.BaseUrl.TrimEnd('/') + "/");
            client.Timeout = TimeSpan.FromSeconds(settings.TimeoutSeconds);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(settings.UserAgent);
        });

        // Key comes from configuration (dotnet user-secrets / env var) only --
        // never hardcode AiCompliance:Gemini:ApiKey/FptAi:ApiKey/Groq:ApiKeys in
        // appsettings*.json. A prior draft of this feature did exactly that and
        // leaked live keys twice.
        //
        // AiKeyPools MUST be Singleton: AddHttpClient's typed client is Transient
        // by default (a fresh AiComplianceService per DI resolution, effectively
        // per request), so a key-rotation counter built inline in its constructor
        // would reset every request and never actually round-robin across calls.
        services.AddSingleton<AiKeyPools>();
        // Shared by AiComplianceService and FptAiKycService's Gemini fallback -- see
        // GeminiVisionClient's remarks for why this was pulled out of AiComplianceService.
        services.AddHttpClient<GeminiVisionClient>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
        });
        services.AddHttpClient<IAiComplianceService, AiComplianceService>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
        });

        // REG-02 eKYC (CCCD OCR + Facematch). Same rule as above: the FPT.AI keys come from
        // user-secrets / env vars, never from a checked-in appsettings file.
        services.AddHttpClient<IKycVerificationService, FptAiKycService>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
        });

        return services;
    }

    public static string GetStreetBizDatabaseConnectionString(
        this IConfiguration configuration)
    {
        var connectionString =
            Environment.GetEnvironmentVariable(EnvironmentVariableName);

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            connectionString = configuration.GetConnectionString(ConnectionStringName)
                ?? configuration.GetConnectionString(LegacyConnectionStringName);
        }

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"Database connection is not configured. Set {EnvironmentVariableName} " +
                $"or ConnectionStrings:{ConnectionStringName} " +
                $"(legacy: ConnectionStrings:{LegacyConnectionStringName}).");
        }

        return connectionString;
    }
}
