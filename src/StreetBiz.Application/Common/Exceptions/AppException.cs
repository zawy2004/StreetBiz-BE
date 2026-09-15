namespace StreetBiz.Application.Common.Exceptions;

/// <summary>Base type for expected application errors mapped to HTTP responses.</summary>
public abstract class AppException(string message) : Exception(message)
{
    public abstract int StatusCode { get; }
    public abstract string ErrorCode { get; }
}

/// <summary>404 - a required record was not found.</summary>
public sealed class NotFoundException(string message) : AppException(message)
{
    public override int StatusCode => 404;
    public override string ErrorCode => "not_found";
}

/// <summary>409 - a uniqueness or state conflict (e.g. duplicate phone).</summary>
public sealed class ConflictException(string message) : AppException(message)
{
    public override int StatusCode => 409;
    public override string ErrorCode => "conflict";
}

/// <summary>401 - authentication failed or session invalid.</summary>
public sealed class AuthenticationException(string message) : AppException(message)
{
    public override int StatusCode => 401;
    public override string ErrorCode => "unauthorized";
}

/// <summary>403 - authenticated but not allowed (ownership/role).</summary>
public sealed class ForbiddenException(string message) : AppException(message)
{
    public override int StatusCode => 403;
    public override string ErrorCode => "forbidden";
}

/// <summary>422 - a business rule was violated (BRxx).</summary>
public sealed class DomainRuleException(string message) : AppException(message)
{
    public override int StatusCode => 422;
    public override string ErrorCode => "domain_rule";
}
