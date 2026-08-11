using Ashes.Fuzzing.Configuration;
using Ashes.Fuzzing.Generation;
using Ashes.Fuzzing.Oracles;
using Shouldly;

namespace Ashes.Fuzzing.Tests;

public sealed class TraitFuzzingTests
{
    [Test]
    public void TraitProfileIsByteAndTraceDeterministic()
    {
        var fixture = TestFixture.Create();
        FuzzProfile profile = fixture.Profiles.Get("traits");

        GeneratedFuzzCase first = fixture.Generator.Generate(73001, 7, profile, 160);
        GeneratedFuzzCase second = fixture.Generator.Generate(73001, 7, profile, 160);

        second.Source.ShouldBe(first.Source);
        second.Trace.Entries.ShouldBe(first.Trace.Entries);
        second.CaseSeed.ShouldBe(first.CaseSeed);
    }

    [Test]
    public void TraitProfileGeneratesCoherentDeclarationsConstraintsAndResolutionTraces()
    {
        var fixture = TestFixture.Create();
        FuzzProfile profile = fixture.Profiles.Get("traits");

        for (int caseIndex = 0; caseIndex < 12; caseIndex++)
        {
            GeneratedFuzzCase generated = fixture.Generator.Generate(73002, caseIndex, profile, 160);
            FuzzSemanticCompilation compilation = FuzzSemanticCompiler.Lower(generated.Source);

            compilation.Diagnostics.StructuredErrors.ShouldBeEmpty(generated.Source);
            generated.Features.Contains(GeneratedFeature.TraitDeclaration).ShouldBeTrue();
            generated.Features.Contains(GeneratedFeature.TraitImplementation).ShouldBeTrue();
            generated.Features.Contains(GeneratedFeature.TraitConstraint).ShouldBeTrue();
            generated.Features.Contains(GeneratedFeature.TraitResolution).ShouldBeTrue();
            generated.Features.Contains(GeneratedFeature.MultiParameterTrait).ShouldBeTrue();
            generated.Trace.Entries.ShouldContain(entry =>
                entry.StartsWith("trait:resolution:", StringComparison.Ordinal));
        }
    }

    [Test]
    public void ConstrainedClosureCombinationWorksAcrossEverySupportedTraitType()
    {
        var fixture = TestFixture.Create();
        AshesType[] types = [AshesType.Int, AshesType.Bool, AshesType.Str];
        for (int typeIndex = 0; typeIndex < types.Length; typeIndex++)
        {
            AshesType type = types[typeIndex];
            FuzzProfile profile = new(
                "trait-type-" + typeIndex.ToString(System.Globalization.CultureInfo.InvariantCulture),
                fixture.Rules.Rules.Select(rule => rule.Id).ToHashSet(StringComparer.Ordinal),
                new HashSet<string>(StringComparer.Ordinal) { "trait.constrained-closure" },
                ["parse", "format", "semantic", "ir"],
                [type],
                3,
                GenerateTraits: true);

            GeneratedFuzzCase generated = fixture.Generator.Generate(73003, typeIndex, profile, 160);
            FuzzSemanticCompilation compilation = FuzzSemanticCompiler.Lower(generated.Source);

            generated.Type.ShouldBe(type);
            generated.Trace.Entries.ShouldContain("combination:trait.constrained-closure");
            generated.Features.Contains(GeneratedFeature.ClosureCapture).ShouldBeTrue();
            generated.Features.Contains(GeneratedFeature.TraitMethodCall).ShouldBeTrue();
            compilation.Diagnostics.StructuredErrors.ShouldBeEmpty(generated.Source);
        }
    }

    [Test]
    public void InvalidSemanticProfileRotatesEveryDeclaredTraitFailureDeterministically()
    {
        var fixture = TestFixture.Create();
        FuzzProfile profile = fixture.Profiles.Get("invalid-semantics");
        var traces = new HashSet<string>(StringComparer.Ordinal);

        for (int caseIndex = 0; caseIndex < 6; caseIndex++)
        {
            GeneratedFuzzCase first = fixture.Generator.Generate(73004, caseIndex, profile, 80);
            GeneratedFuzzCase replay = fixture.Generator.Generate(73004, caseIndex, profile, 80);
            FuzzSemanticCompilation compilation = FuzzSemanticCompiler.Lower(first.Source);
            string[] actualCodes = compilation.Diagnostics.StructuredErrors
                .Select(error => error.Code ?? "<uncoded>")
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            replay.Source.ShouldBe(first.Source);
            replay.Trace.Entries.ShouldBe(first.Trace.Entries);
            first.ExpectedDiagnosticCodes.ShouldAllBe(code => actualCodes.Contains(code, StringComparer.Ordinal));
            compilation.Diagnostics.StructuredErrors.Count.ShouldBeInRange(1, 64);
            traces.Add(first.Trace.Entries.Single(entry =>
                entry.StartsWith("invalid-semantic:", StringComparison.Ordinal)));
        }

        traces.Count.ShouldBe(6);
    }
}
