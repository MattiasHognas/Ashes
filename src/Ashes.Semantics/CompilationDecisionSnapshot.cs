namespace Ashes.Semantics;

/// <summary>
/// Where a lowered value lives and who releases it. This is the reportable union of every placement
/// the compiler settles on, which is wider than any single analysis enum: ordinary placement decides
/// between a region, reference counting, and a borrowed view, while the async and parallel paths add
/// their own answers for a value that outlives the frame that produced it.
/// </summary>
internal enum ValuePlacementCategory
{
    /// <summary>No placement was established; the conservative default applies.</summary>
    ConservativeUnknown = 0,

    /// <summary>Reclaimed with the enclosing region rather than individually.</summary>
    Region,

    /// <summary>Reference counted; released when its last owner drops it.</summary>
    RuntimeRc,

    /// <summary>A non-owning view over storage owned elsewhere.</summary>
    BorrowedView,

    /// <summary>Held in a task frame across a suspend, released by the frame's teardown.</summary>
    TaskFrameOwned,

    /// <summary>Copied across a worker boundary, so each side owns its own value.</summary>
    WorkerTransfer,

    /// <summary>A copy-typed value with no heap ownership at all.</summary>
    CopyValue,
}

/// <summary>
/// One value's final placement, keyed by the function that produced it. Types are carried as their
/// reportable names rather than as <see cref="TypeRef"/> so the snapshot cannot pin inference state
/// alive after compilation.
/// </summary>
/// <remarks>
/// <c>Ordinal</c> is the position in generation order, so a consumer that needs a total order can
/// sort on it without relying on the enclosing collection's order.
/// </remarks>
internal sealed record ValuePlacementRecord(
    int Ordinal,
    IrFunctionOrigin? Function,
    int Temp,
    ValuePlacementCategory Placement,
    LoweredTempOwnershipKind Ownership,
    LoweredTempProducerKind Producer,
    LoweredTempDropKind DropKind,
    LoweredTempOwnershipReason Reason,
    string? TypeName,
    SourceLocation? Location);

/// <summary>
/// A function's ownership summary as reported, without the analysis state the live summary keeps.
/// <see cref="FunctionOwnershipSummary"/> holds an expression-keyed freshness map and pattern facts
/// that reference AST nodes; both are dropped here so the snapshot retains no AST subgraph.
/// </summary>
internal sealed record FunctionOwnershipRecord(
    int Ordinal,
    SourceFunctionOrigin Origin,
    string Function,
    IReadOnlyList<string> Parameters,
    IReadOnlyList<string> BorrowedParameters,
    IReadOnlyList<string> ConsumedParameters,
    IReadOnlyList<string> UniqueParameters,
    IReadOnlyList<string> CapturedValues,
    FunctionResultProvenance ResultProvenance,
    IReadOnlyList<string> ResultAliases,
    bool ResultFresh,
    bool ResultPoisoned,
    bool MayExecuteUnderLiveHandlerPost);

/// <summary>
/// One pattern-extracted reference's classification and the placement it received, with the binder
/// AST node projected away to its stable ordinal, name, and location.
/// </summary>
internal sealed record PatternBindingRecord(
    int Ordinal,
    SourceFunctionOrigin? Function,
    int BindingOrdinal,
    string BindingName,
    string RootParameterName,
    int ExtractionDepth,
    PatternBindingOwnershipUse Uses,
    PatternBindingOwnershipKind Ownership,
    PatternBindingPlacementOutcome PlacementOutcome,
    SourceLocation? Location);

/// <summary>A declared affine external resource and its FFI ownership contract.</summary>
internal sealed record ExternalResourceOwnershipRecord(
    string TypeName,
    string Destructor,
    IReadOnlyList<ExternalResourceParameterRecord> Parameters);

/// <summary>One external resource parameter whose ownership is declared at the FFI boundary.</summary>
internal sealed record ExternalResourceParameterRecord(
    string Function,
    int ParameterIndex,
    string TypeName,
    FfiParameterOwnership Ownership);

/// <summary>
/// The read-only record of what semantic lowering decided, for consumers that report on compilation
/// rather than participate in it.
/// </summary>
/// <remarks>
/// The contract this satisfies, in the order it matters:
/// <list type="bullet">
/// <item>immutable to consumers — every collection is handed out as a read-only projection built once
/// at capture;</item>
/// <item>deterministically ordered — each record carries an explicit <c>Ordinal</c> so a consumer can
/// impose a total order without depending on collection order;</item>
/// <item>reason codes stay enums — no record holds formatted prose, and nothing here formats;</item>
/// <item>retrievable without an environment variable — <see cref="Lowering.GetDecisionSnapshot"/> is
/// an ordinary call, and the environment-variable dump it replaced is gone;</item>
/// <item>observational only — capture appends to a list and reads nothing back, so lowering,
/// optimization, and generated code are identical whether or not a consumer ever asks for it;</item>
/// <item>retains no compiler state — types are reduced to names and AST nodes are projected away, so
/// holding a snapshot cannot keep inference state or expression trees alive.</item>
/// </list>
/// Reuse and coroutine decisions are already captured at their own decision sites in a reportable
/// shape and are surfaced here unchanged rather than re-derived. Nothing in this file inspects
/// emitted IR: the later reference-count report visits the final optimized <see cref="IrProgram"/>
/// separately and correlates through the origins these records share with it.
/// </remarks>
internal sealed record CompilationDecisionSnapshot(
    IReadOnlyList<FunctionOwnershipRecord> FunctionOwnership,
    IReadOnlyList<ValuePlacementRecord> ValuePlacements,
    IReadOnlyList<ReuseDecision> ReuseDecisions,
    IReadOnlyList<CoroutineRepresentationRecord> CoroutineRepresentations,
    IReadOnlyList<PatternBindingRecord> PatternBindings)
{
    public IReadOnlyList<ExternalResourceOwnershipRecord> ExternalResources { get; init; } = [];
    /// <summary>
    /// Every function ownership record for one reportable origin. Colliding local names share a name
    /// but not an origin, so this is the lookup that separates them.
    /// </summary>
    public IReadOnlyList<FunctionOwnershipRecord> OwnershipFor(SourceFunctionOrigin origin)
        => [.. FunctionOwnership.Where(record => record.Origin == origin)];

    /// <summary>Every value placement attributed to one generated function.</summary>
    public IReadOnlyList<ValuePlacementRecord> PlacementsIn(string generatedLabel)
        => [.. ValuePlacements.Where(record => string.Equals(
            record.Function?.GeneratedLabel,
            generatedLabel,
            StringComparison.Ordinal))];
}

