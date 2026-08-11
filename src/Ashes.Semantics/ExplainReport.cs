namespace Ashes.Semantics;

/// <summary>Which compiler decisions a report covers.</summary>
public enum ExplainKind
{
    /// <summary>Inferred source-level ownership contracts.</summary>
    Ownership,

    /// <summary>Perceus operations in the final semantic IR.</summary>
    Rc,

    /// <summary>In-place-reuse specialization decisions.</summary>
    Reuse,

    /// <summary>Hidden trait dictionary ABI parameters and concrete implementation selection.</summary>
    Traits,

    /// <summary>Ownership, reference counting, reuse, and representation, correlated.</summary>
    Memory,
}

/// <summary>
/// What the caller asked to be reported. An empty <paramref name="Kinds"/> set means no report.
/// </summary>
/// <param name="Kinds">The requested reports, deduplicated by the set.</param>
/// <param name="FunctionFilter">
/// Restricts the report to matching functions. Matched against a function's source name, qualified
/// name, generated label, and owning source function, so a caller never needs a generated symbol.
/// Null reports everything.
/// </param>
public sealed record ExplainRequest(
    IReadOnlySet<ExplainKind> Kinds,
    string? FunctionFilter = null)
{
    /// <summary>A request for no report at all.</summary>
    public static ExplainRequest None { get; } = new(new HashSet<ExplainKind>());

    /// <summary>True when nothing was requested, so no report should be produced or printed.</summary>
    public bool IsEmpty => Kinds.Count == 0;

    /// <summary>Whether the given report was requested.</summary>
    public bool Includes(ExplainKind kind) => Kinds.Contains(kind);

    /// <summary>
    /// Parses one <c>--explain</c> value, in either the bare <c>ownership</c> or the filtered
    /// <c>ownership:Map.set</c> form. Returns false with a caller-formattable reason for an unknown
    /// kind; an empty filter after the colon is treated as no filter.
    /// </summary>
    public static bool TryParseValue(
        string value,
        out ExplainKind kind,
        out string? filter,
        out string? error)
    {
        kind = default;
        filter = null;
        error = null;

        string kindText = value;
        int separator = value.IndexOf(':', StringComparison.Ordinal);
        if (separator >= 0)
        {
            kindText = value[..separator];
            string rest = value[(separator + 1)..];
            filter = string.IsNullOrWhiteSpace(rest) ? null : rest;
        }

        switch (kindText.Trim().ToLowerInvariant())
        {
            case "ownership": kind = ExplainKind.Ownership; return true;
            case "rc": kind = ExplainKind.Rc; return true;
            case "reuse": kind = ExplainKind.Reuse; return true;
            case "traits": kind = ExplainKind.Traits; return true;
            case "memory": kind = ExplainKind.Memory; return true;
            default:
                error = $"Unknown explain type '{kindText}'.";
                return false;
        }
    }

    /// <summary>The valid values, in report order, for help text and error messages.</summary>
    public static IReadOnlyList<string> ValidValues { get; } = ["ownership", "rc", "reuse", "traits", "memory"];
}

/// <summary>One function's ownership contract, as reported.</summary>
internal sealed record OwnershipFunctionReport(
    string Function,
    SourceFunctionOrigin Origin,
    IReadOnlyList<OwnershipParameterReport> Parameters,
    IReadOnlyList<string> ResultAliases,
    bool ResultFresh,
    bool ResultPoisoned,
    IReadOnlyList<string> CapturedValues);

/// <summary>One parameter's ownership within <see cref="OwnershipFunctionReport"/>.</summary>
internal sealed record OwnershipParameterReport(
    string Name,
    ParameterOwnership Ownership,
    bool Unique,
    bool MoveSafe);

/// <summary>The Perceus operations counted in one function of the final semantic IR.</summary>
internal sealed record RcFunctionReport(
    string Label,
    IrFunctionOrigin? Origin,
    int Dups,
    int Drops,
    int UniquenessChecks,
    int Allocations,
    int ReusedAllocations,
    int ReuseTokens,
    int Copies)
{
    internal int Total => Dups + Drops + UniquenessChecks + Allocations + ReusedAllocations + ReuseTokens + Copies;
}

/// <summary>One reuse decision, as reported.</summary>
internal sealed record ReuseFunctionReport(
    string Function,
    IrFunctionOrigin Origin,
    ReuseDecisionKind Decision,
    ReuseDecisionOutcome Outcome,
    ReuseDecisionReason Reason,
    string? Candidate,
    SourceLocation? Location);

/// <summary>Where one function's values physically live.</summary>
internal sealed record RepresentationFunctionReport(
    string Label,
    IrFunctionOrigin? Origin,
    IReadOnlyDictionary<ValuePlacementCategory, int> Placements);

/// <summary>
/// Everything the requested reports need, built once from the decisions lowering already recorded and
/// the final semantic IR. Reading it cannot affect what was compiled.
/// </summary>
internal sealed record CompilationExplainReport(
    IReadOnlyList<OwnershipFunctionReport> Ownership,
    IReadOnlyList<RcFunctionReport> Rc,
    IReadOnlyList<ReuseFunctionReport> Reuse,
    IReadOnlyList<RepresentationFunctionReport> Representation,
    TraitEvidenceAnnotations TraitEvidence)
{
    internal static CompilationExplainReport Empty { get; } = new([], [], [], [], TraitEvidenceAnnotations.Empty);
}
