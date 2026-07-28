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

/// <summary>
/// The provenance of a registered function's fully-saturated result, as classified from its innermost
/// body shape (see <c>Lowering.OwnershipProvenance.cs</c>). This is the AST-level, interprocedural
/// generalization of — and the real decision behind —
/// <c>TryResolveKnownFunctionResultOwnership</c>/<c>IsDirectRuntimeManagedFunctionCall</c> in
/// <c>Lowering.cs</c>. The IR-level mechanism this replaced only recognized a returned closure when a
/// function's body temp was produced by a literal <c>MakeClosure</c>/<c>MakeClosureStack</c> instruction
/// found by scanning backward through already-emitted IR, so it could not see a result computed by
/// CALLING another named function (a sibling helper) rather than directly constructing a value. This
/// record is built by classifying the function's body once, resolving through <see cref="ForwardsTo"/>
/// chains transitively (memoized, cycle-guarded), so it sees through arbitrarily deep sibling-helper
/// forwarding, not just one hop.
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
/// When the function's body is itself a call to another registered top-level/self-recursive function,
/// the immediate (one-hop) target's name; null when the body is a direct construction, a parameter
/// passthrough, or unresolved.
/// </param>
internal sealed record FunctionResultProvenance(bool RcEligible, string? ForwardsTo);

/// <summary>
/// The shape a self-recursive function's own tail-call argument takes at a given parameter position,
/// across every self-call site found in its body. Re-derives, from the same whole-program AST-only
/// fixpoint that already computes <see cref="FunctionOwnershipSummary.ExpressionFreshness"/>, the
/// question the TCO loop's own local <c>Collect*</c> classifiers (<c>Lowering.Reuse.cs</c>) answer by
/// re-walking the body a second time.
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
/// One parameter's self-call argument classification, keyed by parameter name on
/// <see cref="FunctionOwnershipSummary.TcoParamFacts"/>. Present only for a self-recursive function's
/// own parameters at a position some self-call site actually supplies — an ordinary, non-recursive
/// function's parameters never get an entry.
/// </summary>
internal sealed record TcoParamStructuralFacts(TcoSelfCallArgumentShape Shape);

/// <summary>
/// The ownership contract inferred for one fully-visible top-level function. It is the stable bridge
/// between today's move/reuse analyses and the owned and borrowed environments used by RC Perceus.
/// </summary>
internal sealed record FunctionOwnershipSummary(
    string Function,
    IReadOnlyList<string> Parameters,
    IReadOnlyDictionary<string, ParameterOwnership> ParameterOwnership,
    IReadOnlySet<string> UniqueParameters,
    IReadOnlyList<string> CapturedValues,
    IReadOnlyDictionary<string, int> ResultReach,
    bool ResultPoisoned,
    IReadOnlyDictionary<Expr, bool> ExpressionFreshness,
    FunctionResultProvenance ResultProvenance,
    IReadOnlyDictionary<string, TcoParamStructuralFacts> TcoParamFacts)
{
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
