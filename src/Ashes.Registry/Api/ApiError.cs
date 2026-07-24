namespace Ashes.Registry.Api;

/// <summary>The uniform error body the CLI maps to diagnostics: <c>{ "error": { "code", "message" } }</c>.</summary>
public sealed record ApiError(string Code, string Message);

/// <summary>Envelope wrapping an <see cref="ApiError"/> in the <c>error</c> field.</summary>
public sealed record ErrorEnvelope(ApiError Error);

/// <summary>The stable error codes shared with the Phase 3 diagnostics.</summary>
public static class ErrorCodes
{
    /// <summary>The requested package, version, account, or blob does not exist.</summary>
    public const string NotFound = "not_found";

    /// <summary>Authentication failed or the caller lacks permission for the action.</summary>
    public const string Unauthorized = "unauthorized";

    /// <summary>The target namespace is already claimed by a different account.</summary>
    public const string NamespaceOwnedByAnother = "namespace_owned_by_another";

    /// <summary>A publish targets a (namespace, version) pair that already exists with different content.</summary>
    public const string VersionExists = "version_exists";

    /// <summary>The operation was refused because the target version has been yanked.</summary>
    public const string VersionYanked = "version_yanked";

    /// <summary>A publish breached one of the registry's size or file-count limits.</summary>
    public const string LimitExceeded = "limit_exceeded";

    /// <summary>An uploaded module lives outside the package's declared namespace.</summary>
    public const string NamespaceLint = "namespace_lint";

    /// <summary>The supplied version string is not valid SemVer.</summary>
    public const string InvalidVersion = "invalid_version";

    /// <summary>The client-declared content hash does not match the server-computed one.</summary>
    public const string HashMismatch = "hash_mismatch";
}

/// <summary>Helpers producing the error envelope with the right status code.</summary>
public static class RegistryResults
{
    /// <summary>Produces a JSON <see cref="ErrorEnvelope"/> carrying <paramref name="code"/> and
    /// <paramref name="message"/> at the given HTTP <paramref name="status"/> code.</summary>
    public static IResult Error(int status, string code, string message) =>
        Results.Json(new ErrorEnvelope(new ApiError(code, message)), statusCode: status);

    /// <summary>Shorthand for a <c>404</c> response carrying the <see cref="ErrorCodes.NotFound"/> code.</summary>
    public static IResult NotFound(string message) => Error(StatusCodes.Status404NotFound, ErrorCodes.NotFound, message);
}
