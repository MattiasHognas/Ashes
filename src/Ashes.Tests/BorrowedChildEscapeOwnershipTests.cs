using Ashes.Frontend;
using Ashes.Semantics;
using Shouldly;

namespace Ashes.Tests;

public sealed class BorrowedChildEscapeOwnershipTests
{
    [Test]
    public void A_field_read_off_a_pattern_binding_is_retained_when_a_list_cell_keeps_it()
    {
        const string source =
            """
            type Reference =
                | referenceModule: Str
                | referenceMember: Str

            let recursive makeReferences (n: Int) (acc: List(Reference)) =
                if n == 0
                then acc
                else makeReferences(n - 1)(Reference(referenceModule = "Ashes.M" + Ashes.Text.fromInt(n), referenceMember = "m" + Ashes.Text.fromInt(n)) :: acc)

            let recursive sourcesNeeded (wanted: Str) (references: List(Reference)) =
                match references with
                    | [] -> []
                    | reference :: rest ->
                        if reference.referenceMember == wanted
                        then sourcesNeeded(wanted)(rest)
                        else reference.referenceModule :: sourcesNeeded(wanted)(rest)

            match sourcesNeeded("m2")(makeReferences(4)([])) with
                | [] -> Ashes.IO.print("none")
                | first :: _ -> Ashes.IO.print(first)
            """;
        IrProgram ir = LowerProgram(source);

        // The cell outlives `reference`, whose release takes the field with it: the stored field
        // read carries a reference of its own.
        IrFunction[] bodies = FunctionsOf(ir, "sourcesNeeded");
        bodies.Any(function =>
        {
            HashSet<int> fieldReads = function.Instructions
                .OfType<IrInst.GetAdtField>()
                .Select(read => read.Target)
                .ToHashSet();
            return function.Instructions
                .OfType<IrInst.RcDup>()
                .Any(retain => retain.RuntimeManaged && fieldReads.Contains(retain.SourceTemp));
        }).ShouldBeTrue();
    }

    [Test]
    public void A_view_inside_a_result_the_scope_cannot_copy_keeps_its_backing_alive()
    {
        const string source =
            """
            type Json(S) =
                | JsonStr(S)
                | JsonObject(S, Json, Json)
                | JsonObjectEnd

            let recursive skipSpaces text =
                match Ashes.Text.unconsText(text) with
                    | None -> ""
                    | Some((head, tail)) ->
                        if head == " "
                        then skipSpaces(tail)
                        else text

            let parseValue text =
                let trimmed = skipSpaces(text)
                in
                    match Ashes.Text.unconsText(trimmed) with
                        | None -> Error("unexpected end of input")
                        | Some((_head, rest)) -> Ok((JsonStr(rest), rest))

            match parseValue("  \"abc\"") with
                | Ok(_json) -> Ashes.IO.print("ok")
                | Error(error) -> Ashes.IO.print(error)
            """;
        IrProgram ir = LowerProgram(source);

        // `rest` views `trimmed`, and a result of a self-recursive type is not deep-copied at the
        // scope exit, so releasing `trimmed` there would leave the returned view dangling.
        FunctionsOf(ir, "parseValue")
            .SelectMany(function => function.Instructions.OfType<IrInst.RcDrop>())
            .Where(release => release.RuntimeManaged && string.Equals(release.TypeName, "String", StringComparison.Ordinal))
            .ShouldBeEmpty();
    }

    private static IrFunction[] FunctionsOf(IrProgram ir, string sourceName)
    {
        IrFunction[] functions = ir.Functions
            .Where(function => string.Equals(function.Origin?.Source?.SourceName, sourceName, StringComparison.Ordinal))
            .ToArray();
        functions.ShouldNotBeEmpty();
        return functions;
    }

    private static IrProgram LowerProgram(string source)
    {
        Diagnostics diagnostics = new();
        Ashes.Frontend.Program program = new Parser(source, diagnostics).ParseProgram();
        Lowering lowering = new(diagnostics);
        IrProgram ir = lowering.Lower(program);
        diagnostics.Errors.ShouldBeEmpty(source);
        return ir;
    }
}
