using Ashes.Cli.Registry;

namespace Ashes.Cli.Package;

/// <summary>An <see cref="IPackageIndex"/> backed by a registry: a package's releases are its non-yanked,
/// SemVer-valid versions with the dependency constraints the registry recorded at publish.</summary>
internal sealed class RegistryPackageIndex(RegistryClient client, string baseUrl) : IPackageIndex
{
    public async Task<IReadOnlyList<PackageRelease>> GetReleasesAsync(string ns, CancellationToken ct)
    {
        var package = await client.GetPackageAsync(baseUrl, ns, ct).ConfigureAwait(false);
        if (package is null)
        {
            return [];
        }

        return package.Versions
            .Where(v => !v.Yanked && SemVer.TryParse(v.Version, out _))
            .Select(v => new PackageRelease(
                SemVer.Parse(v.Version),
                v.Dependencies.Select(d => (d.Namespace, VersionConstraint.Parse(d.Req))).ToList()))
            .ToList();
    }
}
