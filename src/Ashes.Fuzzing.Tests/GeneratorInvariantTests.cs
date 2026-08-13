using Ashes.Frontend;
using Ashes.Fuzzing.Configuration;
using Ashes.Fuzzing.Coverage;
using Ashes.Fuzzing.Generation;
using Ashes.Semantics;
using Shouldly;

namespace Ashes.Fuzzing.Tests;

public sealed class GeneratorInvariantTests
{
    [Test]
    public void LogicalNotRuleProducesTypedSemanticPrograms()
    {
        var fixture = TestFixture.Create();
        GenerationCoverageGuidance coverage = new(["logical-not"], []);
        ExpressionGenerator expressions = new(
            fixture.Rules,
            new HashSet<string>(StringComparer.Ordinal) { "logical-not" },
            coverage);

        GenerationResult<Expr> generated = expressions.Generate(
            AshesType.Bool,
            GenerationContext.Empty,
            GenerationBudget.Create(20),
            new FuzzRandom(20260809));
        Ashes.Frontend.Program program = new(Array.Empty<TopLevelItem>(), generated.Value);
        string source = Ashes.Formatter.Formatter.Format(program);
        Diagnostics diagnostics = new();
        Ashes.Frontend.Program parsed = new Parser(source, diagnostics).ParseProgram();
        Lowering lowering = new(diagnostics);
        _ = lowering.Lower(parsed);

        generated.Type.ShouldBe(AshesType.Bool);
        generated.Value.ShouldBeOfType<Expr.LogicalNot>();
        generated.Features.Contains(GeneratedFeature.LogicalNot).ShouldBeTrue();
        diagnostics.Errors.ShouldBeEmpty(source);
        lowering.LastLoweredType.ShouldNotBeNull();
        lowering.FormatType(lowering.LastLoweredType).ShouldBe("Bool");
    }

    [Test]
    public void IdenticalSeedsGenerateIdenticalProgramsAndTraces()
    {
        var fixture = TestFixture.Create();
        GeneratedFuzzCase first = fixture.Generator.Generate(12345, 17, fixture.Profiles.Get("semantics"), 60);
        GeneratedFuzzCase second = fixture.Generator.Generate(12345, 17, fixture.Profiles.Get("semantics"), 60);
        first.Source.ShouldBe(second.Source);
        first.Trace.Entries.ShouldBe(second.Trace.Entries);
        first.CaseSeed.ShouldBe(second.CaseSeed);
    }

    [Test]
    public void GeneratedProgramsRespectBudgetAndScope()
    {
        var fixture = TestFixture.Create();
        for (int index = 0; index < 100; index++)
        {
            GeneratedFuzzCase generated = fixture.Generator.Generate(77, index, fixture.Profiles.Get("smoke"), 40);
            generated.NodeCount.ShouldBeLessThanOrEqualTo(40);
            generated.NodeCount.ShouldBe(AstCoverageMetrics.Measure(generated.Program).Nodes);
            generated.Source.Length.ShouldBeLessThanOrEqualTo(generated.Budget.MaximumSourceLength);
            GenerationBudgetValidator.Validate(generated.Program, generated.Trace, generated.Source.Length, generated.Budget).ShouldBeEmpty();
            new AstInvariantValidator().ValidateScope(generated.Program).ShouldBeEmpty();
        }
    }

    [Test]
    public void BudgetValidatorRejectsEveryBoundedDimension()
    {
        TypeDecl type = new("BudgetType", [], [new TypeConstructor("BudgetValue", [])]);
        Expr recursive = new Expr.LetRecursive(
            "loop",
            new Expr.Lambda(
                "value",
                new Expr.Match(
                    new Expr.ListLit([new Expr.IntLit(1), new Expr.IntLit(2)]),
                    [
                        new MatchCase(new Pattern.EmptyList(), new Expr.IntLit(0)),
                        new MatchCase(new Pattern.Cons(new Pattern.Wildcard(), new Pattern.Wildcard()), new Expr.IntLit(1)),
                    ])),
            new Expr.IntLit(0));
        Ashes.Frontend.Program program = new([new TopLevelItem.Type(type)], recursive);
        GenerationBudget budget = new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        GenerationTrace trace = new(["combination:test"]);

        IReadOnlyList<string> errors = GenerationBudgetValidator.Validate(program, trace, 1, budget);

        foreach (string dimension in new[]
        {
            "nodes", "depth", "declarations", "functions", "ADTs", "match cases",
            "collection length", "recursion complexity", "combinations", "source length",
        })
        {
            errors.ShouldContain(error => error.Contains(dimension, StringComparison.Ordinal));
        }
    }

