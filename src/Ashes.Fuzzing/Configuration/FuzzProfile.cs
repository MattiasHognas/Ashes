namespace Ashes.Fuzzing.Configuration;

internal sealed record FuzzProfileDefaults(
    int Cases,
    int MaximumNodes,
    int CompilerTimeoutSeconds,
    int ProgramTimeoutSeconds,
    IReadOnlyList<string> Targets)
{
    internal static FuzzProfileDefaults Standard { get; } = new(100, 80, 20, 5, ["host"]);
}

internal sealed record FuzzProfile(
    string Id,
    IReadOnlySet<string> EnabledRules,
    IReadOnlySet<string> EnabledCombinations,
    IReadOnlyList<string> Oracles,
    IReadOnlyList<Generation.AshesType> Types,
    int MinimumFeatureCount,
    bool MutateSource = false,
    bool Native = false,
    bool Differential = false,
    Generation.GenerationFlags ContextFlags = Generation.GenerationFlags.RecursionAllowed | Generation.GenerationFlags.SuspensionAllowed,
    IReadOnlySet<Generation.OwnershipInterest>? OwnershipInterests = null,
    FuzzProfileDefaults? Defaults = null,
    IReadOnlyList<Generation.AshesType.Resource>? ResourceTypes = null,
    bool GenerateTraits = false,
    bool GenerateInvalidSemantics = false)
{
    internal FuzzProfileDefaults EffectiveDefaults => Defaults ?? FuzzProfileDefaults.Standard;
    internal IReadOnlyList<Generation.AshesType.Resource> EffectiveResourceTypes => ResourceTypes ?? [];
}

internal sealed class FuzzProfileRegistry
{
    private readonly SortedDictionary<string, FuzzProfile> _profiles = new(StringComparer.Ordinal);

    internal IReadOnlyCollection<FuzzProfile> Profiles => _profiles.Values;

