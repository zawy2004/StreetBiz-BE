using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Security;

namespace StreetBiz.API.Extensions;

/// <summary>
/// Maps application exceptions to RFC-7807 ProblemDetails responses.
/// This is the only place exceptions are logged: appsettings.json silences ASP.NET's own
/// ExceptionHandlerMiddleware log, which would otherwise print a full stack trace for
/// every expected 4xx (wrong role, not found, validation).
/// </summary>
public sealed class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is AppException expected)
        {
            // Expected outcomes, not faults: one line, no stack trace.
            logger.LogInformation(
                "{Method} {Path} -> {StatusCode} {ErrorCode}: {Message}",
                httpContext.Request.Method,
                httpContext.Request.Path,
                expected.StatusCode,
                expected.ErrorCode,
                expected.Message);
        }

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
