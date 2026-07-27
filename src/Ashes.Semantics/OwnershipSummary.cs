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
    FunctionResultProvenance ResultProvenance)
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
