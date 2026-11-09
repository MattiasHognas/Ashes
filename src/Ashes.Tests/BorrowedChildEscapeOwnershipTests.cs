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
    public void A_view_inside_a_result_the_scope_cannot_copy_releases_its_backing_behind_the_result_copy()
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

        // `rest` views `trimmed`, and a result of a self-recursive type cannot be deep-copied into
        // the arena at the scope exit. The result is normalized onto the reference-counted heap
        // instead, the copy materializing the view, so `trimmed` is released only behind that copy:
        // every release of a reference-counted string follows a call of the result's normalizer, and
        // there is one, since keeping `trimmed` alive to the program's end was a leak per call.
        int releases = 0;
        foreach (IrFunction function in FunctionsOf(ir, "parseValue"))
        {
            List<IrInst> instructions = [.. function.Instructions];
            int firstNormalize = instructions.FindIndex(instruction => instruction is IrInst.CallKnown { FuncLabel: var label }
                && label.StartsWith("__rcnorm", StringComparison.Ordinal));
            int firstStringRelease = instructions.FindIndex(instruction => instruction is IrInst.RcDrop { RuntimeManaged: true, TypeName: var typeName }
                && string.Equals(typeName, "String", StringComparison.Ordinal));
            if (firstStringRelease >= 0)
            {
                releases++;
                firstNormalize.ShouldBeGreaterThanOrEqualTo(0);
                firstNormalize.ShouldBeLessThan(firstStringRelease);
            }
        }

        releases.ShouldBeGreaterThan(0);
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
