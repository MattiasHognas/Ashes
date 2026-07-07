namespace Ashes.Registry.Api;

/// <summary>The uniform error body the CLI maps to diagnostics: <c>{ "error": { "code", "message" } }</c>.</summary>
public sealed record ApiError(string Code, string Message);

/// <summary>Envelope wrapping an <see cref="ApiError"/> in the <c>error</c> field.</summary>
public sealed record ErrorEnvelope(ApiError Error);

/// <summary>The stable error codes shared with the Phase 3 diagnostics.</summary>
public static class ErrorCodes
{
    public const string NotFound = "not_found";
    public const string Unauthorized = "unauthorized";
    public const string NamespaceOwnedByAnother = "namespace_owned_by_another";
    public const string VersionExists = "version_exists";
    public const string VersionYanked = "version_yanked";
    public const string LimitExceeded = "limit_exceeded";
    public const string NamespaceLint = "namespace_lint";
}

/// <summary>Helpers producing the error envelope with the right status code.</summary>
public static class RegistryResults
{
    public static IResult Error(int status, string code, string message) =>
        Results.Json(new ErrorEnvelope(new ApiError(code, message)), statusCode: status);

    public static IResult NotFound(string message) => Error(StatusCodes.Status404NotFound, ErrorCodes.NotFound, message);
}
