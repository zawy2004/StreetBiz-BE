using FluentValidation;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using StreetBiz.Application.Common.Behaviors;
using StreetBiz.Application.Common.Security;
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
        services.AddScoped<IAuthTokenIssuer, AuthTokenIssuer>();
        services.AddScoped<IVendorContext, VendorContext>();
        services.AddScoped<IWardActorResolver, WardActorResolver>();
        services.AddScoped<IWardActorContext, WardActorContext>();

        return services;
    }
}
