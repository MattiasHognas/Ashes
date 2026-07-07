namespace Ashes.Cli.Package;

/// <summary>A required dependency: a namespace, a constraint, and who asked for it (for diagnostics).</summary>
internal sealed record DependencyReq(string Namespace, VersionConstraint Constraint, string Source);

/// <summary>One published version of a package and the dependencies it declares.</summary>
internal sealed record PackageRelease(SemVer Version, IReadOnlyList<(string Namespace, VersionConstraint Constraint)> Dependencies);

/// <summary>A package pinned to a single resolved version.</summary>
internal sealed record ResolvedPackage(string Namespace, SemVer Version);

/// <summary>Source of a package's available releases (registry-backed in production; a fake in tests).</summary>
internal interface IPackageIndex
{
    Task<IReadOnlyList<PackageRelease>> GetReleasesAsync(string ns, CancellationToken ct);
}

internal sealed class DependencyResolutionException(string message) : Exception(message);

/// <summary>
/// The Cargo-model resolver: highest version satisfying all constraints, unified to one version per
/// package. Accumulates constraints across the transitive graph and re-selects until the choice
/// stabilizes; an empty intersection for any package is a typed conflict (ASH032). No backtracking —
/// a conflict is reported rather than searched around, which suits the single-version-per-build world.
/// </summary>
internal sealed class DependencyResolver(IPackageIndex index)
{
    private const int MaxIterations = 1000;

    public async Task<IReadOnlyList<ResolvedPackage>> ResolveAsync(
        IReadOnlyList<DependencyReq> roots, CancellationToken ct)
    {
        var constraints = new Dictionary<string, List<DependencyReq>>(StringComparer.Ordinal);
        foreach (var root in roots)
        {
            Add(constraints, root);
        }

        var selected = new Dictionary<string, SemVer>(StringComparer.Ordinal);
        var releaseCache = new Dictionary<string, IReadOnlyList<PackageRelease>>(StringComparer.Ordinal);

        for (var iteration = 0; iteration < MaxIterations; iteration++)
        {
            var changed = false;
            foreach (var ns in constraints.Keys.ToList())
            {
                if (!releaseCache.TryGetValue(ns, out var releases))
                {
                    releases = await index.GetReleasesAsync(ns, ct).ConfigureAwait(false);
                    releaseCache[ns] = releases;
                }

                var reqs = constraints[ns];
                var best = releases
                    .Where(r => reqs.All(req => req.Constraint.Matches(r.Version)))
                    .OrderByDescending(r => r.Version)
                    .FirstOrDefault();

                if (best is null)
                {
                    var who = string.Join("; ", reqs.Select(req => $"{req.Source} requires {req.Constraint}"));
                    throw new DependencyResolutionException(
                        $"ASH032: no version of '{ns}' satisfies all constraints ({who}).");
                }

                if (!selected.TryGetValue(ns, out var current) || current.CompareTo(best.Version) != 0)
                {
                    selected[ns] = best.Version;
                    changed = true;
                    foreach (var (depNs, depConstraint) in best.Dependencies)
                    {
                        Add(constraints, new DependencyReq(depNs, depConstraint, $"{ns}@{best.Version}"));
                    }
                }
            }

            if (!changed)
            {
                break;
            }
        }

        return selected
            .Select(kv => new ResolvedPackage(kv.Key, kv.Value))
            .OrderBy(p => p.Namespace, StringComparer.Ordinal)
            .ToList();
    }

    private static void Add(Dictionary<string, List<DependencyReq>> map, DependencyReq req)
    {
        if (!map.TryGetValue(req.Namespace, out var list))
        {
            list = [];
            map[req.Namespace] = list;
        }

        if (!list.Any(r => string.Equals(r.Constraint.Raw, req.Constraint.Raw, StringComparison.Ordinal)
                           && string.Equals(r.Source, req.Source, StringComparison.Ordinal)))
        {
            list.Add(req);
        }
    }
}
