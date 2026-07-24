using Ashes.Frontend;
using Ashes.Semantics;
using Shouldly;

namespace Ashes.Tests;

// Regression coverage for the nested-TCO-pattern-alias fix-up: a value extracted through a SECOND
// pattern level below a TCO loop parameter (a tuple field of a list element, not the list element
// itself) needs a protective dup wherever it escapes into the function's result, since only the
// list-cons level was previously tracked. See tests/runtime_rc_tco_nested_tuple_pattern_alias.ash
// for the end-to-end (compiled and run) counterpart.
public sealed class NestedTcoPatternAliasTests
{
    [Test]
    public void Tuple_field_nested_below_a_list_cons_gets_a_protective_dup()
    {
        IrProgram program = LowerProgram("""
            type Entry =
                | text: Str
                | n: Int

            let table = [("=", 1), ("+", 2)]

            let recursive lookup ch tbl =
                match tbl with
                    | [] -> None
                    | pair :: rest ->
                        match pair with
                            | (lit, n) ->
                                if lit == ch
                                then Some(Entry(text = lit, n = n))
                                else lookup(ch)(rest)

            let e1 =
                match lookup("=")(table) with
                    | Some(e) -> e
                    | None -> Entry(text = "?", n = 0)

            Ashes.IO.print(e1.text)
            """);

        IrFunction lookup = program.Functions.Single(function => function.Instructions
            .Any(instruction => instruction is IrInst.CmpStrEq));

        // The guarded protective dup this fix-up inserts: a null check followed by an RcDup into the
        // same slot, landing on a "rc_tco_nested_alias_duplicated" label. Structurally distinct from
        // the pre-existing pair/rest (list-cons-level) protection, which never emits this label.
        lookup.Instructions
            .OfType<IrInst.Label>()
            .ShouldContain(label => label.Name.StartsWith("rc_tco_nested_alias_duplicated", StringComparison.Ordinal));
        lookup.Instructions.OfType<IrInst.RcDup>().ShouldContain(dup => dup.RuntimeManaged);
    }

    [Test]
    public void Copy_typed_tuple_field_is_not_protected_as_runtime_managed()
    {
        // "n" (Int) sits in the same tuple as "lit" (Str) but is a copy type: it must never be
        // treated as a candidate for the protective dup, whose RcDup assumes a refcounted heap
        // pointer -- applying it to a raw integer is a crash, not just a missed optimization.
        IrProgram program = LowerProgram("""
            type Entry =
                | text: Str
                | n: Int

            let table = [("=", 1), ("+", 2)]

            let recursive lookup ch tbl =
                match tbl with
                    | [] -> None
                    | pair :: rest ->
                        match pair with
                            | (lit, n) ->
                                if lit == ch
                                then Some(Entry(text = lit, n = n))
                                else lookup(ch)(rest)

            let e1 =
                match lookup("=")(table) with
                    | Some(e) -> e
                    | None -> Entry(text = "?", n = 0)

            let e2 =
                match lookup("+")(table) with
                    | Some(e) -> e
                    | None -> Entry(text = "?", n = 0)

            Ashes.IO.print(e1.text + " " + e2.text)
            """);

        IrFunction lookup = program.Functions.Single(function => function.Instructions
            .Any(instruction => instruction is IrInst.CmpStrEq));

        // Exactly one protected alias in this function shape (the string "lit"); a second one would
        // mean "n" was wrongly swept in too.
        lookup.Instructions
            .OfType<IrInst.Label>()
            .Count(label => label.Name.StartsWith("rc_tco_nested_alias_duplicated", StringComparison.Ordinal))
            .ShouldBe(1);
    }

    private static IrProgram LowerProgram(string source)
    {
        Diagnostics diagnostics = new();
        Program program = new Parser(source, diagnostics).ParseProgram();
        diagnostics.ThrowIfAny();
        IrProgram ir = new Lowering(diagnostics).Lower(program);
        diagnostics.ThrowIfAny();
        return ir;
    }
}
