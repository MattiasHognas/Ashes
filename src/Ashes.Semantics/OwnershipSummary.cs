using Ashes.Frontend;

namespace Ashes.Semantics;

/// <summary>
/// How a function treats an incoming parameter at its boundary. Borrowed parameters remain owned by
/// the caller; consumed parameters transfer their ownership to the callee. This is compiler-internal
/// metadata and does not add ownership syntax or restrictions to Ashes source programs.
/// </summary>
internal enum ParameterOwnership
{
    Borrowed,
    Consumed,
}

[Flags]
internal enum FunctionCallCensusCause
{
    None = 0,
    EscapedAsValue = 1 << 0,
    IncompleteApplication = 1 << 1,
    AmbiguousResolution = 1 << 2,
    UnknownResolution = 1 << 3,
}

internal sealed record FunctionCallCensus(
    int DirectCallCount,
    FunctionCallCensusCause Causes)
{
    public bool Complete => Causes == FunctionCallCensusCause.None;
}

[Flags]
internal enum ParameterMoveSafetyCause
{
    None = 0,
    FunctionEscaped = 1 << 0,
    IncompleteCallCensus = 1 << 1,
    NoDirectCallSites = 1 << 2,
    NoExternalCallSites = 1 << 3,
    CallArityMismatch = 1 << 4,
    SeedNotSafe = 1 << 5,
    MoveLinearity = 1 << 6,
    CapturedByClosure = 1 << 7,
    TransitiveParameterUnsafe = 1 << 8,
    ResultAliasUnsafe = 1 << 9,
    AmbiguousResolution = 1 << 10,
    ProofCycle = 1 << 11,
    ConservativeUnknown = 1 << 12,
}

internal sealed record ParameterMoveSafetyProof(
    bool IsMoveSafe,
    ParameterMoveSafetyCause Causes);

[Flags]
internal enum ResultReachCause
{
    None = 0,
    GlobalOrTopLevelReach = 1 << 0,
    UnmodelledReach = 1 << 1,
    InternalSharing = 1 << 2,
    ConservativeUnknown = 1 << 3,
}

internal sealed record FunctionResultReachFacts(
    IReadOnlyDictionary<string, int> ParameterReach,
    ResultReachCause Causes)
{
    public bool Poisoned => Causes != ResultReachCause.None;
}

[Flags]
internal enum OwnershipDecisionFact
{
    None = 0,
    ParameterMoveSafety = 1 << 0,
    ResultProvenance = 1 << 1,
    RuntimeManageableResultType = 1 << 2,
}

internal enum OwnershipDecisionKind
{
    ReuseEntryCopyElision,
    RuntimeManagedCallResult,
}

internal sealed record OwnershipFactConsumption(
    SourceFunctionOrigin Function,
    OwnershipDecisionKind Decision,
    string? Parameter,
    OwnershipDecisionFact EvaluatedFacts,
    OwnershipDecisionFact PositiveFacts,
    bool Outcome);

/// <summary>
/// The provenance of a registered function's fully-saturated result, as classified from its innermost
/// body shape (see <c>Lowering.OwnershipProvenance.cs</c>). This is the AST-level, interprocedural
/// generalization of — and the real decision behind —
/// <c>TryResolveKnownFunctionResultOwnership</c>/<c>IsDirectRuntimeManagedFunctionCall</c> in
/// <c>Lowering.cs</c>. The IR-level mechanism this replaced only recognized a returned closure when a
/// function's body temp was produced by a literal <c>MakeClosure</c>/<c>MakeClosureStack</c> instruction
/// found by scanning backward through already-emitted IR, so it could not see a result computed by
/// CALLING another named function (a sibling helper) rather than directly constructing a value. This
/// record is built by classifying each function's body once, then resolving exact saturated forwarding
/// calls through a whole-program <c>FuncKey</c> graph fixpoint. Strongly-connected components converge
/// together, so productive mutual recursion can inherit an independently eligible result construction
/// while pure forwarding cycles and components with any conservative terminal arm fail closed.
/// </summary>
/// <param name="RcEligible">
/// True when the function's fully-saturated result is provably an ordinary heap allocation eligible
/// for RC management (a constructor application, list/tuple/record literal, a fully applied call to a
/// declared fresh-RC-producing builtin, an <c>Expr.Add</c> node, or a forwarding call whose own
/// ultimate target is itself RC-eligible) rather than a copy-typed scalar, a bare parameter passthrough,
/// or an unresolved/foreign value. This does not by itself decide RC-vs-arena representation (a
/// downstream, escape-driven choice) — it only answers "is this an RC-*eligible* ordinary heap value at
/// all."
/// </param>
/// <param name="ForwardsTo">
/// The immediate (one-hop) target's name when all forwarding terminal arms name one exact registered
/// function; null when there are no forwarding arms or when multiple distinct immediate targets occur.
/// This diagnostic correlation is independent of <paramref name="RcEligible"/>.
/// </param>
internal sealed record FunctionResultProvenance(bool RcEligible, string? ForwardsTo);

