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
    SharedReuseFallback, BranchSelectiveReuse, UniqueConstructorUpdate,
    ClosureInMatch, RecursiveReconstruction, ResultClosure, CapabilityInClosure,
    ResultErrorMapping, ResultBinding,
    ResultAliasesInput, FreshResultInternalSharing, StaticallyUniquePath,
    NestedMatch, LoopCarriedAdt,
    CapturedReuseCandidate,
    TaskResultReuse,
    ClosureAcrossAwait,
    MatchAcrossAwait,
    RecursiveResult,
    NestedReusableConstructors,
    AliasedResultPreventsReuse,
    FreshResultAllowsReuse,
    RecursionWithSharing,
    LogicalNot,
    TraitDeclaration,
    TraitImplementation,
    TraitConstraint,
    TraitMethodCall,
    TraitResolution,
    TraitOperator,
    DerivedImplementation,
    MultiParameterTrait,
    ExternalResource,
    TypeAlias,
    ZeroCostType,
}

internal enum OwnershipInterest
{
    Sharing,
    Aliasing,
    ClosureCapture,
    CrossBranch,
    Reuse,
    Uniqueness,
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
internal sealed record GeneratedAdt(
    string Name,
    int Arity,
    IReadOnlyList<(string Name, IReadOnlyList<AshesType> Fields)> Constructors);
internal sealed record GeneratedRecord(string Name, IReadOnlyList<(string Name, AshesType Type)> Fields);
internal sealed record GeneratedCapabilityOperation(string Name, AshesType Parameter, AshesType Result);
internal sealed record GeneratedCapability(string Name, IReadOnlyList<GeneratedCapabilityOperation> Operations);
internal sealed record GeneratedTrait(
    string Name,
    string Method,
    string ConstrainedFunction,
    IReadOnlyList<AshesType> ImplementedTypes,
    string? DerivedBoxType = null,
    string? DerivedBoxConstructor = null);

internal sealed record GenerationContext(
    IReadOnlyList<GeneratedBinding> Bindings,
    IReadOnlyList<GeneratedAdt> Adts,
    IReadOnlyList<GeneratedRecord> Records,
    IReadOnlyList<AshesType.Resource> ResourceTypes,
    IReadOnlyList<GeneratedCapability> Capabilities,
    IReadOnlyList<GeneratedTrait> Traits,
    IReadOnlySet<string> ActiveHandlers,
    GenerationFlags Flags,
    IReadOnlySet<OwnershipInterest> OwnershipInterests,
    IReadOnlySet<string> ActiveTemplates,
    IReadOnlySet<GeneratedFeature> CurrentFeatures)
{
    internal static GenerationContext Empty { get; } = new(
        [],
        [],
        [],
        AshesType.SupportedResources,
        [],
        [],
        new SortedSet<string>(StringComparer.Ordinal),
        GenerationFlags.RecursionAllowed | GenerationFlags.SuspensionAllowed | GenerationFlags.ResourcesAllowed,
        new SortedSet<OwnershipInterest>(Enum.GetValues<OwnershipInterest>()),
        new SortedSet<string>(StringComparer.Ordinal),
        new SortedSet<GeneratedFeature>());
    internal GenerationContext WithBinding(GeneratedBinding binding) => this with { Bindings = [.. Bindings, binding] };
    internal GenerationContext WithAdt(GeneratedAdt adt) => this with { Adts = [.. Adts, adt] };
    internal GenerationContext WithRecord(GeneratedRecord record) => this with { Records = [.. Records, record] };
    internal GenerationContext WithResourceTypes(IEnumerable<AshesType.Resource> resourceTypes) => this with
    {
        ResourceTypes = resourceTypes.OrderBy(type => type.Name, StringComparer.Ordinal).ToArray(),
    };
    internal GenerationContext WithCapability(GeneratedCapability capability) => this with
    {
        Capabilities = [.. Capabilities.Where(candidate => !string.Equals(candidate.Name, capability.Name, StringComparison.Ordinal)), capability],
    };
    internal GenerationContext WithTrait(GeneratedTrait trait) => this with
    {
        Traits = [.. Traits.Where(candidate => !string.Equals(candidate.Name, trait.Name, StringComparison.Ordinal)), trait],
    };
    internal GenerationContext WithActiveHandler(string capability) => this with
    {
        ActiveHandlers = new SortedSet<string>(ActiveHandlers, StringComparer.Ordinal) { capability },
    };
    internal GenerationContext WithFlags(GenerationFlags flags) => this with { Flags = flags };
    internal GenerationContext WithFlag(GenerationFlags flag) => this with { Flags = Flags | flag };
    internal GenerationContext WithoutFlag(GenerationFlags flag) => this with { Flags = Flags & ~flag };
    internal GenerationContext WithOwnershipInterests(IEnumerable<OwnershipInterest> interests) => this with
    {
        OwnershipInterests = new SortedSet<OwnershipInterest>(interests),
    };
    internal GenerationContext WithTemplate(string id) => this with { ActiveTemplates = new SortedSet<string>(ActiveTemplates, StringComparer.Ordinal) { id } };
    internal GenerationContext WithFeature(GeneratedFeature feature) => this with
    {
        CurrentFeatures = new SortedSet<GeneratedFeature>(CurrentFeatures) { feature },
    };
    internal bool Allows(GenerationFlags flag) => (Flags & flag) == flag;
    internal bool IsInterestedIn(OwnershipInterest interest) => OwnershipInterests.Contains(interest);
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
    internal static GenerationBudget Create(int maximumNodes) => new(maximumNodes, 12, 17, 8, 4, 4, 5, 3, 4, maximumNodes * 48);
    internal bool IsLeaf => RemainingNodes <= 2 || RemainingDepth <= 1;
    internal GenerationBudget Descend(int nodes = 1) => this with { RemainingNodes = Math.Max(0, RemainingNodes - nodes), RemainingDepth = Math.Max(0, RemainingDepth - 1) };
    internal GenerationBudget LimitNodes(int maximumNodes) => this with { RemainingNodes = Math.Min(RemainingNodes, maximumNodes) };
    internal GenerationBudget LimitDepth(int maximumDepth) => this with { RemainingDepth = Math.Min(RemainingDepth, maximumDepth) };
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
    GenerationBudget Budget)
{
    internal IReadOnlySet<string> ExpectedDiagnosticCodes { get; init; } =
        new SortedSet<string>(StringComparer.Ordinal);
}
