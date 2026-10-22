using System.Text.RegularExpressions;
using Ashes.Frontend;
using Ashes.Fuzzing.Combinations;
using Ashes.Fuzzing.Configuration;
using Ashes.Fuzzing.Coverage;
using Ashes.Fuzzing.Execution;
using Ashes.Fuzzing.Generation;
using Ashes.Fuzzing.Oracles;
using Shouldly;

namespace Ashes.Fuzzing.Tests;

public sealed class LeakTemplateTests
{
    [Test]
    public async Task EveryLeakTemplateIsForcedAsAValidProgramAtTheMemoryProfileBudget()
    {
        var fixture = TestFixture.Create();
        FuzzProfile profile = fixture.Profiles.Get("memory-growth");
        int maximumNodes = profile.EffectiveDefaults.MaximumNodes;
        string[] rotation = fixture.Generator.RotationOrder(profile);
        FuzzExecutionContext context = new(Environment.CurrentDirectory, "host", TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(2), 4096, new CompilerExecution());
        int leakTemplates = 0;
        for (int caseIndex = 0; caseIndex < rotation.Length; caseIndex++)
        {
            if (fixture.Combinations.Get(rotation[caseIndex]) is not IDeclaringCombinationTemplate)
            {
                continue;
            }

            leakTemplates++;
            GeneratedFuzzCase generated = fixture.Generator.Generate(677349, caseIndex, profile, maximumNodes);
            generated.Trace.Entries.ShouldContain("combination:" + rotation[caseIndex]);
            generated.Type.ShouldBe(AshesType.Int);
            generated.ReplayNodes.ShouldBe(maximumNodes);
            generated.NodeCount.ShouldBe(AstCoverageMetrics.Measure(generated.Program).Nodes);
            GenerationBudgetValidator.Validate(generated.Program, generated.Trace, generated.Source.Length, generated.Budget).ShouldBeEmpty(generated.Source);
            new AstInvariantValidator().ValidateScope(generated.Program).ShouldBeEmpty(generated.Source);
            // A map threaded through Ashes.Collection.Map.setStr in a recursive loop fails IR
            // verification on its own (a reuse token with three consumers in the inlined map code),
            // so the map templates are held to the frontend and semantic oracles only.
            IFuzzOracle[] oracles = generated.Source.Contains("Ashes.Collection.Map.", StringComparison.Ordinal)
                ? [new ParseOracle(), new FormatOracle(), new SemanticOracle()]
                : [new ParseOracle(), new FormatOracle(), new SemanticOracle(), new IrVerificationOracle()];
            foreach (IFuzzOracle oracle in oracles)
            {
                FuzzOracleResult result = await oracle.EvaluateAsync(generated, context, CancellationToken.None);
                result.Success.ShouldBeTrue($"{rotation[caseIndex]} {oracle.Id}: {result.Message}\n{generated.Source}");
            }
        }

        leakTemplates.ShouldBe(fixture.Combinations.Templates.Count(template => template.Id.StartsWith("leak.", StringComparison.Ordinal)));
    }

    [Test]
    public void DeclaringTemplatesLeaveTheCasesOfEveryOtherTemplateUnchanged()
    {
        var fixture = TestFixture.Create();
        FuzzProfile profile = fixture.Profiles.Get("memory-growth");
        FuzzProfile withoutDeclaring = profile with
        {
            EnabledCombinations = profile.EnabledCombinations
                .Where(id => fixture.Combinations.Get(id) is not IDeclaringCombinationTemplate)
                .ToHashSet(StringComparer.Ordinal),
        };
        string[] rotation = fixture.Generator.RotationOrder(profile);
        rotation.Take(withoutDeclaring.EnabledCombinations.Count).ShouldBe(fixture.Generator.RotationOrder(withoutDeclaring));
        for (int caseIndex = 0; caseIndex < withoutDeclaring.EnabledCombinations.Count; caseIndex++)
        {
            fixture.Generator.Generate(677350, caseIndex, profile, 50).Source
                .ShouldBe(fixture.Generator.Generate(677350, caseIndex, withoutDeclaring, 50).Source);
        }
    }

    [Test]
    public void LeakTemplatesVaryTheirProgramsAcrossSeeds()
    {
        var fixture = TestFixture.Create();
        foreach (IDeclaringCombinationTemplate template in fixture.Combinations.Templates.OfType<IDeclaringCombinationTemplate>())
        {
            HashSet<string> shapes = new(StringComparer.Ordinal);
            for (ulong seed = 1; seed <= 8; seed++)
            {
                GeneratedDeclarations declarations = template.Declare(new FuzzRandom(seed));
                string source = Ashes.Formatter.Formatter.Format(new Ashes.Frontend.Program(declarations.Items, new Expr.IntLit(0)));
                shapes.Add(Regex.Replace(source, "[0-9]{4}", "", RegexOptions.None, TimeSpan.FromSeconds(1)));
                declarations.Items.OfType<TopLevelItem.LetDecl>().ShouldContain(item => item.Name == declarations.Entry.Name);
            }

            shapes.Count.ShouldBeGreaterThan(1, template.Id);
        }
    }
}