/// <summary>
/// The shape a self-recursive function's own tail-call argument takes at a given parameter position,
/// across every exact self-call site found in its body. It is computed by the canonical
/// binding-identity-aware ownership walk, using the same whole-program AST-only fixpoint that
/// computes <see cref="FunctionOwnershipSummary.ExpressionFreshness"/>. Orthogonal list-reset and
/// closure-construction facts live on <see cref="TcoParamStructuralFacts"/>.
/// </summary>
internal enum TcoSelfCallArgumentShape
{
    /// Every self-recursive call site's argument at this position is a bare, unchanged reference to
    /// this same parameter — the loop-invariant shape (never rebuilt, so a plain per-iteration arena
    /// reset always leaves it valid).
    UnchangedPassthrough,

    /// Every self-recursive call site's argument at this position aliases no parameter of the
    /// function (<c>ExpressionFreshness[argExpr] == true</c> for every occurrence) and is not itself
    /// an unchanged passthrough — a self-contained value built fresh this iteration.
    FreshRebuilt,

    /// Every self-recursive call site's argument at this position is a name pattern-extracted, one
    /// match level deep, as the cons-tail of this same parameter's own value — a strictly smaller
    /// structural decomposition of the parameter, never rebuilt and never handed to a different
    /// parameter's own slot. Does not model field extraction beyond a list cons-tail, matching what
    /// the classifier it re-derives actually checks.
    ConsumedTail,

    /// Every self-recursive call site's argument at this position is a fresh cons cell whose tail is a
    /// bare, unchanged reference to this same parameter — the accumulator grows by one cell per
    /// iteration, consing onto its own prior value rather than replacing it wholesale. Distinct from
    /// <see cref="FreshRebuilt"/> (the cons cell is NOT alias-free — it embeds the parameter's own
    /// previous value as its tail) and from <see cref="ConsumedTail"/> (the parameter grows here,
    /// rather than shrinking via pattern-extraction).
    GrownCons,

    /// No single shape above holds at every self-recursive call site, or the function has no
    /// self-recursive call site to classify from at all.
    Mixed,
}

/// <summary>
/// How a TCO parameter and values structurally derived from it are used by the recursive body.
/// This is intentionally separate from <see cref="TcoSelfCallArgumentShape"/>: the successor may
/// consume a list tail while the current iteration either only inspects that graph or lets a
/// derived reference escape.
/// </summary>
internal enum TcoParamUseMode
{
    /// <summary>
    /// The body contains an owning/escaping use, or the analysis encountered a shape it cannot
    /// classify. Consumers must retain the conservative ownership path.
    /// </summary>
    GeneralOrUnknown,

    /// <summary>
    /// The parameter is consumed through its tail and every derived head/tail reference is used
    /// only for structural inspection or transferred to the same parameter of an exact self-call.
    /// </summary>
    BorrowInspectOnly,
}

/// <summary>
/// The affine ownership discipline proven for a TCO parameter across loop-continuing paths. This is
/// independent of the successor value's structural shape and of the parameter's borrow-inspection
/// mode: it records whether a consuming reuse operation may treat the current value as the sole live
/// loop reference.
/// </summary>
internal enum TcoParamReuseAffinity
{
    /// <summary>
    /// The body contains another continuing-path use, or the analysis encountered a shape it cannot
    /// classify. Reuse consumers must retain their non-affine path.
    /// </summary>
    GeneralOrUnknown,

