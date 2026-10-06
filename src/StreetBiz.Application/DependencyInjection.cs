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
        services.AddScoped<IWardActorResolver, WardActorResolver>();
        services.AddScoped<IWardActorContext, WardActorContext>();

        return services;
    }
}