    [Test]
    public void GeneratedValidProgramsFormatAndParse()
    {
        var fixture = TestFixture.Create();
        for (int index = 0; index < 50; index++)
        {
            GeneratedFuzzCase generated = fixture.Generator.Generate(991, index, fixture.Profiles.Get("syntax"), 50);
            Diagnostics diagnostics = new();
            _ = new Parser(generated.Source, diagnostics).ParseProgram();
            diagnostics.Errors.ShouldBeEmpty();
        }
    }

    [Test]
    public void ScopeValidationTraversesResultBindingsAndPipelines()
    {
        Expr body = new Expr.TupleLit(
        [
            new Expr.ResultPipe(new Expr.Var("missingPipeInput"), new Expr.Var("missingPipeFunction")),
            new Expr.ResultMapErrorPipe(new Expr.Var("missingMapInput"), new Expr.Var("missingMapFunction")),
            new Expr.LetResult(
                "boundResult",
                new Expr.Var("missingBoundInput"),
                new Expr.TupleLit([new Expr.Var("boundResult"), new Expr.Var("missingBoundBody")])),
        ]);

        IReadOnlyList<string> errors = new AstInvariantValidator().ValidateScope(
            new Ashes.Frontend.Program(Array.Empty<TopLevelItem>(), body));

        foreach (string missing in new[]
        {
            "missingPipeInput",
            "missingPipeFunction",
            "missingMapInput",
            "missingMapFunction",
            "missingBoundInput",
            "missingBoundBody",
        })
        {
            errors.ShouldContain($"'{missing}' is not in scope.");
        }
        errors.ShouldNotContain("'boundResult' is not in scope.");
    }

    [Test]
    public void DifferentCaseIndexesAreIndependentAndDeterministic()
    {
        var fixture = TestFixture.Create();
        GeneratedFuzzCase zero = fixture.Generator.Generate(4, 0, fixture.Profiles.Get("syntax"), 40);
        GeneratedFuzzCase one = fixture.Generator.Generate(4, 1, fixture.Profiles.Get("syntax"), 40);
        zero.CaseSeed.ShouldNotBe(one.CaseSeed);
        fixture.Generator.Generate(4, 1, fixture.Profiles.Get("syntax"), 40).Source.ShouldBe(one.Source);
    }

    [Test]
    public void CompleteProgramsExerciseVariedTopLevelDeclarations()
    {
        var fixture = TestFixture.Create();
        GeneratedFuzzCase adtCase = fixture.Generator.Generate(84, 0, fixture.Profiles.Get("semantics"), 80);
        GeneratedFuzzCase recordCase = fixture.Generator.Generate(84, 1, fixture.Profiles.Get("semantics"), 80);
        GeneratedFuzzCase recursiveCase = fixture.Generator.Generate(84, 2, fixture.Profiles.Get("semantics"), 80);

        adtCase.Program.Items.OfType<TopLevelItem.Type>()
            .ShouldContain(item => item.Decl.Name == "FuzzChoice0" && item.Decl.TypeParameters.Count == 2);
        recordCase.Program.Items.OfType<TopLevelItem.Type>()
            .ShouldContain(item => item.Decl.Name == "FuzzRecordShape1" && item.Decl.IsRecord);
        recursiveCase.Program.Items.OfType<TopLevelItem.RecursiveGroup>().Single().Bindings.Count.ShouldBe(2);

        adtCase.Features.Contains(GeneratedFeature.TopLevelFunction).ShouldBeTrue();
        recursiveCase.Features.Contains(GeneratedFeature.MutualRecursion).ShouldBeTrue();
        new AstInvariantValidator().ValidateScope(adtCase.Program).ShouldBeEmpty();
        new AstInvariantValidator().ValidateScope(recordCase.Program).ShouldBeEmpty();
        new AstInvariantValidator().ValidateScope(recursiveCase.Program).ShouldBeEmpty();
    }

