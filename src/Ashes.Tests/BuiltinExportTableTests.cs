using Ashes.Semantics;
using Shouldly;

namespace Ashes.Tests;

/// <summary>
/// Exercises the queryable per-module export tables that <see cref="BuiltinRegistry"/> surfaces so
/// the import resolver can validate <c>import Ashes.IO.print</c>-style selectors against built-in
/// modules with the same mechanism it uses for user modules.
/// </summary>
public sealed class BuiltinExportTableTests
{
    [Test]
    public void Ashes_io_exports_its_intrinsic_value_members()
    {
        BuiltinRegistry.TryGetModuleExports("Ashes.IO", out var exports).ShouldBeTrue();

        foreach (var name in new[] { "print", "panic", "args", "write", "writeLine", "readLine" })
        {
            exports.ShouldContain(name);
        }
    }

    [Test]
    public void Ashes_io_export_query_matches_the_registered_members()
    {
        BuiltinRegistry.TryGetModule("Ashes.IO", out var module).ShouldBeTrue();
        BuiltinRegistry.TryGetModuleExports("Ashes.IO", out var exports).ShouldBeTrue();

        foreach (var member in module.Members.Keys)
        {
            exports.ShouldContain(member);
        }
    }

    [Test]
    public void Ashes_io_does_not_export_an_unknown_name()
    {
        BuiltinRegistry.TryGetModuleExports("Ashes.IO", out var exports).ShouldBeTrue();

        exports.Contains("definitelyNotAnIoBinding").ShouldBeFalse();
        BuiltinRegistry.ModuleExportsName("Ashes.IO", "definitelyNotAnIoBinding").ShouldBeFalse();
        BuiltinRegistry.ModuleExportsName("Ashes.IO", "print").ShouldBeTrue();
    }

    [Test]
    public void Resource_backed_modules_expose_their_top_level_bindings()
    {
        BuiltinRegistry.TryGetModuleExports("Ashes.List", out var listExports).ShouldBeTrue();
        foreach (var name in new[] { "foldLeft", "map", "filter", "reverse" })
        {
            listExports.ShouldContain(name);
        }

        BuiltinRegistry.TryGetModuleExports("Ashes.String", out var stringExports).ShouldBeTrue();
        foreach (var name in new[] { "length", "contains", "split" })
        {
            stringExports.ShouldContain(name);
        }

        BuiltinRegistry.TryGetModuleExports("Ashes.Result", out var resultExports).ShouldBeTrue();
        foreach (var name in new[] { "map", "flatMap", "getOrElse" })
        {
            resultExports.ShouldContain(name);
        }

        BuiltinRegistry.TryGetModuleExports("Ashes.Maybe", out var maybeExports).ShouldBeTrue();
        foreach (var name in new[] { "map", "isSome", "isNone" })
        {
            maybeExports.ShouldContain(name);
        }
    }

    [Test]
    public void Resource_backed_modules_expose_their_top_level_types()
    {
        BuiltinRegistry.TryGetModuleExports("Ashes.Array", out var arrayExports).ShouldBeTrue();
        arrayExports.ShouldContain("ArrayTree");

        BuiltinRegistry.TryGetModuleExports("Ashes.Map", out var mapExports).ShouldBeTrue();
        mapExports.ShouldContain("MapTree");
    }

    [Test]
    public void Intrinsic_text_module_exports_its_members()
    {
        BuiltinRegistry.ModuleExportsName("Ashes.Text", "parseInt").ShouldBeTrue();
        BuiltinRegistry.ModuleExportsName("Ashes.Text", "fromInt").ShouldBeTrue();
        BuiltinRegistry.ModuleExportsName("Ashes.Text", "notARealTextBinding").ShouldBeFalse();
    }

    [Test]
    public void Resource_backed_module_does_not_export_an_unknown_name()
    {
        BuiltinRegistry.ModuleExportsName("Ashes.List", "foldLeft").ShouldBeTrue();
        BuiltinRegistry.ModuleExportsName("Ashes.List", "thisBindingDoesNotExist").ShouldBeFalse();
    }

    [Test]
    public void Unknown_module_has_no_export_table()
    {
        BuiltinRegistry.TryGetModuleExports("Ashes.NotAModule", out var exports).ShouldBeFalse();
        exports.ShouldBeEmpty();
        BuiltinRegistry.ModuleExportsName("Ashes.NotAModule", "anything").ShouldBeFalse();
    }

    [Test]
    public void Every_standard_module_has_a_queryable_export_table()
    {
        foreach (var moduleName in BuiltinRegistry.StandardModuleNames)
        {
            BuiltinRegistry.TryGetModuleExports(moduleName, out _).ShouldBeTrue();
        }
    }
}
