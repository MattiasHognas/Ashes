using Ashes.Formatter;
using Shouldly;

namespace Ashes.Tests;

public sealed class EditorConfigFormattingOptionsResolverEdgeCaseTests
{
    [Test]
    public void ResolveForPath_should_return_defaults_for_null_path()
    {
        var options = EditorConfigFormattingOptionsResolver.ResolveForPath(null);

        options.IndentSize.ShouldBe(4);
        options.UseTabs.ShouldBeFalse();
        options.NewLine.ShouldBe("\n");
    }

    [Test]
    public void ResolveForPath_should_return_defaults_for_empty_path()
    {
        var options = EditorConfigFormattingOptionsResolver.ResolveForPath("");

        options.IndentSize.ShouldBe(4);
        options.UseTabs.ShouldBeFalse();
    }

    [Test]
    public void ResolveForPath_should_return_defaults_when_no_editorconfig_exists()
    {
        var root = CreateTempDirectory();
        try
        {
            var filePath = Path.Combine(root, "Main.ash");
            var options = EditorConfigFormattingOptionsResolver.ResolveForPath(filePath);

            options.IndentSize.ShouldBe(4);
            options.UseTabs.ShouldBeFalse();
            options.NewLine.ShouldBe("\n");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void ResolveForPath_should_match_wildcard_pattern()
    {
        var root = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, ".editorconfig"), """
                                                            root = true

                                                            [*]
                                                            indent_size = 2
                                                            """);

            var filePath = Path.Combine(root, "Main.ash");
            var options = EditorConfigFormattingOptionsResolver.ResolveForPath(filePath);

            options.IndentSize.ShouldBe(2);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void ResolveForPath_should_not_match_non_matching_section()
    {
        var root = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, ".editorconfig"), """
                                                            root = true

                                                            [*.js]
                                                            indent_size = 2
                                                            """);

            var filePath = Path.Combine(root, "Main.ash");
            var options = EditorConfigFormattingOptionsResolver.ResolveForPath(filePath);

            options.IndentSize.ShouldBe(4);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void ResolveForPath_should_handle_comments_in_editorconfig()
    {
        var root = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, ".editorconfig"), """
                                                            # This is a comment
                                                            root = true

                                                            ; Another comment
                                                            [*.ash]
                                                            indent_size = 3
                                                            """);

            var filePath = Path.Combine(root, "Main.ash");
            var options = EditorConfigFormattingOptionsResolver.ResolveForPath(filePath);

            options.IndentSize.ShouldBe(3);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void ResolveForPath_should_merge_parent_and_child_editorconfigs_before_root()
    {
        var root = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, ".editorconfig"), """
                                                            root = true

                                                            [*.ash]
                                                            indent_size = 8
                                                            indent_style = tab
                                                            """);

            var childDir = Path.Combine(root, "src");
            Directory.CreateDirectory(childDir);

            File.WriteAllText(Path.Combine(childDir, ".editorconfig"), """
                                                            [*.ash]
                                                            indent_size = 2
                                                            """);

            var filePath = Path.Combine(childDir, "Main.ash");
            var options = EditorConfigFormattingOptionsResolver.ResolveForPath(filePath);

            // Child overrides parent indent_size
            options.IndentSize.ShouldBe(2);
            // Parent's indent_style still inherited
            options.UseTabs.ShouldBeTrue();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void ResolveForPath_should_handle_editorconfig_with_unknown_keys()
    {
        var root = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, ".editorconfig"), """
                                                            root = true

                                                            [*.ash]
                                                            unknown_key = value
                                                            indent_size = 2
                                                            another_unknown = yes
                                                            """);

            var filePath = Path.Combine(root, "Main.ash");
            var options = EditorConfigFormattingOptionsResolver.ResolveForPath(filePath);

            options.IndentSize.ShouldBe(2);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void FormattingOptions_default_constructor_should_have_expected_defaults()
    {
        var options = new FormattingOptions();

        options.IndentSize.ShouldBe(4);
        options.UseTabs.ShouldBeFalse();
        options.NewLine.ShouldBe("\n");
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "ashes_editorconfig_edge_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
