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

    [Test]
    public void Direct_list_element_escaping_into_returned_construction_gets_a_protective_dup()
    {
        // "s" is bound directly off tbl -- one pattern level, not a further nested match, the same
        // depth as "rest" -- and escapes independently into Box(v = s), which becomes part of the
        // function's own returned value. This is a different fate than "rest" (which only ever flows
        // back into tbl's own next tail-call argument, already handled by the ordinary back-edge
        // argument machinery) and must still get its own protective dup: tbl's cons cell that "s" came
        // from is dropped once the loop exits without recursing, and without a dup the returned Box
        // would keep a dangling pointer into that just-freed cell.
        IrProgram program = LowerProgram("""
            type Box =
                | v: Str

            let table = ["a", "b", "c", "d"]

            let recursive findFirst target tbl =
                match tbl with
                    | [] -> None
                    | s :: rest ->
                        if s == target
                        then Some(Box(v = s))
                        else findFirst(target)(rest)

            let r1 =
                match findFirst("c")(table) with
                    | Some(b) -> b.v
                    | None -> "?"

            let r2 =
                match findFirst("b")(table) with
                    | Some(b) -> b.v
                    | None -> "?"

            Ashes.IO.print(r1 + " " + r2)
            """);

        IrFunction findFirst = program.Functions.Single(function => function.Instructions
            .Any(instruction => instruction is IrInst.CmpStrEq));

        findFirst.Instructions
            .OfType<IrInst.Label>()
            .ShouldContain(label => label.Name.StartsWith("rc_tco_nested_alias_duplicated", StringComparison.Ordinal));
        findFirst.Instructions.OfType<IrInst.RcDup>().ShouldContain(dup => dup.RuntimeManaged);
    }

    // The adversarial counterpart -- proving a direct binding used ONLY as a further match scrutinee
    // ("pair") or ONLY as the unchanged argument at its own parameter's slot ("rest") is not
    // double-protected by this fix -- is Copy_typed_tuple_field_is_not_protected_as_runtime_managed
    // above: it asserts an EXACT protected-alias count of 1 (just "lit") for a shape where "pair" and
    // "rest" are exactly those two safe cases, and it is required to keep passing unmodified. A prior,
    // broader attempt at this same fix (replacing the exclusion instead of narrowing it with a positive
    // escape check) regressed exactly this test by also protecting "pair" and "rest"; this fix's own
    // narrower EscapingDirectPatternBindings set is what keeps it passing.

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
