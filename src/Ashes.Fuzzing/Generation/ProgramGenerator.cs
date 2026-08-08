using Ashes.Frontend;
using Ashes.Fuzzing.Combinations;
using Ashes.Fuzzing.Configuration;
using Ashes.Fuzzing.Coverage;

namespace Ashes.Fuzzing.Generation;

using AshesFormatter = Ashes.Formatter.Formatter;
using FrontendProgram = Ashes.Frontend.Program;

internal sealed class ProgramGenerator
{
    private readonly GeneratorRegistry _rules;
    private readonly CombinationRegistry _combinations;

    internal ProgramGenerator(GeneratorRegistry rules, CombinationRegistry combinations)
    {
        _rules = rules;
        _combinations = combinations;
    }

    internal GeneratedFuzzCase Generate(ulong masterSeed, int caseIndex, FuzzProfile profile, int maximumNodes)
    {
        ulong caseSeed = FuzzRandom.DeriveCaseSeed(masterSeed, caseIndex);
        GenerationBudget budget = GenerationBudget.Create(maximumNodes);
        string[] enabledRuleIds = profile.EnabledRules.Order(StringComparer.Ordinal).ToArray();
        string preferredRule = enabledRuleIds[caseIndex % enabledRuleIds.Length];
        string[] enabledCombinationIds = profile.EnabledCombinations.Order(StringComparer.Ordinal).ToArray();
        string? preferredCombination = enabledCombinationIds.Length == 0 ? null : enabledCombinationIds[caseIndex % enabledCombinationIds.Length];
        GenerationCoverageGuidance coverage = new(enabledRuleIds, enabledCombinationIds);
        CombinationGenerator? combinations = profile.EnabledCombinations.Count == 0
            ? null
            : new CombinationGenerator(_combinations, profile.EnabledCombinations, coverage, preferredCombination);
        ExpressionGenerator expressions = new(_rules, profile.EnabledRules, coverage, combinations, preferredRule);
        FuzzRandom random = new(caseSeed);
        AshesType type = profile.Types[random.Next(profile.Types.Count)];
        GenerationBudget expressionBudget = budget.Descend(2);
        GenerationResult<Expr> generated = expressions.Generate(type, GenerationContext.Empty, expressionBudget, random);
        for (int attempt = 1; generated.Features.Count < profile.MinimumFeatureCount && attempt < 16; attempt++)
        {
            random = new FuzzRandom(caseSeed + (ulong)attempt);
            type = profile.Types[random.Next(profile.Types.Count)];
            generated = expressions.Generate(type, GenerationContext.Empty, expressionBudget, random);
        }
        if (generated.Features.Count < profile.MinimumFeatureCount)
        {
            throw new InvalidOperationException($"Generation did not meet profile '{profile.Id}' minimum feature count {profile.MinimumFeatureCount}.");
        }
        generated = ConstrainRootType(generated, type);

        List<TopLevelItem> items = BuildDeclarations(type, generated.Features);
        FrontendProgram program = new(items, generated.Value);
        string source = AshesFormatter.Format(program);
        AstCoverageMetrics metrics = AstCoverageMetrics.Measure(program);
        if (metrics.Nodes > maximumNodes || source.Length > budget.MaximumSourceLength)
        {
            GenerationResult<Expr> leaf = ExpressionGenerator.GenerateLeaf(type, GenerationContext.Empty, budget, random);
            generated = ConstrainRootType(leaf, type);
            program = new FrontendProgram(BuildDeclarations(type, generated.Features), generated.Value);
            source = AshesFormatter.Format(program);
            metrics = AstCoverageMetrics.Measure(program);
        }
        if (metrics.Nodes > maximumNodes || source.Length > budget.MaximumSourceLength)
        {
            throw new ArgumentException($"Generation budget {maximumNodes} is too small for the minimum valid '{type}' program in profile '{profile.Id}'.");
        }
        return new GeneratedFuzzCase(masterSeed, caseSeed, caseIndex, profile.Id, type, program, source, generated.Features, generated.Trace, metrics.Nodes, budget);
    }

    private static GenerationResult<Expr> ConstrainRootType(GenerationResult<Expr> generated, AshesType type)
    {
        const string resultName = "generatedResult";
        Expr.Let typed = new(resultName, generated.Value, new Expr.Var(resultName)) { TypeAnnotation = type.ToSyntax() };
        GeneratedFeatureSet features = generated.Features.Copy();
        features.Add(GeneratedFeature.Let);
        features.Add(GeneratedFeature.Variable);
        return new GenerationResult<Expr>(typed, type, features, GenerationTrace.Merge($"typed-root:{type}", generated.Trace), generated.NodeCount + 2);
    }

    private static List<TopLevelItem> BuildDeclarations(AshesType type, GeneratedFeatureSet features)
    {
        List<TopLevelItem> items = [];
        if (features.Contains(GeneratedFeature.Adt))
        {
            TypeDecl boxDeclaration = new("FuzzBoxType", [new TypeParameter("a")], [new TypeConstructor("FuzzBox", [new TypeExpr.Named("a")])]);
            items.Add(new TopLevelItem.Type(boxDeclaration));

            TypeExpr treeOfA = new TypeExpr.Applied("FuzzTree", [new TypeExpr.Named("a")]);
            TypeDecl treeDeclaration = new("FuzzTree", [new TypeParameter("a")],
            [
                new TypeConstructor("FuzzEmpty", []),
                new TypeConstructor("FuzzLeaf", [new TypeExpr.Named("a")]),
                new TypeConstructor("FuzzBranch", [treeOfA, treeOfA]),
            ]);
            items.Add(new TopLevelItem.Type(treeDeclaration));
        }
        if (features.Contains(GeneratedFeature.Record) || ContainsType(type, "FuzzRecord"))
        {
            TypeConstructor fields = new("FuzzRecord", [AshesType.Int.ToSyntax(), AshesType.Bool.ToSyntax()]) { FieldNames = ["first", "second"] };
            TypeDecl declaration = new("FuzzRecord", [], [fields]) { IsRecord = true };
            items.Add(new TopLevelItem.Type(declaration));
        }
        if (features.Contains(GeneratedFeature.Capability))
        {
            TypeExpr signature = new TypeExpr.Arrow(new TypeExpr.Named("Unit"), new TypeExpr.Named("a"));
            CapabilityDecl declaration = new("FuzzCapability", [new TypeParameter("a")], [new CapabilityOperation("get", signature)]);
            items.Add(new TopLevelItem.Capability(declaration));
        }
        return items;
    }

    private static bool ContainsType(AshesType type, string name) => type switch
    {
        AshesType.Adt adt => string.Equals(adt.Name, name, StringComparison.Ordinal) || adt.Arguments.Any(argument => ContainsType(argument, name)),
        AshesType.Tuple tuple => tuple.Elements.Any(element => ContainsType(element, name)),
        AshesType.List list => ContainsType(list.Element, name),
        AshesType.Function function => ContainsType(function.Parameter, name) || ContainsType(function.Return, name),
        AshesType.Result result => ContainsType(result.Error, name) || ContainsType(result.Value, name),
        AshesType.Task task => ContainsType(task.Error, name) || ContainsType(task.Value, name),
        AshesType.Record record => string.Equals(record.Name, name, StringComparison.Ordinal),
        _ => false,
    };
}