    [Test]
    public void CompleteProgramsExerciseDeterministicCapabilityProviders()
    {
        var fixture = TestFixture.Create();
        GeneratedFuzzCase generated = fixture.Generator.Generate(84, 7, fixture.Profiles.Get("semantics"), 80);

        generated.Program.Items.OfType<TopLevelItem.Capability>()
            .ShouldContain(item => item.Decl.Name == "FuzzProvided7");
        generated.Program.Items.OfType<TopLevelItem.Provide>()
            .ShouldContain(item => item.Decl.CapabilityName == "FuzzProvided7");
        generated.Features.Contains(GeneratedFeature.Provider).ShouldBeTrue();
        new AstInvariantValidator().ValidateScope(generated.Program).ShouldBeEmpty();
    }

    [Test]
    public void ProviderAndCapabilityDeclarationsFitTheGenerationBudget()
    {
        var fixture = TestFixture.Create();
        FuzzProfile profile = new(
            "provider-capability-budget",
            fixture.Rules.Rules.Select(rule => rule.Id).ToHashSet(StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal) { "capability.result-operation" },
            ["parse", "format", "semantic", "ir"],
            [AshesType.Int],
            2);

        GeneratedFuzzCase generated = fixture.Generator.Generate(20260809, 7, profile, 120);

        generated.Trace.Entries.ShouldContain(entry =>
            entry.Contains("capability:result", StringComparison.Ordinal));
        generated.Features.Contains(GeneratedFeature.Provider).ShouldBeTrue();
        generated.Features.Contains(GeneratedFeature.Capability).ShouldBeTrue();
        GenerationBudgetValidator.Validate(
            generated.Program,
            generated.Trace,
            generated.Source.Length,
            generated.Budget).ShouldBeEmpty();
    }

    [Test]
    public void RecordGenerationUsesTheDeclaredContextSchema()
    {
        var fixture = TestFixture.Create();
        GeneratedProgramPrelude prelude = ProgramPreludeGenerator.Generate(1);
        AshesType.Record requiredType = new("FuzzRecordShape1");
        GenerationCoverageGuidance coverage = new(["record"], []);
        ExpressionGenerator expressions = new(
            fixture.Rules,
            new HashSet<string>(StringComparer.Ordinal) { "record" },
            coverage,
            preferredRule: "record");

        GenerationResult<Expr> generated = expressions.Generate(
            requiredType,
            prelude.Context,
            GenerationBudget.Create(40),
            new FuzzRandom(1201));
        Ashes.Frontend.Program program = new(prelude.Items, generated.Value);
        string source = Ashes.Formatter.Formatter.Format(program);
        Diagnostics diagnostics = new();
        Ashes.Frontend.Program parsed = new Parser(source, diagnostics).ParseProgram();
        Lowering lowering = new(diagnostics);
        _ = lowering.Lower(parsed);

        generated.Type.ShouldBe(requiredType);
        generated.Value.ShouldBeOfType<Expr.RecordLit>().TypeName.ShouldBe("FuzzRecordShape1");
        diagnostics.Errors.ShouldBeEmpty(source);
        lowering.LastLoweredType.ShouldNotBeNull();
        lowering.FormatType(lowering.LastLoweredType).ShouldBe("FuzzRecordShape1");
    }

