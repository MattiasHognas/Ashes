using Ashes.Fuzzing.Generation;
using Ashes.Fuzzing.Shrinking;
using Shouldly;

namespace Ashes.Fuzzing.Tests;

public sealed class ShrinkerTests
{
    [Test]
    public async Task ShrinkingPreservesSimulatedFailureAndReducesMetric()
    {
        var fixture = TestFixture.Create();
        GeneratedFuzzCase original = fixture.Generator.Generate(700, 2, fixture.Profiles.Get("combinations"), 80);
        ShrinkResult result = await new FuzzShrinker().ShrinkAsync(original, (candidate, _) => ValueTask.FromResult(candidate.Source.Contains('0', StringComparison.Ordinal)), 50, TimeSpan.FromSeconds(2), CancellationToken.None);
        result.Attempts.ShouldBeLessThanOrEqualTo(50);
        if (result.Accepted > 0) StableSizeMetric.Measure(result.Case).ShouldBeLessThan(StableSizeMetric.Measure(original));
    }

    [Test]
    public void EveryCandidateHasStrictlySmallerStableMetric()
    {
        var fixture = TestFixture.Create();
        GeneratedFuzzCase original = fixture.Generator.Generate(91, 5, fixture.Profiles.Get("combinations"), 80);
        foreach (GeneratedFuzzCase candidate in new FuzzShrinker().Candidates(original)) StableSizeMetric.Measure(candidate).ShouldBeLessThan(StableSizeMetric.Measure(original));
    }
}
