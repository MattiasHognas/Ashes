using System.Text.Json;
using Ashes.Fuzzing.Configuration;
using Ashes.Fuzzing.Generation;
using Ashes.Fuzzing.Oracles;
using Ashes.Fuzzing.Persistence;
using Ashes.Fuzzing.Shrinking;
using Shouldly;

namespace Ashes.Fuzzing.Tests;

public sealed class ArtifactTests
{
    [Test]
    public async Task ArtifactContainsSourcesMetadataOutputAndReplayCommand()
    {
        var fixture = TestFixture.Create();
        GeneratedFuzzCase generated = fixture.Generator.Generate(11, 4, fixture.Profiles.Get("syntax"), 40);
        ShrinkResult shrink = new(generated, 1, 0, TimeSpan.Zero);
        string root = Path.Combine(Path.GetTempPath(), "ashes-fuzz-artifact-tests", Guid.NewGuid().ToString("N"));
        FuzzConfiguration configuration = FuzzConfiguration.Parse(
            ["run", "--profile", "syntax", "--max-nodes", "40", "--target", "linux-x64", "--compiler-timeout", "17", "--program-timeout", "3"]);
        FuzzFailure failure = new(generated, generated, FuzzOracleResult.Failed("simulated", "failure", "out", "err"), shrink, configuration);
        string path = await new FuzzArtifactWriter().WriteAsync(failure, root, CancellationToken.None);
        File.Exists(Path.Combine(path, "original.ash")).ShouldBeTrue();
        File.Exists(Path.Combine(path, "minimized.ash")).ShouldBeTrue();
        using JsonDocument metadata = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(path, "metadata.json")));
        string replay = metadata.RootElement.GetProperty("ReplayCommand").GetString().ShouldNotBeNull();
        replay.ShouldContain("--seed 11 --case 4");
        replay.ShouldContain("--max-nodes 40");
        replay.ShouldContain("--target linux-x64");
        replay.ShouldContain("--compiler-timeout 17 --program-timeout 3");
        metadata.RootElement.GetProperty("ShrinkDurationMilliseconds").GetDouble().ShouldBe(0);
        metadata.RootElement.GetProperty("OutputMaximumBytes").GetInt32().ShouldBe(1024 * 1024);
        metadata.RootElement.GetProperty("ArtifactMaximumBytes").GetInt32().ShouldBe(FuzzArtifactWriter.DefaultMaximumArtifactBytes);
        (await File.ReadAllTextAsync(Path.Combine(path, "stdout.txt"))).ShouldBe("out");
        (await File.ReadAllTextAsync(Path.Combine(path, "stderr.txt"))).ShouldBe("err");
    }

    [Test]
    public async Task ArtifactOutputNeverExceedsConfiguredSizeLimit()
    {
        var fixture = TestFixture.Create();
        GeneratedFuzzCase generated = fixture.Generator.Generate(12, 2, fixture.Profiles.Get("syntax"), 40);
        ShrinkResult shrink = new(generated, 1, 0, TimeSpan.FromMilliseconds(4));
        string root = Path.Combine(Path.GetTempPath(), "ashes-fuzz-artifact-limit-tests", Guid.NewGuid().ToString("N"));
        FuzzConfiguration configuration = FuzzConfiguration.Parse(["run", "--profile", "syntax"]);
        string output = new('x', 16_000);
        FuzzFailure failure = new(
            generated,
            generated,
            FuzzOracleResult.Failed("simulated", "failure", output, output),
            shrink,
            configuration);

        const int maximumBytes = 4096;
        string path = await new FuzzArtifactWriter().WriteAsync(
            failure,
            root,
            CancellationToken.None,
            maximumBytes);

        long artifactBytes = Directory.EnumerateFiles(path).Sum(file => new FileInfo(file).Length);
        artifactBytes.ShouldBeLessThanOrEqualTo(maximumBytes);
        using JsonDocument metadata = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(path, "metadata.json")));
        metadata.RootElement.GetProperty("ArtifactMaximumBytes").GetInt32().ShouldBe(maximumBytes);
    }

    [Test]
    public void CorpusLoadingIsOrdinalAndDeterministic()
    {
        string root = Path.Combine(Path.GetTempPath(), "ashes-fuzz-corpus-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "tests", "fuzz", "corpus"));
        File.WriteAllText(Path.Combine(root, "tests", "fuzz", "corpus", "b.ash"), "2");
        File.WriteAllText(Path.Combine(root, "tests", "fuzz", "corpus", "a.ash"), "1");
        IReadOnlyList<string> files = new FuzzCorpus().Load(root);
        Path.GetFileName(files[0]).ShouldBe("a.ash");
    }
}