    /// <summary>
    /// Along every loop-continuing path, the parameter is used at most once and only as the
    /// leftmost leaf of the addition chain producing its own exact self-call argument, or is passed
    /// through unchanged. Combined with the loop-entry watermark, this licenses string reservation
    /// reuse.
    /// </summary>
    SelfAppendOnly,
}

/// <summary>
/// One parameter position's self-call argument classification. Parameter ordinal is the stable
/// binding identity within the function's curried parameter list; the source name is retained for
/// diagnostics but is not used to join facts back to lowering slots. Facts are present only for a
/// self-recursive function whose exact self-call sites supply every parameter position.
/// </summary>
/// <param name="ParameterOrdinal">Zero-based position in the curried parameter list.</param>
/// <param name="ParameterName">Source name retained for diagnostics.</param>
/// <param name="Shape">
/// The reference-ownership shape shared by every exact self-call argument at this parameter position.
/// </param>
/// <param name="ArenaSelfContainedListRebuild">
/// True when every exact self-call argument at this position rebuilds a list during the current
/// iteration in a form that is independent of the called helper's arena extent. This licenses a
/// bounded whole-list copy at the TCO reset boundary; it does not claim reference freshness. In
/// particular, a helper call result may be arena-self-contained while still retaining an input tail.
/// </param>
/// <param name="FreshClosureRebuild">
/// True when every exact self-call argument at this position directly constructs a closure, or
/// selects between direct closure constructions. This is separate from <paramref name="Shape"/>
/// because a new closure may legitimately capture and therefore reach an input reference.
/// </param>
/// <param name="UseMode">
/// The canonical ownership use mode for this parameter and values structurally derived from it.
/// </param>
/// <param name="ReuseAffinity">
/// The canonical affine-use proof consumed by representation-specific reuse lowering.
/// </param>
internal sealed record TcoParamStructuralFacts(
    int ParameterOrdinal,
    string ParameterName,
    TcoSelfCallArgumentShape Shape,
    bool ArenaSelfContainedListRebuild,
    bool FreshClosureRebuild,
    TcoParamUseMode UseMode,
    TcoParamReuseAffinity ReuseAffinity);

/// <summary>
/// The ownership contract inferred for one fully-visible top-level function. It is the stable bridge
/// between today's move/reuse analyses and the owned and borrowed environments used by RC Perceus.
/// </summary>
internal sealed record FunctionOwnershipSummary(
    string Function,
    SourceFunctionOrigin Origin,
    IReadOnlyList<string> Parameters,
    IReadOnlyDictionary<string, ParameterOwnership> ParameterOwnership,
    IReadOnlySet<string> UniqueParameters,
    FunctionCallCensus CallCensus,
    IReadOnlyDictionary<string, ParameterMoveSafetyProof> ParameterMoveSafety,
    IReadOnlyList<string> CapturedValues,
    FunctionResultReachFacts ResultReachFacts,
    IReadOnlyDictionary<Expr, bool> ExpressionFreshness,
    FunctionResultProvenance ResultProvenance,
    IReadOnlyList<TcoParamStructuralFacts> TcoParamFacts)
{
    public IReadOnlyDictionary<string, int> ResultReach => ResultReachFacts.ParameterReach;

    public bool ResultPoisoned => ResultReachFacts.Poisoned;

    /// <summary>Parameters whose ownership remains with the caller.</summary>
    public IReadOnlyList<string> BorrowedParameters => Parameters
        .Where(parameter => ParameterOwnership[parameter] == Ashes.Semantics.ParameterOwnership.Borrowed)
        .ToList();

    /// <summary>Parameters whose ownership transfers to the callee.</summary>
    public IReadOnlyList<string> ConsumedParameters => Parameters
        .Where(parameter => ParameterOwnership[parameter] == Ashes.Semantics.ParameterOwnership.Consumed)
        .ToList();

    /// <summary>
    /// The result is a fresh, uniquely-owned value: it aliases no parameter and is not poisoned.
    /// </summary>
    public bool ResultFresh => !ResultPoisoned && ResultReach.Count == 0;

    /// <summary>True if the result may alias parameter <paramref name="parameter"/>.</summary>
    public bool ResultReaches(string parameter) => ResultReach.ContainsKey(parameter);
}
