using Shouldly;

namespace Ashes.Lsp.Tests;

public sealed class LspHoverCoverageTests
{
    [Test]
    public void Hover_on_a_local_binding_named_like_a_trait_method_shows_the_binding()
    {
        const string source = "let add a b = a + b\n\nAshes.IO.print(add(1)(2))\n";

        string definition = HoverAt(source, "add a b");
        string use = HoverAt(source, "add(1)");

        definition.ShouldStartWith("```ashes\nadd : ");
        definition.ShouldContain("*function*");
        use.ShouldStartWith("```ashes\nadd : ");
        use.ShouldNotContain("trait method");
    }

    [Test]
    public void Hover_after_an_inline_module_lands_on_the_hovered_binding()
    {
        const string source = """
            module Geometry =
                let square = given (x) -> x * x

            let area = Geometry.square(3)

            Ashes.IO.print(area)
            """;

        HoverAt(source, "area = ").ShouldStartWith("```ashes\narea : Int");
        HoverAt(source, "square(3)").ShouldContain("Geometry.square : Int -> Int");
        HoverAt(source, "area)").ShouldStartWith("```ashes\narea : Int");
    }

    [Test]
    public void Hover_inside_an_inline_module_never_shows_another_bindings_type()
    {
        const string source = """
            module Geometry =
                let square = given (x) -> x * x

            let area = Geometry.square(3)

            Ashes.IO.print(area)
            """;

        DocumentService.HoverItem? hover = DocumentService.GetHover(source, source.IndexOf("x * x", StringComparison.Ordinal));

        if (hover is { } item)
        {
            item.Contents.ShouldNotContain("area");
        }
    }

    [Test]
    public void Standalone_import_of_an_inline_module_resolves()
    {
        const string source = """
            import Geometry

            module Geometry =
                let square = given (x) -> x * x

            Ashes.IO.print(square(5))
            """;

        DocumentService.Analyze(source).ShouldBeEmpty();
        HoverAt(source, "square(5)").ShouldContain("square : Int -> Int");
    }

    [Test]
    public void Hover_never_shows_compiler_generated_bindings()
    {
        const string source = """
            type Shape =
                | Circle(Int)
                | Square(Int)
                deriving {Eq, Show}

            trait Area(a) =
                | area : a -> Int

            implement Area(Shape) =
                | area =
                    given (s) ->
                        match s with
                            | Circle(r) -> r * r
                            | Square(w) -> w * w

            let recursive total : List(a) -> Int requires {Area(a)} =
                given (shapes) ->
                    match shapes with
                        | [] -> 0
                        | s :: rest -> Area.area(s) + total(rest)

            Ashes.IO.print(total([Circle(1), Square(2)]))
            """;

        for (int position = 0; position < source.Length; position++)
        {
            DocumentService.HoverItem? hover = DocumentService.GetHover(source, position);
            if (hover is { } item)
            {
                item.Contents.ShouldNotContain("__", customMessage: $"position {position}");
            }
        }

        HoverAt(source, "s with").ShouldStartWith("```ashes\ns : Shape");
        HoverAt(source, "Shape) =").ShouldContain("*algebraic data type*");
    }

    [Test]
    public void Hover_on_type_declarations_constructors_and_fields_shows_the_declaration()
    {
        const string source = """
            type Color =
                | Red
                | Green(Int)

            type Point =
                | x: Int
                | y: Int

            let origin = Point(x = 0, y = 0)
            let shade = Green(3)

            Ashes.IO.print(origin.x)
            """;

        HoverAt(source, "Color =").ShouldContain("*algebraic data type*");
        string constructor = HoverAt(source, "Green(3)");
        constructor.ShouldStartWith("```ashes\nGreen(Int)\n```");
        constructor.ShouldContain("*constructor of `Color`*");
        HoverAt(source, "Point =").ShouldContain("*record type*");
        string field = HoverAt(source, "x: Int");
        field.ShouldStartWith("```ashes\nx: Int\n```");
        field.ShouldContain("*field of record `Point`*");
        HoverAt(source, "Int)").ShouldContain("*built-in type*");
    }

    [Test]
    public void Hover_on_capabilities_and_their_operations_shows_the_declaration()
    {
        const string source = """
            capability Clock =
                | now : Unit -> Int

            let x =
                handle Clock.now(Unit) + 1 with
                    | Clock.now(_) -> resume(42)
                    | return(r) -> r

            Ashes.IO.print(x)
            """;

        HoverAt(source, "Clock =").ShouldContain("*capability*");
        HoverAt(source, "now : ").ShouldStartWith("```ashes\nClock.now : Unit -> Int\n```");
        int armOperation = source.IndexOf("Clock.now(_)", StringComparison.Ordinal) + "Clock.".Length;
        DocumentService.GetHover(source, armOperation).ShouldNotBeNull().Contents
            .ShouldContain("*capability operation of `Clock`*");
    }

    [Test]
    public void Hover_on_a_plain_external_declaration_shows_it()
    {
        const string source = """
            external strlen(Str) -> Int
            external type Handle
            external handleOf(Str) -> Handle = "strlen"

            let length = strlen("Ashes")

            Ashes.IO.print(length)
            """;

        HoverAt(source, "strlen(Str)").ShouldContain("*external function*");
        HoverAt(source, "Handle\n").ShouldContain("*external opaque type*");
        HoverAt(source, "strlen(\"Ashes\")").ShouldContain("strlen : Str -> Int");
    }

    private static string HoverAt(string source, string marker)
    {
        int position = source.IndexOf(marker, StringComparison.Ordinal);
        position.ShouldBeGreaterThanOrEqualTo(0, $"marker '{marker}' not found");
        return DocumentService.GetHover(source, position).ShouldNotBeNull($"no hover at '{marker}'").Contents;
    }
}