    [Test]
    public void Generated_external_resource_round_trips_and_lowers_ownership_metadata()
    {
        GeneratedProgramPrelude prelude = ProgramPreludeGenerator.Generate(5);
        Ashes.Frontend.Program program = new(prelude.Items, new Expr.IntLit(0));
        string source = Ashes.Formatter.Formatter.Format(program);
        Diagnostics diagnostics = new();
        Ashes.Frontend.Program parsed = new Parser(source, diagnostics).ParseProgram();
        Lowering lowering = new(diagnostics);
        IrProgram ir = lowering.Lower(parsed);

        diagnostics.Errors.ShouldBeEmpty(source);
        prelude.Features.Contains(GeneratedFeature.ExternalResource).ShouldBeTrue();
        prelude.Features.Contains(GeneratedFeature.FfiBuffer).ShouldBeTrue();
        prelude.Features.Contains(GeneratedFeature.FfiOut).ShouldBeTrue();
        prelude.Features.Contains(GeneratedFeature.AmbientAuthority).ShouldBeTrue();
        prelude.Trace.Entries.ShouldContain("program:external-resource");
        prelude.Trace.Entries.ShouldContain("program:ffi-buffer");
        prelude.Trace.Entries.ShouldContain("program:ffi-out");
        prelude.Trace.Entries.ShouldContain("program:ambient-authority");
        source.ShouldContain("external type FuzzResource5 resource destructor fuzzResourceClose5");
        source.ShouldContain("borrow FuzzResource5");
        source.ShouldContain("consume FuzzResource5");
        IrExternalFunction inspect = ir.ExternalFunctions.Single(function => string.Equals(
            function.Name,
            "fuzzResourceInspect5",
            StringComparison.Ordinal));
        inspect.ParameterOwnerships.ShouldBe([FfiParameterOwnership.Borrow]);
        inspect.RuntimeCapabilities.ShouldBe(["Entropy"]);
        source.ShouldContain("needs {Entropy}");
        source.ShouldContain("external type FuzzOpaque5");
        source.ShouldContain("external fuzzBufferInspect5(FfiBuffer(FuzzOpaque5), u64) -> Int");
        IrExternalFunction bufferInspect = ir.ExternalFunctions.Single(function => string.Equals(
            function.Name,
            "fuzzBufferInspect5",
            StringComparison.Ordinal));
        bufferInspect.ParameterTypes.ShouldBe([
            new FfiType.Buffer(new FfiType.Opaque("FuzzOpaque5")),
            new FfiType.UInt(64)
        ]);
        source.ShouldContain("external fuzzResolve5(Str, out FuzzOpaque5, out *u8) -> Bool");
        IrExternalFunction resolve = ir.ExternalFunctions.Single(function => string.Equals(
            function.Name,
            "fuzzResolve5",
            StringComparison.Ordinal));
        resolve.ParameterTypes.ShouldBe([
            new FfiType.Str(),
            new FfiType.Out(new FfiType.Opaque("FuzzOpaque5")),
            new FfiType.Out(new FfiType.Ptr(new FfiType.UInt(8)))
        ]);
    }

    [Test]
    public void Generated_alias_and_zero_cost_type_round_trip_and_lower()
    {
        GeneratedProgramPrelude prelude = ProgramPreludeGenerator.Generate(4);
        Ashes.Frontend.Program program = new(prelude.Items, new Expr.Call(
            new Expr.Var("FuzzUserIdValue4"),
            new Expr.IntLit(42)));
        string source = Ashes.Formatter.Formatter.Format(program);
        Diagnostics diagnostics = new();
        Ashes.Frontend.Program parsed = new Parser(source, diagnostics).ParseProgram();
        Lowering lowering = new(diagnostics);
        _ = lowering.Lower(parsed);

        diagnostics.Errors.ShouldBeEmpty(source);
        prelude.Features.Contains(GeneratedFeature.TypeAlias).ShouldBeTrue();
        prelude.Features.Contains(GeneratedFeature.ZeroCostType).ShouldBeTrue();
        source.ShouldContain("type alias FuzzIdentifier4 = Int");
        source.ShouldContain("type FuzzUserId4 = FuzzUserIdValue4(FuzzIdentifier4)");
        lowering.LastLoweredType.ShouldNotBeNull();
        lowering.FormatType(lowering.LastLoweredType).ShouldBe("FuzzUserId4");
    }

    [Test]
    public void AdtGenerationUsesTheDeclaredGenericContextSchema()
    {
        var fixture = TestFixture.Create();
        GeneratedProgramPrelude prelude = ProgramPreludeGenerator.Generate(0);
        AshesType.Adt requiredType = new("FuzzChoice0", [AshesType.Bool, AshesType.Str]);
        GenerationCoverageGuidance coverage = new(["adt"], []);
        ExpressionGenerator expressions = new(
            fixture.Rules,
            new HashSet<string>(StringComparer.Ordinal) { "adt" },
            coverage,
            preferredRule: "adt");

        GenerationResult<Expr> generated = expressions.Generate(
            requiredType,
            prelude.Context,
            GenerationBudget.Create(40),
            new FuzzRandom(1202));
        Ashes.Frontend.Program program = new(prelude.Items, generated.Value);
        string source = Ashes.Formatter.Formatter.Format(program);
        Diagnostics diagnostics = new();
        Ashes.Frontend.Program parsed = new Parser(source, diagnostics).ParseProgram();
        Lowering lowering = new(diagnostics);
        _ = lowering.Lower(parsed);

        generated.Type.ShouldBe(requiredType);
        generated.Trace.Entries.ShouldContain(entry => entry.StartsWith("adt:Fuzz", StringComparison.Ordinal));
        diagnostics.Errors.ShouldBeEmpty(source);
        lowering.LastLoweredType.ShouldNotBeNull();
        lowering.FormatType(lowering.LastLoweredType).ShouldBe("FuzzChoice0<Bool, Str>");
    }
}
