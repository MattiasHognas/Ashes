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
        bool forcePreferredCombination = preferredCombination is not null && profile.MinimumFeatureCount > 0;
        GenerationCoverageGuidance coverage = new(enabledRuleIds, enabledCombinationIds);
        CombinationGenerator? combinations = profile.EnabledCombinations.Count == 0
            ? null
            : new CombinationGenerator(_combinations, profile.EnabledCombinations, coverage, preferredCombination);
        ExpressionGenerator expressions = new(
            _rules,
            profile.EnabledRules,
            coverage,
            combinations,
            preferredRule,
            forcePreferredCombination);
        FuzzRandom random = new(caseSeed);
        GeneratedProgramPrelude prelude = ProgramPreludeGenerator.Generate(caseIndex);
        int preludeNodes = AstCoverageMetrics.Measure(new FrontendProgram(prelude.Items, new Expr.IntLit(0))).Nodes - 1;
        int availableExpressionNodes = Math.Max(2, maximumNodes - preludeNodes - 2);
        GenerationBudget expressionBudget = budget.Descend(2).LimitNodes(availableExpressionNodes);
        AshesType type = SelectType(profile, preferredCombination, forcePreferredCombination, prelude.Context, expressionBudget, random);
        GenerationResult<Expr> generated = expressions.Generate(type, prelude.Context, expressionBudget, random);
        for (int attempt = 1; generated.Features.Count < profile.MinimumFeatureCount && attempt < 16; attempt++)
        {
            random = new FuzzRandom(caseSeed + (ulong)attempt);
            type = SelectType(profile, preferredCombination, forcePreferredCombination, prelude.Context, expressionBudget, random);
            generated = expressions.Generate(type, prelude.Context, expressionBudget, random);
        }
        if (generated.Features.Count < profile.MinimumFeatureCount)
        {
            throw new InvalidOperationException($"Generation did not meet profile '{profile.Id}' minimum feature count {profile.MinimumFeatureCount}.");
        }
        generated = ConstrainRootType(generated, type);
        generated.Features.UnionWith(prelude.Features);
        generated = generated with { Trace = GenerationTrace.Merge("program", prelude.Trace, generated.Trace) };

        List<TopLevelItem> items = [.. prelude.Items, .. BuildFeatureDeclarations(generated.Features)];
        FrontendProgram program = new(items, generated.Value);
        string source = AshesFormatter.Format(program);
        AstCoverageMetrics metrics = AstCoverageMetrics.Measure(program);
        IReadOnlyList<string> budgetErrors = GenerationBudgetValidator.Validate(program, generated.Trace, source.Length, budget);
        if (budgetErrors.Count != 0)
        {
            GenerationResult<Expr> leaf = ExpressionGenerator.GenerateLeaf(type, prelude.Context, budget, random);
            generated = ConstrainRootType(leaf, type);
            generated.Features.UnionWith(prelude.Features);
            generated = generated with { Trace = GenerationTrace.Merge("program", prelude.Trace, generated.Trace) };
            program = new FrontendProgram([.. prelude.Items, .. BuildFeatureDeclarations(generated.Features)], generated.Value);
            source = AshesFormatter.Format(program);
            metrics = AstCoverageMetrics.Measure(program);
            budgetErrors = GenerationBudgetValidator.Validate(program, generated.Trace, source.Length, budget);
        }
        if (budgetErrors.Count != 0)
        {
            throw new ArgumentException(
                $"Generation budget {maximumNodes} is too small for the minimum valid '{type}' program in profile '{profile.Id}': {string.Join(" ", budgetErrors)}");
        }
        return new GeneratedFuzzCase(masterSeed, caseSeed, caseIndex, profile.Id, type, program, source, generated.Features, generated.Trace, metrics.Nodes, budget);
    }

    private AshesType SelectType(
        FuzzProfile profile,
        string? preferredCombination,
        bool forcePreferredCombination,
        GenerationContext context,
        GenerationBudget budget,
        FuzzRandom random)
    {
        if (forcePreferredCombination && preferredCombination is not null)
        {
            ICombinationTemplate template = _combinations.Get(preferredCombination);
            AshesType[] compatible = profile.Types
                .Where(type => template.CanApply(type, context, budget))
                .OrderBy(type => type.ToString(), StringComparer.Ordinal)
                .ToArray();
            if (compatible.Length == 0)
            {
                throw new InvalidOperationException(
                    $"Profile '{profile.Id}' cannot supply a compatible type for preferred combination '{preferredCombination}'.");
            }
            return compatible[random.Next(compatible.Length)];
        }
        return profile.Types[random.Next(profile.Types.Count)];
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

    private static List<TopLevelItem> BuildFeatureDeclarations(GeneratedFeatureSet features)
    {
        List<TopLevelItem> items = [];
        if (features.Contains(GeneratedFeature.Handler))
        {
            TypeExpr signature = new TypeExpr.Arrow(new TypeExpr.Named("Unit"), new TypeExpr.Named("a"));
            CapabilityDecl declaration = new("FuzzCapability", [new TypeParameter("a")], [new CapabilityOperation("get", signature)]);
            items.Add(new TopLevelItem.Capability(declaration));
        }
        return items;
    }
}
