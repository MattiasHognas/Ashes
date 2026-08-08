using Ashes.Frontend;

namespace Ashes.Fuzzing.Generation;

[Flags]
internal enum GenerationFlags
{
    None = 0,
    SuspensionAllowed = 1,
    ResourcesAllowed = 2,
    RecursionAllowed = 4,
    TailPosition = 8,
}

internal enum GeneratedFeature
{
    Literal, Variable, Arithmetic, Comparison, Let, If, Tuple, List, Lambda, Call,
    Match, Adt, Record, RecursiveFunction, TailCall, Capability, Handler, Await, Spawn,
    Resource, ResultShortCircuit, ReuseCandidate, SharedValue, CrossBranchAlias,
    ConstructorReconstruction, LayoutCompatibleReuse, LayoutIncompatibleFallback,
    ClosureCapture, EscapingClosure, MultipleClosures, GuardedMatch, RuntimeUniquenessCheck,
    NestedCombination, TopLevelDeclaration, TopLevelFunction, MutualRecursion, Provider,
}

internal sealed class GeneratedFeatureSet : IReadOnlyCollection<GeneratedFeature>
{
    private readonly SortedSet<GeneratedFeature> _features = [];
    internal GeneratedFeatureSet() { }
    internal GeneratedFeatureSet(IEnumerable<GeneratedFeature> features) => _features.UnionWith(features);
    public int Count => _features.Count;
    public IEnumerator<GeneratedFeature> GetEnumerator() => _features.GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    internal void Add(GeneratedFeature feature) => _features.Add(feature);
    internal void UnionWith(IEnumerable<GeneratedFeature> features) => _features.UnionWith(features);
    internal bool Contains(GeneratedFeature feature) => _features.Contains(feature);
    internal GeneratedFeatureSet Copy() => new(_features);
}

internal sealed record GenerationTrace(IReadOnlyList<string> Entries)
{
    internal static GenerationTrace Empty { get; } = new([]);
    internal GenerationTrace Append(string entry) => new([.. Entries, entry]);
    internal static GenerationTrace Merge(string entry, params GenerationTrace[] traces) =>
        new([entry, .. traces.SelectMany(trace => trace.Entries)]);
}

internal sealed record GenerationResult<T>(T Value, AshesType Type, GeneratedFeatureSet Features, GenerationTrace Trace, int NodeCount);
internal sealed record GeneratedBinding(string Name, AshesType Type, bool IsFunction = false);
internal sealed record GeneratedAdt(string Name, IReadOnlyList<(string Name, IReadOnlyList<AshesType> Fields)> Constructors);
internal sealed record GeneratedRecord(string Name, IReadOnlyList<(string Name, AshesType Type)> Fields);

internal sealed record GenerationContext(
    IReadOnlyList<GeneratedBinding> Bindings,
    IReadOnlyList<GeneratedAdt> Adts,
    IReadOnlyList<GeneratedRecord> Records,
    IReadOnlySet<string> Capabilities,
    GenerationFlags Flags,
    IReadOnlySet<string> ActiveTemplates)
{
    internal static GenerationContext Empty { get; } = new([], [], [], new SortedSet<string>(StringComparer.Ordinal), GenerationFlags.RecursionAllowed, new SortedSet<string>(StringComparer.Ordinal));
    internal GenerationContext WithBinding(GeneratedBinding binding) => this with { Bindings = [.. Bindings, binding] };
    internal GenerationContext WithAdt(GeneratedAdt adt) => this with { Adts = [.. Adts, adt] };
    internal GenerationContext WithRecord(GeneratedRecord record) => this with { Records = [.. Records, record] };
    internal GenerationContext WithCapability(string capability) => this with
    {
        Capabilities = new SortedSet<string>(Capabilities, StringComparer.Ordinal) { capability },
    };
    internal GenerationContext WithTemplate(string id) => this with { ActiveTemplates = new SortedSet<string>(ActiveTemplates, StringComparer.Ordinal) { id } };
}

internal sealed record GenerationBudget(
    int RemainingNodes,
    int RemainingDepth,
    int RemainingDeclarations,
    int RemainingFunctions,
    int RemainingAdts,
    int MaximumMatchCases,
    int MaximumCollectionLength,
    int RemainingRecursion,
    int RemainingCombinations,
    int MaximumSourceLength)
{
    internal static GenerationBudget Create(int maximumNodes) => new(maximumNodes, 10, 8, 6, 3, 4, 5, 3, 4, maximumNodes * 48);
    internal bool IsLeaf => RemainingNodes <= 2 || RemainingDepth <= 1;
    internal GenerationBudget Descend(int nodes = 1) => this with { RemainingNodes = Math.Max(0, RemainingNodes - nodes), RemainingDepth = Math.Max(0, RemainingDepth - 1) };
    internal GenerationBudget UseCombination() => Descend() with { RemainingCombinations = Math.Max(0, RemainingCombinations - 1) };
}

internal sealed record GeneratedFuzzCase(
    ulong MasterSeed,
    ulong CaseSeed,
    int CaseIndex,
    string Profile,
    AshesType Type,
    Ashes.Frontend.Program Program,
    string Source,
    GeneratedFeatureSet Features,
    GenerationTrace Trace,
    int NodeCount,
    GenerationBudget Budget);