    internal void Register(FuzzProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.Id) || !_profiles.TryAdd(profile.Id, profile))
        {
            throw new ArgumentException($"Duplicate or invalid fuzz profile '{profile.Id}'.");
        }
    }

    internal FuzzProfile Get(string id) => _profiles.TryGetValue(id, out FuzzProfile? profile)
        ? profile
        : throw new ArgumentException($"Unknown fuzz profile '{id}'.");

    internal static FuzzProfileRegistry CreateDefault(Generation.GeneratorRegistry rules, Combinations.CombinationRegistry combinations)
    {
        string[] allRules = rules.Rules.Select(rule => rule.Id).ToArray();
        string[] allCombinations = combinations.Templates.Select(template => template.Id).ToArray();
        string[] defaultCombinations = allCombinations.Where(id =>
            !id.StartsWith("resource.", StringComparison.Ordinal)
            && !id.StartsWith("trait.", StringComparison.Ordinal)).ToArray();
        string[] traitCombinations = allCombinations.Where(id =>
            !id.StartsWith("resource.", StringComparison.Ordinal)).ToArray();
        Generation.AshesType[] scalarTypes = [Generation.AshesType.Int, Generation.AshesType.Bool, Generation.AshesType.Str, Generation.AshesType.Float, Generation.AshesType.BigInt, new Generation.AshesType.UInt(8), new Generation.AshesType.UInt(16), new Generation.AshesType.UInt(32), new Generation.AshesType.UInt(64)];
        Generation.AshesType[] aggregateTypes =
        [
            new Generation.AshesType.List(Generation.AshesType.Int),
            new Generation.AshesType.List(Generation.AshesType.Str),
            new Generation.AshesType.Tuple([Generation.AshesType.Bool, Generation.AshesType.Str]),
            new Generation.AshesType.Tuple([Generation.AshesType.Int, Generation.AshesType.Int]),
            new Generation.AshesType.Tuple([Generation.AshesType.Str, Generation.AshesType.Str]),
            new Generation.AshesType.Tuple([Generation.AshesType.Int, new Generation.AshesType.List(Generation.AshesType.Int)]),
            new Generation.AshesType.Tuple([Generation.AshesType.Str, new Generation.AshesType.List(Generation.AshesType.Str)]),
            new Generation.AshesType.Tuple(
            [
                new Generation.AshesType.Function(Generation.AshesType.Int, Generation.AshesType.Str),
                new Generation.AshesType.Function(Generation.AshesType.Int, Generation.AshesType.Str),
            ]),
            new Generation.AshesType.Record("FuzzRecord"),
            new Generation.AshesType.Result(Generation.AshesType.Str, Generation.AshesType.Int),
            new Generation.AshesType.Result(Generation.AshesType.Str, new Generation.AshesType.List(Generation.AshesType.Int)),
            new Generation.AshesType.Adt("FuzzTree", [Generation.AshesType.Int]),
            new Generation.AshesType.Adt("FuzzTree", [Generation.AshesType.Str]),
            new Generation.AshesType.Adt("FuzzMaybe", [Generation.AshesType.Int]),
            new Generation.AshesType.Adt("FuzzMaybe", [Generation.AshesType.Str]),
            new Generation.AshesType.Function(Generation.AshesType.Int, Generation.AshesType.Str),
            new Generation.AshesType.Task(Generation.AshesType.Str, Generation.AshesType.Int),
            new Generation.AshesType.Task(Generation.AshesType.Str, new Generation.AshesType.Record("FuzzRecord")),
        ];
        Generation.AshesType[] allTypes = [.. scalarTypes, .. aggregateTypes];
        Generation.AshesType[] observableTypes = allTypes.Where(IsObservable).ToArray();
        string[] observableCombinations = CompatibleCombinations(
            defaultCombinations,
            combinations,
            observableTypes);
        string[] memoryCombinations = observableCombinations
            .Where(id => !id.StartsWith("async.", StringComparison.Ordinal))
            .ToArray();
        IReadOnlySet<Generation.OwnershipInterest> ownershipInterests = Enum.GetValues<Generation.OwnershipInterest>()
            .ToHashSet();
        FuzzProfileRegistry registry = new();
        registry.Register(new FuzzProfile("syntax", allRules.ToHashSet(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal), ["parse", "format"], allTypes, 0, Defaults: Defaults(200, 80)));
        registry.Register(new FuzzProfile("semantics", allRules.ToHashSet(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal), ["parse", "format", "semantic", "ir"], allTypes, 0, Defaults: Defaults(100, 80)));
        registry.Register(new FuzzProfile("perceus", allRules.ToHashSet(StringComparer.Ordinal), defaultCombinations.ToHashSet(StringComparer.Ordinal), ["parse", "format", "semantic", "ir"], allTypes, 2, OwnershipInterests: ownershipInterests, Defaults: Defaults(100, 100)));
        registry.Register(new FuzzProfile("combinations", allRules.ToHashSet(StringComparer.Ordinal), defaultCombinations.ToHashSet(StringComparer.Ordinal), ["parse", "format", "semantic", "ir"], allTypes, 2, OwnershipInterests: ownershipInterests, Defaults: Defaults(100, 100)));
        registry.Register(new FuzzProfile("compile", allRules.ToHashSet(StringComparer.Ordinal), observableCombinations.ToHashSet(StringComparer.Ordinal), ["parse", "format", "semantic", "execution"], observableTypes, 1, Native: true, OwnershipInterests: ownershipInterests, Defaults: Defaults(10, 50, compilerTimeout: 30)));
        registry.Register(new FuzzProfile("differential", allRules.ToHashSet(StringComparer.Ordinal), observableCombinations.ToHashSet(StringComparer.Ordinal), ["parse", "format", "semantic", "differential-optimization", "differential-reuse"], observableTypes, 1, Native: true, Differential: true, OwnershipInterests: ownershipInterests, Defaults: Defaults(5, 50, compilerTimeout: 30)));
        registry.Register(new FuzzProfile("memory-growth", allRules.ToHashSet(StringComparer.Ordinal), memoryCombinations.ToHashSet(StringComparer.Ordinal), ["parse", "format", "semantic", "ir", "memory-growth"], observableTypes, 1, Native: true, ContextFlags: Generation.GenerationFlags.RecursionAllowed, OwnershipInterests: ownershipInterests, Defaults: Defaults(3, 50, compilerTimeout: 30, programTimeout: 30)));
        registry.Register(new FuzzProfile("cross-target", allRules.ToHashSet(StringComparer.Ordinal), observableCombinations.ToHashSet(StringComparer.Ordinal), ["parse", "format", "semantic", "cross-target"], observableTypes, 1, Native: true, Defaults: Defaults(10, 50, compilerTimeout: 30)));
        registry.Register(new FuzzProfile("invalid-source", allRules.ToHashSet(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal), ["invalid-source"], scalarTypes, 0, MutateSource: true, Defaults: Defaults(250, 80)));
        registry.Register(new FuzzProfile(
            "invalid-semantics",
            allRules.ToHashSet(StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal),
            ["invalid-semantic"],
            scalarTypes,
            0,
            Defaults: Defaults(60, 80),
            GenerateInvalidSemantics: true));
        registry.Register(new FuzzProfile(
            "traits",
            allRules.ToHashSet(StringComparer.Ordinal),
            traitCombinations.ToHashSet(StringComparer.Ordinal),
            ["parse", "format", "semantic", "ir"],
            allTypes,
            3,
            OwnershipInterests: ownershipInterests,
            Defaults: Defaults(100, 140),
            GenerateTraits: true));
        registry.Register(new FuzzProfile(
            "traits-differential",
            allRules.ToHashSet(StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal)
            {
                "trait.constrained-closure",
                "trait.derived-operator-sharing",
            },
            ["parse", "format", "semantic", "differential-trait-evidence"],
            scalarTypes,
            3,
            Native: true,
            Differential: true,
            OwnershipInterests: ownershipInterests,
            Defaults: Defaults(5, 120, compilerTimeout: 30),
            GenerateTraits: true));
        registry.Register(new FuzzProfile("async", allRules.ToHashSet(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal) { "async.capture-across-await", "async.closure-match-across-await", "async.spawn-shared-value", "async.task-result-reuse" }, ["parse", "format", "semantic", "ir"], allTypes, 2, OwnershipInterests: ownershipInterests, Defaults: Defaults(100, 80)));
        registry.Register(new FuzzProfile("capabilities", allRules.ToHashSet(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal) { "capability.deterministic-handler", "capability.nested-handlers", "capability.closure-match", "capability.result-operation", "capability.recursive-list" }, ["parse", "format", "semantic", "ir"], allTypes, 2, Defaults: Defaults(100, 80)));
        registry.Register(new FuzzProfile("resources", allRules.ToHashSet(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal) { "resource.deterministic-file-handle" }, ["parse", "format", "semantic", "ir"], scalarTypes, 2, ContextFlags: Generation.GenerationFlags.RecursionAllowed | Generation.GenerationFlags.ResourcesAllowed, Defaults: Defaults(50, 80), ResourceTypes: [Generation.AshesType.FileHandle]));
        // GenerateTraits is deliberately off here: smoke backs ci_quick (`just ci-quick`), the fast
        // pre-commit inner loop, budgeted for a fixed-seed pass on every commit. Trait declarations and
        // coherent implementations add real generation and validation cost (see the "traits" profile's
        // larger Defaults(100, 140) below); that cost belongs in the traits/traits-differential/
        // invalid-semantics profiles wired into ci/jobs.sh's fuzz() job instead, not on every commit.
        registry.Register(new FuzzProfile("smoke", allRules.ToHashSet(StringComparer.Ordinal), defaultCombinations.ToHashSet(StringComparer.Ordinal), ["parse", "format", "semantic", "ir"], allTypes, 0, OwnershipInterests: ownershipInterests, Defaults: Defaults(40, 40)));
        registry.Register(new FuzzProfile(
            "all",
            allRules.ToHashSet(StringComparer.Ordinal),
            allCombinations.ToHashSet(StringComparer.Ordinal),
            ["parse", "format", "semantic", "ir"],
            allTypes,
            1,
            ContextFlags: Generation.GenerationFlags.RecursionAllowed |
                Generation.GenerationFlags.SuspensionAllowed |
                Generation.GenerationFlags.ResourcesAllowed,
            OwnershipInterests: ownershipInterests,
            Defaults: Defaults(100, 80),
            ResourceTypes: [Generation.AshesType.FileHandle],
            GenerateTraits: true));
        registry.Validate(rules, combinations);
        return registry;
    }

    private static FuzzProfileDefaults Defaults(
        int cases,
        int maximumNodes,
        int compilerTimeout = 20,
        int programTimeout = 5) => new(cases, maximumNodes, compilerTimeout, programTimeout, ["host"]);

    private static bool IsObservable(Generation.AshesType type) => type switch
    {
        Generation.AshesType.Primitive => true,
        Generation.AshesType.UInt => true,
        Generation.AshesType.List list => IsObservable(list.Element),
        Generation.AshesType.Tuple tuple => tuple.Elements.All(IsObservable),
        Generation.AshesType.Record { Name: "FuzzRecord" } => true,
        Generation.AshesType.Result result => IsObservable(result.Error) && IsObservable(result.Value),
        Generation.AshesType.Adt { Name: "FuzzTree" } tree => tree.Arguments.All(IsObservable),
        Generation.AshesType.Adt { Name: "FuzzMaybe" } maybe => maybe.Arguments.All(IsObservable),
        _ => false,
    };

    private static string[] CompatibleCombinations(
        IEnumerable<string> ids,
        Combinations.CombinationRegistry combinations,
        IReadOnlyList<Generation.AshesType> types)
    {
        Generation.GenerationBudget budget = Generation.GenerationBudget.Create(120);
        return ids.Where(id => types.Any(type => combinations.Get(id).CanApply(
                type,
                Generation.GenerationContext.Empty,
                budget)))
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    internal void Validate(Generation.GeneratorRegistry rules, Combinations.CombinationRegistry combinations)
    {
        IReadOnlySet<string> ruleIds = rules.Rules.Select(rule => rule.Id).ToHashSet(StringComparer.Ordinal);
        IReadOnlySet<string> combinationIds = combinations.Templates.Select(template => template.Id).ToHashSet(StringComparer.Ordinal);
        foreach (FuzzProfile profile in _profiles.Values)
        {
            FuzzProfileDefaults defaults = profile.EffectiveDefaults;
            if (defaults.Cases <= 0 || defaults.MaximumNodes <= 0 || defaults.CompilerTimeoutSeconds <= 0 ||
                defaults.ProgramTimeoutSeconds <= 0 || defaults.Targets.Count == 0 ||
                defaults.Targets.Any(target => !FuzzConfiguration.SupportedTargets.Contains(target)) ||
                profile.EnabledRules.Count == 0 || profile.Types.Count == 0 || profile.MinimumFeatureCount < 0)
            {
                throw new ArgumentException($"Profile '{profile.Id}' has invalid campaign defaults.");
            }
            string? unknownRule = profile.EnabledRules.FirstOrDefault(id => !ruleIds.Contains(id));
            string? unknownCombination = profile.EnabledCombinations.FirstOrDefault(id => !combinationIds.Contains(id));
            if (unknownRule is not null || unknownCombination is not null)
            {
                throw new ArgumentException($"Profile '{profile.Id}' references an unknown registry entry '{unknownRule ?? unknownCombination}'.");
            }

            Generation.GenerationContext context = Generation.GenerationContext.Empty
                .WithFlags(profile.ContextFlags)
                .WithResourceTypes(profile.EffectiveResourceTypes)
                .WithOwnershipInterests(profile.OwnershipInterests is null
                    ? Enum.GetValues<Generation.OwnershipInterest>()
                    : profile.OwnershipInterests);
            if (profile.GenerateTraits)
            {
                context = Generation.TraitPreludeGenerator.Generate(0, context).Context;
            }
            if (profile.EffectiveResourceTypes.Any(type => string.IsNullOrWhiteSpace(type.Name)) ||
                profile.EffectiveResourceTypes.Distinct().Count() != profile.EffectiveResourceTypes.Count)
            {
                throw new ArgumentException($"Profile '{profile.Id}' contains duplicate or invalid resource types.");
            }
            Generation.AshesType.Resource? unknownResource = profile.EffectiveResourceTypes
                .FirstOrDefault(type => !Generation.AshesType.SupportedResources.Contains(type));
            if (unknownResource is not null)
            {
                throw new ArgumentException($"Profile '{profile.Id}' references unknown resource type '{unknownResource.Name}'.");
            }
            if (profile.EffectiveResourceTypes.Count > 0 && !context.Allows(Generation.GenerationFlags.ResourcesAllowed))
            {
                throw new ArgumentException($"Profile '{profile.Id}' enables resource types without resource generation.");
            }
            string? incompatibleCombination = profile.EnabledCombinations.FirstOrDefault(id =>
                !profile.Types.Any(type => combinations.Get(id).CanApply(
                    type,
                    context,
                    Generation.GenerationBudget.Create(120))));
            if (incompatibleCombination is not null)
            {
                throw new ArgumentException(
                    $"Profile '{profile.Id}' cannot supply a compatible type for combination '{incompatibleCombination}'.");
            }
        }
    }

    internal void ValidateOracles(Oracles.FuzzOracleRegistry oracles)
    {
        IReadOnlySet<string> oracleIds = oracles.Oracles.Select(oracle => oracle.Id).ToHashSet(StringComparer.Ordinal);
        foreach (FuzzProfile profile in _profiles.Values)
        {
            string? unknown = profile.Oracles.FirstOrDefault(id => !oracleIds.Contains(id));
            if (profile.Oracles.Count == 0 ||
                profile.Oracles.Any(string.IsNullOrWhiteSpace) ||
                profile.Oracles.Distinct(StringComparer.Ordinal).Count() != profile.Oracles.Count ||
                unknown is not null)
            {
                throw new ArgumentException(
                    $"Profile '{profile.Id}' contains duplicate, invalid, or unknown oracle '{unknown ?? "<none>"}'.");
            }
        }
    }
}
