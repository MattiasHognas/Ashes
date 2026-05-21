using Shouldly;

namespace Ashes.Tests;

public sealed class TestProcessHelperTests
{
    [Test]
    public void FindCommand_should_detect_wine_stable_on_path()
    {
        var tempDir = CreateTempDirectory();
        var candidatePath = Path.Combine(tempDir, "wine-stable");

        try
        {
            File.WriteAllText(candidatePath, string.Empty);

            var resolved = TestProcessHelper.FindCommand(["wine-stable"], tempDir);

            resolved.ShouldBe(candidatePath);
        }
        finally
        {
            DeleteDirectoryIfExists(tempDir);
        }
    }

    [Test]
    public void FindCommand_should_accept_rooted_candidates_without_path_entries()
    {
        var tempDir = CreateTempDirectory();
        var candidatePath = Path.Combine(tempDir, "wine64");

        try
        {
            File.WriteAllText(candidatePath, string.Empty);

            var resolved = TestProcessHelper.FindCommand([candidatePath], null);

            resolved.ShouldBe(candidatePath);
        }
        finally
        {
            DeleteDirectoryIfExists(tempDir);
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "ashes-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectoryIfExists(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        Directory.Delete(path, recursive: true);
    }
}
