using Ashes.Cli.Package;
using Shouldly;

namespace Ashes.Cli.Tests;

/// <summary>SemVer parsing/ordering, version constraints, and the Cargo-model dependency resolver.</summary>
public sealed class ResolverTests
{
    [Test]
    public void SemVer_orders_numerically_and_ranks_release_above_prerelease()
    {
        SemVer.Parse("1.10.0").CompareTo(SemVer.Parse("1.2.0")).ShouldBeGreaterThan(0);
        SemVer.Parse("2.0.0").CompareTo(SemVer.Parse("2.0.0-rc.1")).ShouldBeGreaterThan(0);
        SemVer.Parse("1.0.0-alpha").CompareTo(SemVer.Parse("1.0.0-beta")).ShouldBeLessThan(0);
        SemVer.TryParse("1.2", out _).ShouldBeFalse();
    }

    [Test]
    public void Caret_constraint_allows_compatible_updates_only()
    {
        var caret = VersionConstraint.Parse("^1.2.0");
        caret.Matches(SemVer.Parse("1.2.0")).ShouldBeTrue();
        caret.Matches(SemVer.Parse("1.9.5")).ShouldBeTrue();
        caret.Matches(SemVer.Parse("2.0.0")).ShouldBeFalse();
        caret.Matches(SemVer.Parse("1.1.0")).ShouldBeFalse();
        caret.Matches(SemVer.Parse("1.5.0-rc.1")).ShouldBeFalse(); // prereleases excluded
    }

    [Test]
    public void Tilde_exact_and_star_constraints()
    {
        VersionConstraint.Parse("~1.2.0").Matches(SemVer.Parse("1.2.9")).ShouldBeTrue();
        VersionConstraint.Parse("~1.2.0").Matches(SemVer.Parse("1.3.0")).ShouldBeFalse();
        VersionConstraint.Parse("=1.2.3").Matches(SemVer.Parse("1.2.3")).ShouldBeTrue();
        VersionConstraint.Parse("=1.2.3").Matches(SemVer.Parse("1.2.4")).ShouldBeFalse();
        VersionConstraint.Parse("*").Matches(SemVer.Parse("9.9.9")).ShouldBeTrue();
    }

    [Test]
    public async Task Resolver_picks_the_highest_compatible_version()
    {
        var index = new FakeIndex(new(StringComparer.Ordinal)
        {
            ["Json"] = [Rel("1.0.0"), Rel("1.2.0"), Rel("2.0.0")],
        });

        var resolved = await new DependencyResolver(index)
            .ResolveAsync([Req("Json", "^1.0.0")], CancellationToken.None);

        resolved.ShouldHaveSingleItem().Version.ToString().ShouldBe("1.2.0");
    }

    [Test]
    public async Task Resolver_pulls_in_transitive_dependencies()
    {
        var index = new FakeIndex(new(StringComparer.Ordinal)
        {
            ["App"] = [Rel("1.0.0", ("Utf8", "^0.4.0"))],
            ["Utf8"] = [Rel("0.4.0"), Rel("0.4.3")],
        });

        var resolved = await new DependencyResolver(index)
            .ResolveAsync([Req("App", "^1.0.0")], CancellationToken.None);

        resolved.Select(r => $"{r.Namespace}@{r.Version}").ShouldBe(["App@1.0.0", "Utf8@0.4.3"]);
    }

    [Test]
    public async Task Resolver_reports_a_conflict_when_constraints_cannot_be_unified()
    {
        var index = new FakeIndex(new(StringComparer.Ordinal)
        {
            ["Json"] = [Rel("1.0.0"), Rel("2.0.0")],
        });

        var ex = await Should.ThrowAsync<DependencyResolutionException>(
            new DependencyResolver(index).ResolveAsync(
                [Req("Json", "^1.0.0", "a"), Req("Json", "^2.0.0", "b")], CancellationToken.None));

        ex.Message.ShouldContain("ASH032");
        ex.Message.ShouldContain("Json");
    }

    private static DependencyReq Req(string ns, string constraint, string source = "root") =>
        new(ns, VersionConstraint.Parse(constraint), source);

    private static PackageRelease Rel(string version, params (string Ns, string Req)[] deps) =>
        new(SemVer.Parse(version), deps.Select(d => (d.Ns, VersionConstraint.Parse(d.Req))).ToList());

    private sealed class FakeIndex(Dictionary<string, IReadOnlyList<PackageRelease>> data) : IPackageIndex
    {
        public Task<IReadOnlyList<PackageRelease>> GetReleasesAsync(string ns, CancellationToken ct) =>
            Task.FromResult(data.TryGetValue(ns, out var releases) ? releases : []);
    }
}
