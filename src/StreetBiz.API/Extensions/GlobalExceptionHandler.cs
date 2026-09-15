using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using StreetBiz.Application.Common.Exceptions;

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
                await WriteAsync(httpContext, new ProblemDetails
                {
                    Status = app.StatusCode,
                    Title = app.ErrorCode,
                    Detail = app.Message,
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

    private static async Task WriteAsync(HttpContext httpContext, ProblemDetails problem)
    {
        httpContext.Response.StatusCode = problem.Status ?? StatusCodes.Status500InternalServerError;
        await httpContext.Response.WriteAsJsonAsync(problem);
    }
}
