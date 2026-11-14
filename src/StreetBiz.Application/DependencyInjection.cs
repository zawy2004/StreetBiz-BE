using FluentValidation;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using StreetBiz.Application.Common.Behaviors;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.DTOs.Commerce;
using StreetBiz.Application.Features.Chat;
using StreetBiz.Application.Features.Commerce;
using StreetBiz.Application.Features.Commerce.OrderTracking;
using StreetBiz.Application.Features.WardSlots;

namespace StreetBiz.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        var assembly = typeof(DependencyInjection).Assembly;

        services.AddMediatR(configuration =>
            configuration.RegisterServicesFromAssembly(assembly));
        services.AddValidatorsFromAssembly(assembly);
        UseVietnameseValidationMessages();
        services.AddAutoMapper(_ => { }, assembly);

        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));
        // After validation (registration order is run order): only the two commands that create an order.
        services.AddTransient<
            IPipelineBehavior<CheckoutOrderCommand, CheckoutDto>,
            PickupRangeBehavior<CheckoutOrderCommand, CheckoutDto>>();
        services.AddTransient<
            IPipelineBehavior<PlacePrepaidOrderCommand, OrderDto>,
            PickupRangeBehavior<PlacePrepaidOrderCommand, OrderDto>>();
        services.AddScoped<IAuthTokenIssuer, AuthTokenIssuer>();
        services.AddScoped<IVendorContext, VendorContext>();
        services.AddScoped<ICustomerContext, CustomerContext>();
        services.AddScoped<IChatParticipantResolver, ChatParticipantResolver>();
        services.AddScoped<IPlatformAdminContext, PlatformAdminContext>();
        services.AddScoped<IContentReporterContext, ContentReporterContext>();
        services.AddScoped<IWardActorResolver, WardActorResolver>();
        services.AddScoped<IWardActorContext, WardActorContext>();
        // Payments straight to a payee's own payOS channel (the ward for fees, the household for orders).
        services.AddScoped<StreetBiz.Application.Features.PayoutAccounts.PayoutAccountService>();
        services.AddScoped<StreetBiz.Application.Features.PayoutAccounts.PayeeChannelFailures>();
        services.AddScoped<StreetBiz.Application.Features.PayoutAccounts.PayeeCheckouts>();

        return services;
    }

    /// <summary>
    /// Rules without their own <c>WithMessage</c> (and the first rules of a chain whose
    /// <c>WithMessage</c> only covers the last one) fall back to FluentValidation's built-in text.
    /// Use its Vietnamese set so such a message never reaches a user in English, whatever the
    /// server's culture. Messages written in the validators are unaffected.
    /// </summary>
    public static void UseVietnameseValidationMessages() =>
        ValidatorOptions.Global.LanguageManager.Culture = new System.Globalization.CultureInfo("vi");
}
