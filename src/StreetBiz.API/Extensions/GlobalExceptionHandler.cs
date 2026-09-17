using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Security;

namespace StreetBiz.API.Extensions;

/// <summary>Maps application exceptions to RFC-7807 ProblemDetails responses.</summary>
public sealed class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        switch (exception)
        {
            case ValidationAppException validation:
                await WriteAsync(httpContext, new ValidationProblemDetails(validation.Errors)
                {
                    Status = validation.StatusCode,
                    Title = "One or more validation errors occurred.",
                    Type = validation.ErrorCode,
                });
                return true;

            case AppException app:
                var problem = new ProblemDetails
                {
                    Status = app.StatusCode,
                    Title = app.ErrorCode,
                    Type = app.ErrorCode,
                    Detail = app.Message,
                };

                if (app is TooManyRequestsException tooMany)
                {
                    httpContext.Response.Headers.RetryAfter = tooMany.RetryAfterSeconds.ToString();
                    problem.Extensions["retryAfterSeconds"] = tooMany.RetryAfterSeconds;
                }

                await WriteAsync(httpContext, problem);
                return true;

            // Kestrel rejects oversized bodies (e.g. an upload over the limit) with a 413;
            // report that status instead of turning it into a 500.
            case BadHttpRequestException badRequest:
                await WriteAsync(httpContext, new ProblemDetails
                {
                    Status = badRequest.StatusCode,
                    Title = "bad_request",
                    Type = "bad_request",
                    Detail = badRequest.StatusCode == StatusCodes.Status413PayloadTooLarge
                        ? EvidenceFiles.TooLarge
                        : "The request could not be read.",
                });
                return true;

            default:
                logger.LogError(exception, "Unhandled exception");
                await WriteAsync(httpContext, new ProblemDetails
                {
                    Status = StatusCodes.Status500InternalServerError,
                    Title = "server_error",
                    Detail = "An unexpected error occurred.",
                });
                return true;
        }
    }

    // Generic so the compile-time type is the concrete one: serializing a
    // ValidationProblemDetails through a ProblemDetails parameter would drop the
    // "errors" dictionary the client needs to highlight individual fields.
    private static async Task WriteAsync<TProblem>(HttpContext httpContext, TProblem problem)
        where TProblem : ProblemDetails
    {
        httpContext.Response.StatusCode = problem.Status ?? StatusCodes.Status500InternalServerError;
        await httpContext.Response.WriteAsJsonAsync(problem);
    }
}
