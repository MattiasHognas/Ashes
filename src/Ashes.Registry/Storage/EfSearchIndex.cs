using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;

namespace Ashes.Registry.Storage;

/// <summary>
/// List + search over the package set. Search selects candidates through a SQLite FTS5 index
/// (namespace/description/keywords, prefix-token match) and applies the documented name-first ranking
/// (exact namespace &gt; prefix &gt; description) in memory over just those candidates, tie-broken by
/// downloads. Browse orders the package table directly. Swapping in Postgres full-text at scale is an
/// API-transparent change behind this same interface.
/// </summary>
internal sealed class EfSearchIndex(RegistryDbContext db) : ISearchIndex
{
    private const int MaxLimit = 100;

    // Search reads live metadata, so there is no separate index to maintain.
    public Task IndexAsync(PackageInfo pkg, CancellationToken ct) => Task.CompletedTask;

    public async Task<ResultPage> SearchAsync(string query, int limit, string? cursor, CancellationToken ct)
    {
        var q = (query ?? "").Trim().ToLowerInvariant();
        var match = BuildMatch(q);
        if (match is null)
        {
            return new ResultPage([], null);
        }

        // FTS5 selects the candidate set (prefix-token match across namespace/description/keywords); the
        // documented name-first ranking (exact > prefix > description) is then applied in memory over just
        // those candidates, tie-broken by downloads.
        var namespaces = await db.Database
            .SqlQueryRaw<string>("SELECT namespace AS Value FROM PackageSearch WHERE PackageSearch MATCH {0}", match)
            .ToListAsync(ct);
        if (namespaces.Count == 0)
        {
            return new ResultPage([], null);
        }

        var packages = await db.Packages.AsNoTracking().Include(p => p.Versions)
            .Where(p => namespaces.Contains(p.Namespace))
            .ToListAsync(ct);

        var scored = packages
            .Select(p => (Package: p, Score: Score(p, q)))
            .OrderByDescending(t => t.Score)
            .ThenByDescending(t => t.Package.Downloads)
            .ThenBy(t => t.Package.Namespace, StringComparer.Ordinal)
            .Select(t => Summarize(t.Package, t.Score));

        return Paginate(scored, limit, cursor);
    }

    /// <summary>Turn a user query into a safe FTS5 MATCH expression: alphanumeric tokens as prefix terms,
    /// OR-joined. Returns null when there is nothing to match (so the caller returns an empty page).</summary>
    private static string? BuildMatch(string query)
    {
        var cleaned = new StringBuilder(query.Length);
        foreach (var c in query)
        {
            cleaned.Append(char.IsLetterOrDigit(c) ? c : ' ');
        }

        var tokens = cleaned.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return tokens.Length == 0 ? null : string.Join(" OR ", tokens.Select(t => t + "*"));
    }

    public async Task<ResultPage> ListAsync(SortOrder sort, int limit, string? cursor, CancellationToken ct)
    {
        var rows = await LoadAsync(ct);

        IEnumerable<PackageEntity> ordered = sort switch
        {
            SortOrder.Name => rows.OrderBy(p => p.Namespace, StringComparer.Ordinal),
            SortOrder.Downloads => rows.OrderByDescending(p => p.Downloads).ThenBy(p => p.Namespace, StringComparer.Ordinal),
            _ => rows.OrderByDescending(p => p.UpdatedAt).ThenBy(p => p.Namespace, StringComparer.Ordinal),
        };

        return Paginate(ordered.Select(p => Summarize(p, 0)), limit, cursor);
    }

    private async Task<List<PackageEntity>> LoadAsync(CancellationToken ct) =>
        await db.Packages.AsNoTracking().Include(p => p.Versions).ToListAsync(ct);

    private static double Score(PackageEntity p, string q)
    {
        if (q.Length == 0)
        {
            return 0;
        }

        var ns = p.Namespace.ToLowerInvariant();
        if (string.Equals(ns, q, StringComparison.Ordinal))
        {
            return 1.0;
        }

        if (ns.StartsWith(q, StringComparison.Ordinal))
        {
            return 0.8;
        }

        if (ns.Contains(q, StringComparison.Ordinal))
        {
            return 0.6;
        }

        if (p.Description.Contains(q, StringComparison.OrdinalIgnoreCase) ||
            p.KeywordsJson.Contains(q, StringComparison.OrdinalIgnoreCase))
        {
            return 0.4;
        }

        return 0;
    }

    private static PackageSummary Summarize(PackageEntity p, double score)
    {
        string? latest = null;
        foreach (var v in p.Versions)
        {
            if (v.Yanked)
            {
                continue;
            }

            if (latest is null || SemVer.Compare(v.Version, latest) > 0)
            {
                latest = v.Version;
            }
        }

        return new PackageSummary(p.Namespace, p.Description, latest, p.Downloads, score);
    }

    private static ResultPage Paginate(IEnumerable<PackageSummary> ordered, int limit, string? cursor)
    {
        limit = Math.Clamp(limit <= 0 ? 20 : limit, 1, MaxLimit);
        var offset = DecodeCursor(cursor);

        var page = ordered.Skip(offset).Take(limit + 1).ToList();
        var hasMore = page.Count > limit;
        if (hasMore)
        {
            page.RemoveAt(page.Count - 1);
        }

        return new ResultPage(page, hasMore ? EncodeCursor(offset + limit) : null);
    }

    private static string EncodeCursor(int offset) =>
        Convert.ToBase64String(Encoding.ASCII.GetBytes(offset.ToString(CultureInfo.InvariantCulture)));

    private static int DecodeCursor(string? cursor)
    {
        if (string.IsNullOrEmpty(cursor))
        {
            return 0;
        }

        try
        {
            var text = Encoding.ASCII.GetString(Convert.FromBase64String(cursor));
            return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= 0 ? n : 0;
        }
        catch (FormatException)
        {
            return 0;
        }
    }
}
