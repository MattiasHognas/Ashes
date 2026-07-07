using System.Text;
using Ashes.Registry.Publish;
using Shouldly;

namespace Ashes.Registry.Tests;

/// <summary><see cref="CompilerCapabilityExtractor"/> over uploaded source, standalone and multi-module.</summary>
public sealed class CapabilityExtractionTests
{
    [Test]
    public void Single_module_package_reports_its_capability()
    {
        var files = new List<SourceFile>
        {
            new("src/Logger.ash", U("""
                capability Log =
                    | write : Str -> Unit

                let emit : Str -> Unit needs {Log} =
                    given (m) -> Log.write(m)
                """)),
        };

        new CompilerCapabilityExtractor().PublicCapabilities(files, "Logger").ShouldContain("Log");
    }

    [Test]
    public void Multi_module_package_reports_capabilities_across_modules()
    {
        var files = new List<SourceFile>
        {
            new("ashes.json", U("""{ "name": "gfx", "entry": "src/Main.ash", "sourceRoots": ["src"] }""")),
            new("src/Draw.ash", U("""
                capability Pen =
                    | ink : Int -> Unit

                let stroke : Int -> Unit needs {Pen} =
                    given (n) -> Pen.ink(n)
                """)),
            new("src/Main.ash", U("import Draw\nlet useDraw = given (n) -> Draw.stroke(n)\n0\n")),
        };

        new CompilerCapabilityExtractor().PublicCapabilities(files, "Gfx").ShouldContain("Pen");
    }

    [Test]
    public void Unparseable_source_yields_no_capabilities_rather_than_throwing()
    {
        var files = new List<SourceFile> { new("src/Bad.ash", U("this is not @#$ ashes")) };

        new CompilerCapabilityExtractor().PublicCapabilities(files, "Bad").ShouldBeEmpty();
    }

    private static byte[] U(string source) => Encoding.UTF8.GetBytes(source);
}
