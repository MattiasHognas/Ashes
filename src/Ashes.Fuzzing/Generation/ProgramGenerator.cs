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

    /// <summary>The profile's templates in the order its cases force them; see <see cref="CombinationRegistry.RotationOrder"/>.</summary>
    internal string[] RotationOrder(FuzzProfile profile) => _combinations.RotationOrder(profile.EnabledCombinations);

    internal GeneratedFuzzCase Generate(ulong masterSeed, int caseIndex, FuzzProfile profile, int maximumNodes)
    {
        if (profile.GenerateInvalidSemantics)
        {
            return TraitInvalidCaseGenerator.Generate(masterSeed, caseIndex, profile, maximumNodes);
        }

        ulong caseSeed = FuzzRandom.DeriveCaseSeed(masterSeed, caseIndex);
        GenerationBudget budget = GenerationBudget.Create(maximumNodes);
        if (profile.GenerateTraits)
        {
            budget = budget with
            {
                RemainingDeclarations = Math.Max(budget.RemainingDeclarations, 29),
                RemainingFunctions = Math.Max(budget.RemainingFunctions, 16),
                RemainingAdts = Math.Max(budget.RemainingAdts, 6),
            };
        }
        string[] enabledRuleIds = profile.EnabledRules.Order(StringComparer.Ordinal).ToArray();
        string preferredRule = enabledRuleIds[caseIndex % enabledRuleIds.Length];
        string[] enabledCombinationIds = RotationOrder(profile);
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
        GeneratedDeclarations? declarations = DeclareForcedTemplate(preferredCombination, forcePreferredCombination, caseSeed);
        GeneratedProgramPrelude prelude = ProgramPreludeGenerator.Generate(caseIndex, profile.GenerateTraits, stableOnly: declarations is not null);
        GenerationContext context = prelude.Context
            .WithFlags(profile.ContextFlags | GenerationFlags.TailPosition)
            .WithResourceTypes(profile.EffectiveResourceTypes)
            .WithOwnershipInterests(profile.OwnershipInterests is null
                ? Enum.GetValues<OwnershipInterest>()
                : profile.OwnershipInterests);
        FrontendProgram measuredPrelude = new(prelude.Items, new Expr.IntLit(0));
        GenerationBudgetUsage preludeUsage = GenerationBudgetValidator.Measure(
            measuredPrelude,
            prelude.Trace,
            sourceLength: 0);
        budget = budget with
        {
            // BuildFeatureDeclarations can add one capability declaration after expression generation.
            RemainingDeclarations = Math.Max(budget.RemainingDeclarations, preludeUsage.Declarations + 1),
        };
        if (declarations is not null)
        {
            context = context.WithBinding(declarations.Entry);
            budget = WidenForDeclarations(budget, declarations.Items);
        }
        IReadOnlyList<TopLevelItem> declaredItems = declarations?.Items ?? [];
        GenerationTrace preludeTrace = declarations is null
            ? prelude.Trace
            : new GenerationTrace([.. prelude.Trace.Entries, .. declarations.Trace.Entries]);
        int preludeNodes = AstCoverageMetrics.Measure(measuredPrelude).Nodes - 1;
        int availableExpressionNodes = Math.Max(2, maximumNodes - preludeNodes - 2);
        GenerationBudget selectionBudget = budget.Descend(2).LimitNodes(availableExpressionNodes);
        AshesType type = SelectType(profile, preferredCombination, forcePreferredCombination, context, selectionBudget, random);
        GenerationBudget baseBudget = budget;
        int preludeFunctions = preludeUsage.Functions;
        budget = EnsureMinimumFunctionBudget(baseBudget, type, context, preludeFunctions, profile.GenerateTraits);
        GenerationBudget expressionBudget = budget.Descend(2).LimitNodes(availableExpressionNodes);
        GenerationResult<Expr> generated = expressions.Generate(type, context, expressionBudget, random);
        for (int attempt = 1; generated.Features.Count < profile.MinimumFeatureCount && attempt < 16; attempt++)
        {
            random = new FuzzRandom(caseSeed + (ulong)attempt);
            type = SelectType(profile, preferredCombination, forcePreferredCombination, context, selectionBudget, random);
            budget = EnsureMinimumFunctionBudget(baseBudget, type, context, preludeFunctions, profile.GenerateTraits);
            expressionBudget = budget.Descend(2).LimitNodes(availableExpressionNodes);
            generated = expressions.Generate(type, context, expressionBudget, random);
        }
        if (generated.Features.Count < profile.MinimumFeatureCount)
        {
            throw new InvalidOperationException($"Generation did not meet profile '{profile.Id}' minimum feature count {profile.MinimumFeatureCount}.");
        }
        generated = ConstrainRootType(generated, type);
        generated.Features.UnionWith(prelude.Features);
        generated = generated with { Trace = GenerationTrace.Merge("program", preludeTrace, generated.Trace) };

        List<TopLevelItem> items = [.. prelude.Items, .. declaredItems, .. BuildFeatureDeclarations(generated.Features)];
        FrontendProgram program = new(items, generated.Value);
        string source = AshesFormatter.Format(program);
        AstCoverageMetrics metrics = AstCoverageMetrics.Measure(program);
        IReadOnlyList<string> budgetErrors = GenerationBudgetValidator.Validate(program, generated.Trace, source.Length, budget);
        if (budgetErrors.Count != 0)
        {
            string fallbackReason = "budget-fallback:" + string.Join("|", budgetErrors);
            GenerationResult<Expr> leaf = ExpressionGenerator.GenerateLeaf(type, context, budget, random);
            generated = ConstrainRootType(leaf, type);
            generated.Features.UnionWith(prelude.Features);
            generated = generated with
            {
                Trace = GenerationTrace.Merge("program", preludeTrace, new GenerationTrace([fallbackReason]), generated.Trace),
            };
            program = new FrontendProgram([.. prelude.Items, .. declaredItems, .. BuildFeatureDeclarations(generated.Features)], generated.Value);
            source = AshesFormatter.Format(program);
            metrics = AstCoverageMetrics.Measure(program);
            budgetErrors = GenerationBudgetValidator.Validate(program, generated.Trace, source.Length, budget);
        }
        if (budgetErrors.Count != 0)
        {
            throw new ArgumentException(
                $"Generation budget {maximumNodes} is too small for the minimum valid '{type}' program in profile '{profile.Id}': {string.Join(" ", budgetErrors)}");
        }
        return new GeneratedFuzzCase(masterSeed, caseSeed, caseIndex, profile.Id, type, program, source, generated.Features, generated.Trace, metrics.Nodes, budget)
        {
            RequestedNodes = maximumNodes,
            TrailingWorkload = declarations?.TrailingWorkload,
        };
    }

    // The declarations draw from their own stream, derived from the case seed, so adding them leaves
    // the expression's random choices as they would otherwise be.
    private GeneratedDeclarations? DeclareForcedTemplate(string? preferredCombination, bool forcePreferredCombination, ulong caseSeed) =>
        forcePreferredCombination && preferredCombination is not null &&
        _combinations.Get(preferredCombination) is IDeclaringCombinationTemplate declaring
            ? declaring.Declare(new FuzzRandom(caseSeed ^ 0xD1B54A32D192ED03UL))
            : null;

    // A forced template's declarations are scaffolding its expression calls, so they widen the budget
    // by exactly their own size: the expression keeps the budget it would have had without them.
    private static GenerationBudget WidenForDeclarations(GenerationBudget budget, IReadOnlyList<TopLevelItem> declarations)
    {
        FrontendProgram measured = new(declarations, new Expr.IntLit(0));
        int sourceLength = AshesFormatter.Format(measured).Length;
        GenerationBudgetUsage usage = GenerationBudgetValidator.Measure(measured, GenerationTrace.Empty, sourceLength);
        return budget with
        {
            RemainingNodes = budget.RemainingNodes + usage.Nodes - 1,
            RemainingDepth = Math.Max(budget.RemainingDepth, usage.Depth),
            RemainingDeclarations = budget.RemainingDeclarations + usage.Declarations,
            RemainingFunctions = budget.RemainingFunctions + usage.Functions,
            RemainingAdts = budget.RemainingAdts + usage.Adts,
            MaximumMatchCases = Math.Max(budget.MaximumMatchCases, usage.MaximumMatchCases),
            MaximumCollectionLength = Math.Max(budget.MaximumCollectionLength, usage.MaximumCollectionLength),
            RemainingRecursion = Math.Max(budget.RemainingRecursion, usage.RecursionComplexity),
            MaximumSourceLength = budget.MaximumSourceLength + sourceLength,
        };
    }

    private static GenerationBudget EnsureMinimumFunctionBudget(
        GenerationBudget budget,
        AshesType type,
        GenerationContext context,
        int preludeFunctions,
        bool generateTraits)
    {
        if (!generateTraits)
        {
            return budget;
        }
        int minimumCompleteProgramFunctions = preludeFunctions +
            ExpressionGenerator.MaximumLeafFunctionCount(type, context);
        return budget with
        {
            RemainingFunctions = Math.Max(budget.RemainingFunctions, minimumCompleteProgramFunctions),
        };
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
            if (compatible.Length != 0)
            {
                return compatible[random.Next(compatible.Length)];
            }
            // The registry validates every profile combination against a generous budget, but this
            // case's remaining node budget is whatever the measured prelude left behind. A template
            // that no longer fits degrades to ordinary type selection, like any other exceeded
            // generation dimension, instead of failing the whole campaign.
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
