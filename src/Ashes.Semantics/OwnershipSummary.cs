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
    bool ResultPoisoned)
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
