using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;

namespace StreetBiz.API.Extensions;

/// <summary>
/// The illustrative photos the web and mobile apps show when a stall or dish has no photo of its
/// own (the dish stock photos and the street hero), served by the API from
/// <c>wwwroot/static</c> as <c>/static/food/{key}.jpg</c> and <c>/static/hero/{name}.webp</c>.
/// They ship in the API image, so every client loads the same files from the VPS instead of
/// bundling a copy. Only image types are served; <c>CREDITS.md</c> stays in the folder for licences.
/// </summary>
public static class StaticPhotos
{
    public const string RequestPath = "/static";

    /// <summary>The files are replaced only under a new name, so a month in the browser is safe.</summary>
    public const string CacheControl = "public, max-age=2592000";

    public static IApplicationBuilder UseStaticPhotos(this WebApplication app)
    {
        var root = Path.Combine(app.Environment.ContentRootPath, "wwwroot", "static");
        if (!Directory.Exists(root))
        {
            return app;
        }

        return app.UseStaticFiles(new StaticFileOptions
        {
            RequestPath = RequestPath,
            FileProvider = new PhysicalFileProvider(root),
            // Photos only: the licence notes (.md) and anything else in the folder answer 404.
            ContentTypeProvider = new FileExtensionContentTypeProvider(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [".jpg"] = "image/jpeg",
                [".png"] = "image/png",
                [".webp"] = "image/webp",
            }),
            ServeUnknownFileTypes = false,
            OnPrepareResponse = context =>
            {
                var headers = context.Context.Response.Headers;
                headers.CacheControl = CacheControl;
                // Shown on the Vercel site and in the app, so another origin may embed them.
                headers["Cross-Origin-Resource-Policy"] = "cross-origin";
            },
        });
    }
}
