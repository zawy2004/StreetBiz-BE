using System.IO.Compression;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.AspNetCore.ResponseCompression;

namespace StreetBiz.API.Extensions;

/// <summary>Output-cache policy and tag names shared by the endpoints that use them.</summary>
public static class PublicCache
{
    /// <summary>Food categories, service areas, wards: change only when an administrator edits them.</summary>
    public const string Reference = "PublicReference";

    /// <summary>Storefronts, menu items, the community vendor map and public profiles.</summary>
    public const string Listing = "PublicListing";

    public const string ReferenceTag = "public-reference";
    public const string MarketplaceTag = "public-marketplace";
    public const string CommunityTag = "public-community";

    public static readonly TimeSpan ReferenceDuration = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan ListingDuration = TimeSpan.FromSeconds(30);
}

/// <summary>
/// Output caching and response compression for the API.
/// <para>
/// Only public, read-only endpoints whose answer does not depend on who asks are cached (their
/// handlers and repositories read no current user). The cache is kept per instance and short: 30 s
/// for listings that move with opening hours and sales, 5 min for reference lists, and the writes that
/// change them evict the matching tag at once (<see cref="EvictsPublicCacheAttribute"/>).
/// </para>
/// <para>
/// Compression covers JSON and text, but not sign-in endpoints (BREACH: tokens in the body), the
/// chatbot (streamed answers must not be buffered) or SignalR hubs.
/// </para>
/// </summary>
public static class HttpPerformanceSetup
{
    private static readonly string[] Uncompressed = ["/api/auth", "/api/chatbot", "/hubs"];

    public static IServiceCollection AddHttpPerformance(this IServiceCollection services)
    {
        services.AddOutputCache(options =>
        {
            // Bounded for a small App Service plan (the default is 100 MB); the least used entries go first.
            options.SizeLimit = 32 * 1024 * 1024;
            // A listing is a few hundred KB at most; anything bigger is not worth holding.
            options.MaximumBodySize = 1024 * 1024;
            options.AddPolicy(PublicCache.Reference, new PublicOutputCachePolicy(PublicCache.ReferenceDuration, PublicCache.ReferenceTag));
            options.AddPolicy(PublicCache.Listing, new PublicOutputCachePolicy(
                PublicCache.ListingDuration, PublicCache.MarketplaceTag, PublicCache.CommunityTag));
        });

        services.AddResponseCompression(options =>
        {
            // Azure terminates TLS in front of the app; every client request is HTTPS.
            options.EnableForHttps = true;
            options.Providers.Add<BrotliCompressionProvider>();
            options.Providers.Add<GzipCompressionProvider>();
        });
        services.Configure<BrotliCompressionProviderOptions>(o => o.Level = CompressionLevel.Fastest);
        services.Configure<GzipCompressionProviderOptions>(o => o.Level = CompressionLevel.Fastest);
        return services;
    }

    /// <summary>Compression early in the pipeline, skipping the paths listed above.</summary>
    public static IApplicationBuilder UseResponseCompressionExceptSensitive(this IApplicationBuilder app) =>
        app.UseWhen(
            context => !Uncompressed.Any(prefix => context.Request.Path.StartsWithSegments(prefix)),
            branch => branch.UseResponseCompression());
}

/// <summary>
/// Caches GET/HEAD 200 responses for every caller, signed in or not. The built-in default skips any
/// request with an Authorization header, which is every request the SPA makes once someone signs
/// in, so the public pages would never be cached for them. Use it only on endpoints whose response
/// is the same for everyone.
/// </summary>
public sealed class PublicOutputCachePolicy(TimeSpan duration, params string[] tags) : IOutputCachePolicy
{
    public ValueTask CacheRequestAsync(OutputCacheContext context, CancellationToken cancellation)
    {
        var request = context.HttpContext.Request;
        var cacheable = HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method);
        context.EnableOutputCaching = true;
        context.AllowCacheLookup = cacheable;
        context.AllowCacheStorage = cacheable;
        context.AllowLocking = true;
        context.ResponseExpirationTimeSpan = duration;
        context.CacheVaryByRules.QueryKeys = "*";
        foreach (var tag in tags)
        {
            context.Tags.Add(tag);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask ServeFromCacheAsync(OutputCacheContext context, CancellationToken cancellation) => ValueTask.CompletedTask;

    public ValueTask ServeResponseAsync(OutputCacheContext context, CancellationToken cancellation)
    {
        var response = context.HttpContext.Response;
        // Only a plain success, and never a response that sets a cookie.
        if (response.StatusCode != StatusCodes.Status200OK || response.Headers.ContainsKey("Set-Cookie"))
        {
            context.AllowCacheStorage = false;
        }

        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// After a successful write, evicts the public output-cache entries it changed, so a seller who edits
/// a dish or a customer who posts a rating sees the change at once instead of after the cache expires.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
public sealed class EvictsPublicCacheAttribute(string tag) : Attribute, IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var executed = await next();
        var request = context.HttpContext.Request;
        if (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method) || executed.Exception is not null)
        {
            return;
        }

        var status = executed.HttpContext.Response.StatusCode;
        var resultStatus = (executed.Result as Microsoft.AspNetCore.Mvc.Infrastructure.IStatusCodeActionResult)?.StatusCode ?? status;
        if (resultStatus is < 200 or > 299)
        {
            return;
        }

        var store = context.HttpContext.RequestServices.GetRequiredService<IOutputCacheStore>();
        await store.EvictByTagAsync(tag, context.HttpContext.RequestAborted);
    }
}
