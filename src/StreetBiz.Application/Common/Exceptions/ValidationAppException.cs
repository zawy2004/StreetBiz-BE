namespace StreetBiz.Application.Common.Exceptions;

/// <summary>400 - input validation failures aggregated from FluentValidation.</summary>
public sealed class ValidationAppException : AppException
{
    public ValidationAppException(IDictionary<string, string[]> errors)
        : base("One or more validation errors occurred.")
    {
        Errors = errors;
    }

    public IDictionary<string, string[]> Errors { get; }
    public override int StatusCode => 400;
    public override string ErrorCode => "validation_error";
}
