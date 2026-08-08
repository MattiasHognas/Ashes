using Ashes.Fuzzing.Configuration;
using Ashes.Fuzzing.Generation;
using Ashes.Fuzzing.Persistence;
using Shouldly;

namespace Ashes.Fuzzing.Tests;

public sealed class ReplayTests
{
    [Test]
    public void ReplayCoordinatesRegenerateByteIdenticalSource()
    {
        var fixture = TestFixture.Create();
        GeneratedFuzzCase original = fixture.Generator.Generate(12345, 417, fixture.Profiles.Get("perceus"), 80);
        GeneratedFuzzCase replay = fixture.Generator.Generate(original.MasterSeed, original.CaseIndex, fixture.Profiles.Get(original.Profile), 80);
        replay.Source.ShouldBe(original.Source);
        replay.Trace.Entries.ShouldBe(original.Trace.Entries);
    }

    [Test]
    public void CliConfigurationParsesReplayAndRejectsInvalidValues()
    {
        FuzzConfiguration configuration = FuzzConfiguration.Parse(["replay", "--profile", "perceus", "--seed", "12345", "--case", "417"]);
        configuration.CaseIndex.ShouldBe(417);
        configuration.Seed.ShouldBe(12345UL);
        Should.Throw<ArgumentException>(() => FuzzConfiguration.Parse(["run", "--cases", "0"]));
    }

    [Test]
    public void ReportedReplayArgumentsPreserveNonDefaultGenerationConfiguration()
    {
        var fixture = TestFixture.Create();
        GeneratedFuzzCase original = fixture.Generator.Generate(12345, 417, fixture.Profiles.Get("perceus"), 37);
        FuzzConfiguration campaign = FuzzConfiguration.Parse(
            ["run", "--profile", "perceus", "--max-nodes", "37", "--target", "linux-arm64", "--compiler-timeout", "41", "--program-timeout", "7", "--max-output-bytes", "8192", "--max-artifact-bytes", "32768"]);

        FuzzConfiguration replayConfiguration = FuzzConfiguration.Parse(FuzzReplayCommand.Arguments(original, campaign));
        GeneratedFuzzCase replay = fixture.Generator.Generate(
            replayConfiguration.Seed,
            replayConfiguration.CaseIndex,
            fixture.Profiles.Get(replayConfiguration.Profile),
            replayConfiguration.MaximumNodes);

        replay.Source.ShouldBe(original.Source);
        replay.Trace.Entries.ShouldBe(original.Trace.Entries);
        replayConfiguration.Target.ShouldBe("linux-arm64");
        replayConfiguration.CompilerTimeout.ShouldBe(TimeSpan.FromSeconds(41));
        replayConfiguration.ProgramTimeout.ShouldBe(TimeSpan.FromSeconds(7));
        replayConfiguration.MaximumOutputBytes.ShouldBe(8192);
        replayConfiguration.MaximumArtifactBytes.ShouldBe(32768);
    }

    [Test]
    public void ProfilesSupplyDefaultsWithoutOverridingExplicitCliValues()
    {
        var fixture = TestFixture.Create();
        FuzzProfile compile = fixture.Profiles.Get("compile");
        FuzzConfiguration defaults = FuzzConfiguration.Parse(["run", "--profile", "compile"])
            .ApplyProfileDefaults(compile);
        defaults.Cases.ShouldBe(10);
        defaults.MaximumNodes.ShouldBe(50);
        defaults.CompilerTimeout.ShouldBe(TimeSpan.FromSeconds(30));
        defaults.Target.ShouldBe("host");

        FuzzConfiguration overridden = FuzzConfiguration.Parse(
                ["run", "--profile", "compile", "--cases", "17", "--max-nodes", "91", "--compiler-timeout", "44", "--target", "linux-arm64"])
            .ApplyProfileDefaults(compile);
        overridden.Cases.ShouldBe(17);
        overridden.MaximumNodes.ShouldBe(91);
        overridden.CompilerTimeout.ShouldBe(TimeSpan.FromSeconds(44));
        overridden.Target.ShouldBe("linux-arm64");
    }
}
