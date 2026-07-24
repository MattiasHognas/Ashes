using Ashes.Registry.Storage;

namespace Ashes.Registry.Publish;

/// <summary>One file in an uploaded source tree: a package-root-relative path and its bytes.</summary>
public sealed record SourceFile(string Path, byte[] Bytes);

/// <summary>The client-declared metadata accompanying a publish upload.</summary>
public sealed record PublishMetadata(
    string Namespace,
    string Version,
    string Hash,
    string Description,
    IReadOnlyList<string> Keywords,
    IReadOnlyList<Dependency> Dependencies);

/// <summary>Everything the pipeline needs for one publish: who, what, and the compressed source tarball
/// bytes (unpacked for validation and stored verbatim as the content-addressed blob).</summary>
public sealed record PublishRequest(Account Account, PublishMetadata Metadata, byte[] Source);

/// <summary>A typed publish failure carrying one of the shared error codes.</summary>
public sealed record PublishError(string Code, string Message);

/// <summary>The pipeline outcome: either the stored version or a typed error (never both).</summary>
public sealed record PublishResult(VersionInfo? Version, PublishError? Error)
{
    /// <summary>Whether the publish succeeded, i.e. carries a version and no error.</summary>
    public bool Succeeded => Error is null;

    /// <summary>Builds a success result carrying the stored <paramref name="version"/>.</summary>
    public static PublishResult Ok(VersionInfo version) => new(version, null);

    /// <summary>Builds a failure result carrying an error with the given <paramref name="code"/> and
    /// <paramref name="message"/>.</summary>
    public static PublishResult Fail(string code, string message) => new(null, new PublishError(code, message));
}
