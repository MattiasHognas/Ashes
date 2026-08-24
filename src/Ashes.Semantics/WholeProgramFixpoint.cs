namespace Ashes.Semantics;

/// <summary>
/// The shared "repeat until no further change" control structure behind this compiler's
/// whole-program fixpoint analyses. Several independent analyses — <c>ComputeNonAllocatingFunctions</c>
/// (<c>IrOptimizer.cs</c>, IR-level, a shrinking candidate set) and
/// <c>PropagateLiveHandlerEffects</c> (<c>Lowering.HandlerEffects.cs</c>, AST-level, a growing live set)
/// — each independently implement the same <c>bool changed = ...; while (changed) { changed = false;
/// ...; }</c> skeleton over otherwise unrelated node domains and propagation directions. Rather than
/// forcing both into one generic node/call-graph model (they run in different compiler phases over
/// different node types — one over IR functions post-lowering, one over AST-level FuncKeys during
/// lowering — with genuinely different per-iteration shapes), this extracts exactly the control
/// structure they share: repeat a caller-supplied "run one pass, report whether anything changed"
/// iteration until it reports no change, which for a monotone update over a finite domain is
/// guaranteed to terminate.
/// </summary>
internal static class WholeProgramFixpoint
{
    /// <summary>
    /// Repeatedly invokes <paramref name="iteration"/> — which should perform one full pass over
    /// the analysis's node domain and return whether anything changed — until it returns
    /// <see langword="false"/>.
    /// </summary>
    public static void RunToFixpoint(Func<bool> iteration)
    {
        while (iteration())
        {
        }
    }
}
