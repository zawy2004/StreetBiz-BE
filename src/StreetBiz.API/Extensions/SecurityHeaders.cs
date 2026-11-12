namespace StreetBiz.API.Extensions;

/// <summary>
/// Browser security headers on every API response: JSON, uploaded files, errors, cached answers.
/// <para>
/// The API never serves a page, so its policy is the strictest there is: nothing may load, run or
/// frame anything (<c>default-src 'none'</c>), and no site may embed a response in a frame
/// (<c>frame-ancestors 'none'</c> plus <c>X-Frame-Options: DENY</c> against clickjacking). With
/// <c>nosniff</c> a browser never treats an uploaded file as HTML or script, whatever it contains;
/// together with the CSP, even a crafted file opened straight from the API cannot run code.
/// </para>
/// <para>
/// Images and files are still shown by the web app on its own origin: these headers govern what
/// a response may do when opened as a document, not whether another page may display it as an
/// image. Swagger UI (Development only) needs its scripts, so it is left out of the CSP.
/// </para>
/// </summary>
public static class SecurityHeaders
{
    public const string ContentSecurityPolicy =
        "default-src 'none'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'; sandbox";

    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) =>
        app.Use((context, next) =>
        {
            context.Response.OnStarting(() =>
            {
                var headers = context.Response.Headers;
                headers["X-Content-Type-Options"] = "nosniff";
                headers["X-Frame-Options"] = "DENY";
                headers["Referrer-Policy"] = "no-referrer";
                if (!context.Request.Path.StartsWithSegments("/swagger"))
                {
                    headers.ContentSecurityPolicy = ContentSecurityPolicy;
                }

                return Task.CompletedTask;
            });
            return next(context);
        });
}