public sealed partial class Lowering
{
    private readonly List<ValuePlacementRecord> _valuePlacementRecords = [];

    /// <summary>
    /// Retains the placements settled in the function being added. Temp facts are per-body state that
    /// the next function clears, so this is the last point at which a function's placements are both
    /// complete and still present.
    /// </summary>
    private void CaptureValuePlacements(IrFunctionOrigin origin)
    {
        foreach (LoweredTempOwnershipFact fact in _tempOwnershipFacts.Values.OrderBy(fact => fact.Temp))
        {
            _valuePlacementRecords.Add(new ValuePlacementRecord(
                _valuePlacementRecords.Count,
                fact.FunctionOrigin ?? origin,
                fact.Temp,
                ClassifyValuePlacement(fact),
                fact.Ownership,
                fact.Producer,
                fact.DropKind,
                fact.Reason,
                fact.Type is null ? null : GetOwnedTypeName(Prune(fact.Type)),
                fact.Location));
        }
    }

    /// <summary>
    /// Widens a lowered temp's representation into the reportable placement union. A borrowed view is
    /// decided before a copy type can be recognized, so it is tested first; a copy-typed value reports
    /// as such rather than as an unplaced unknown, because having no drop obligation is an answer.
    /// </summary>
    private ValuePlacementCategory ClassifyValuePlacement(LoweredTempOwnershipFact fact)
    {
        if (fact.Representation == LoweredTempRepresentation.BorrowedView)
        {
            return ValuePlacementCategory.BorrowedView;
        }

        if (fact.Type is not null && CanArenaReset(Prune(fact.Type)))
        {
            return ValuePlacementCategory.CopyValue;
        }

        return fact.Representation switch
        {
            LoweredTempRepresentation.ArenaRegion => ValuePlacementCategory.Region,
            LoweredTempRepresentation.RuntimeRc => ValuePlacementCategory.RuntimeRc,
            _ => ValuePlacementCategory.ConservativeUnknown,
        };
    }

    /// <summary>
    /// The read-only record of what this compilation decided. Building it reads retained records and
    /// nothing else, so asking for it cannot change what was compiled.
    /// </summary>
    internal CompilationDecisionSnapshot GetDecisionSnapshot()
    {
        var functionOwnership = new List<FunctionOwnershipRecord>();
        foreach (FunctionOwnershipSummary summary in OwnershipSummaries)
        {
            functionOwnership.Add(new FunctionOwnershipRecord(
                functionOwnership.Count,
                summary.Origin,
                summary.Function,
                summary.Parameters,
                summary.BorrowedParameters,
                summary.ConsumedParameters,
                [.. summary.UniqueParameters.OrderBy(name => name, StringComparer.Ordinal)],
                summary.CapturedValues,
                summary.ResultProvenance,
                // Reported in parameter order rather than the reach map's own, so two compilations of
                // one program cannot disagree about the order aliases are listed in.
                [.. summary.Parameters.Where(summary.ResultReaches)],
                summary.ResultFresh,
                summary.ResultPoisoned,
                summary.MayExecuteUnderLiveHandlerPost));
        }

        var patternBindings = new List<PatternBindingRecord>();
        foreach (PatternBindingOwnershipDecision decision in PatternBindingOwnershipDecisions)
        {
            patternBindings.Add(new PatternBindingRecord(
                patternBindings.Count,
                decision.Function,
                decision.BindingOrdinal,
                decision.BindingName,
                decision.RootParameterName,
                decision.ExtractionDepth,
                decision.Uses,
                decision.Ownership,
                decision.PlacementOutcome,
                decision.Location));
        }

        return new CompilationDecisionSnapshot(
            functionOwnership,
            [.. _valuePlacementRecords],
            [.. ReuseDecisions],
            [.. CoroutineRepresentationDecisions],
            patternBindings)
        {
            ExternalResources = CaptureExternalResourceOwnership(),
        };
    }

    private IReadOnlyList<ExternalResourceOwnershipRecord> CaptureExternalResourceOwnership()
    {
        return [.. _externalResourceTypes
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new ExternalResourceOwnershipRecord(
                pair.Key,
                pair.Value.DestructorName ?? string.Empty,
                [.. _externalFunctions.SelectMany(function => function.ParameterTypes
                    .Select((type, index) => (Function: function, Type: type, Index: index)))
                    .Where(parameter => parameter.Type is FfiType.Opaque opaque
                        && string.Equals(opaque.Name, pair.Key, StringComparison.Ordinal))
                    .Select(parameter => new ExternalResourceParameterRecord(
                        parameter.Function.Name,
                        parameter.Index,
                        pair.Key,
                        parameter.Index < parameter.Function.ParameterOwnerships.Count
                            ? parameter.Function.ParameterOwnerships[parameter.Index]
                            : FfiParameterOwnership.Unspecified))]))];
    }
}
