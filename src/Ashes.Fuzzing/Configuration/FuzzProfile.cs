namespace Ashes.Fuzzing.Configuration;

internal sealed record FuzzProfile(
    string Id,
    IReadOnlySet<string> EnabledRules,
    IReadOnlySet<string> EnabledCombinations,
    IReadOnlyList<string> Oracles,
    IReadOnlyList<Generation.AshesType> Types,
    int MinimumFeatureCount,
    bool MutateSource = false,
    bool Native = false,
    bool Differential = false);

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
        string[] defaultCombinations = allCombinations.Where(id => !id.StartsWith("resource.", StringComparison.Ordinal)).ToArray();
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
        FuzzProfileRegistry registry = new();
        registry.Register(new FuzzProfile("syntax", allRules.ToHashSet(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal), ["parse", "format"], allTypes, 0));
        registry.Register(new FuzzProfile("semantics", allRules.ToHashSet(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal), ["parse", "format", "semantic", "ir"], allTypes, 0));
        registry.Register(new FuzzProfile("perceus", allRules.ToHashSet(StringComparer.Ordinal), defaultCombinations.ToHashSet(StringComparer.Ordinal), ["parse", "format", "semantic", "ir"], allTypes, 2));
        registry.Register(new FuzzProfile("combinations", allRules.ToHashSet(StringComparer.Ordinal), defaultCombinations.ToHashSet(StringComparer.Ordinal), ["parse", "format", "semantic", "ir"], allTypes, 2));
        registry.Register(new FuzzProfile("compile", allRules.ToHashSet(StringComparer.Ordinal), observableCombinations.ToHashSet(StringComparer.Ordinal), ["parse", "format", "semantic", "execution"], observableTypes, 1, Native: true));
        registry.Register(new FuzzProfile("differential", allRules.ToHashSet(StringComparer.Ordinal), observableCombinations.ToHashSet(StringComparer.Ordinal), ["parse", "format", "semantic", "differential-optimization", "differential-reuse"], observableTypes, 1, Native: true, Differential: true));
        registry.Register(new FuzzProfile("cross-target", allRules.ToHashSet(StringComparer.Ordinal), observableCombinations.ToHashSet(StringComparer.Ordinal), ["parse", "format", "semantic", "cross-target"], observableTypes, 1, Native: true));
        registry.Register(new FuzzProfile("invalid-source", allRules.ToHashSet(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal), ["invalid-source"], scalarTypes, 0, MutateSource: true));
        registry.Register(new FuzzProfile("async", allRules.ToHashSet(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal) { "async.capture-across-await" }, ["parse", "format", "semantic", "ir"], allTypes, 2));
        registry.Register(new FuzzProfile("capabilities", allRules.ToHashSet(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal) { "capability.deterministic-handler", "capability.nested-handlers" }, ["parse", "format", "semantic", "ir"], allTypes, 2));
        registry.Register(new FuzzProfile("resources", allRules.ToHashSet(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal) { "resource.deterministic-file-handle" }, ["parse", "format", "semantic", "ir"], scalarTypes, 2));
        registry.Register(new FuzzProfile("smoke", allRules.ToHashSet(StringComparer.Ordinal), defaultCombinations.ToHashSet(StringComparer.Ordinal), ["parse", "format", "semantic", "ir"], allTypes, 0));
        registry.Register(new FuzzProfile("all", allRules.ToHashSet(StringComparer.Ordinal), defaultCombinations.ToHashSet(StringComparer.Ordinal), ["parse", "format", "semantic", "ir"], allTypes, 1));
        registry.Validate(rules, combinations);
        return registry;
    }

    private static bool IsObservable(Generation.AshesType type) => type switch
    {
        Generation.AshesType.Primitive => true,
        Generation.AshesType.UInt => true,
        Generation.AshesType.List list => IsObservable(list.Element),
        Generation.AshesType.Tuple tuple => tuple.Elements.All(IsObservable),
        Generation.AshesType.Record { Name: "FuzzRecord" } => true,
        Generation.AshesType.Result result => IsObservable(result.Error) && IsObservable(result.Value),
        Generation.AshesType.Adt { Name: "FuzzTree" } tree => tree.Arguments.All(IsObservable),
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
            string? unknownRule = profile.EnabledRules.FirstOrDefault(id => !ruleIds.Contains(id));
            string? unknownCombination = profile.EnabledCombinations.FirstOrDefault(id => !combinationIds.Contains(id));
            if (unknownRule is not null || unknownCombination is not null)
            {
                throw new ArgumentException($"Profile '{profile.Id}' references an unknown registry entry '{unknownRule ?? unknownCombination}'.");
            }

            string? incompatibleCombination = profile.EnabledCombinations.FirstOrDefault(id =>
                !profile.Types.Any(type => combinations.Get(id).CanApply(
                    type,
                    Generation.GenerationContext.Empty,
                    Generation.GenerationBudget.Create(120))));
            if (incompatibleCombination is not null)
            {
                throw new ArgumentException(
                    $"Profile '{profile.Id}' cannot supply a compatible type for combination '{incompatibleCombination}'.");
            }
        }
    }
}
