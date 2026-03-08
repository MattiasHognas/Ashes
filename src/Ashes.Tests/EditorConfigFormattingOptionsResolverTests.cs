using Ashes.Formatter;
using Shouldly;

namespace Ashes.Tests;

public sealed class EditorConfigFormattingOptionsResolverTests
{
    [Test]
    public void ResolveForPath_should_use_indent_size_from_editorconfig()
    {
        var root = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, ".editorconfig"), """
                                                           root = true

                                                           [*.ash]
                                                           indent_style = space
                                                           indent_size = 2
                                                           end_of_line = lf
                                                           """);

            var filePath = Path.Combine(root, "Main.ash");
            var options = EditorConfigFormattingOptionsResolver.ResolveForPath(filePath);

            options.UseTabs.ShouldBeFalse();
            options.IndentSize.ShouldBe(2);
            options.NewLine.ShouldBe("\n");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void ResolveForPath_should_use_tabs_when_indent_style_is_tab()
    {
        var root = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, ".editorconfig"), """
                                                           root = true

                                                           [*.ash]
                                                           indent_style = tab
                                                           tab_width = 4
                                                           """);

            var filePath = Path.Combine(root, "Main.ash");
            var options = EditorConfigFormattingOptionsResolver.ResolveForPath(filePath);

            options.UseTabs.ShouldBeTrue();
            options.IndentSize.ShouldBe(4);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void ResolveForPath_should_use_crlf_when_end_of_line_is_crlf()
    {
        var root = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, ".editorconfig"), """
                                                           root = true

                                                           [*.ash]
                                                           end_of_line = crlf
                                                           """);

            var filePath = Path.Combine(root, "Main.ash");
            var options = EditorConfigFormattingOptionsResolver.ResolveForPath(filePath);

            options.NewLine.ShouldBe("\r\n");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void ResolveForPath_should_use_tab_width_when_indent_size_is_tab()
    {
        var root = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, ".editorconfig"), """
                                                           root = true

                                                           [*.ash]
                                                           indent_style = tab
                                                           indent_size = tab
                                                           tab_width = 8
                                                           """);

            var filePath = Path.Combine(root, "Main.ash");
            var options = EditorConfigFormattingOptionsResolver.ResolveForPath(filePath);

            options.UseTabs.ShouldBeTrue();
            options.IndentSize.ShouldBe(8);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void ResolveForPath_should_fallback_to_tab_width_when_indent_size_is_invalid()
    {
        var root = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, ".editorconfig"), """
                                                           root = true

                                                           [*.ash]
                                                           indent_size = foo
                                                           tab_width = 8
                                                           """);

            var filePath = Path.Combine(root, "Main.ash");
            var options = EditorConfigFormattingOptionsResolver.ResolveForPath(filePath);

            options.IndentSize.ShouldBe(8);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void ResolveForPath_should_stop_at_root_editorconfig_when_resolving()
    {
        var root = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, ".editorconfig"), """
                                                           [*.ash]
                                                           indent_style = space
                                                           indent_size = 2
                                                           """);

            var childDir = Path.Combine(root, "child");
            Directory.CreateDirectory(childDir);

            File.WriteAllText(Path.Combine(childDir, ".editorconfig"), """
                                                           root = true

                                                           [*.ash]
                                                           indent_style = tab
                                                           tab_width = 4
                                                           """);

            var grandChildDir = Path.Combine(childDir, "grandchild");
            Directory.CreateDirectory(grandChildDir);

            File.WriteAllText(Path.Combine(grandChildDir, ".editorconfig"), """
                                                           [*.ash]
                                                           end_of_line = crlf
                                                           """);

            var filePath = Path.Combine(grandChildDir, "Main.ash");
            var options = EditorConfigFormattingOptionsResolver.ResolveForPath(filePath);

            options.UseTabs.ShouldBeTrue();
            options.IndentSize.ShouldBe(4);
            options.NewLine.ShouldBe("\r\n");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void ResolveForPath_should_match_case_sensitively_on_non_windows()
    {
        var root = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, ".editorconfig"), """
                                                           root = true

                                                           [main.ash]
                                                           indent_size = 2
                                                           """);

            var filePath = Path.Combine(root, "Main.ash");
            var options = EditorConfigFormattingOptionsResolver.ResolveForPath(filePath);

            if (OperatingSystem.IsWindows())
            {
                options.IndentSize.ShouldBe(2);
            }
            else
            {
                options.IndentSize.ShouldBe(4);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "ashes_editorconfig_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
