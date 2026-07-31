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
        // same stable owner slot, landing on the ordinary pattern-owner duplication label.
        lookup.Instructions
            .OfType<IrInst.Label>()
            .ShouldContain(label => label.Name.StartsWith("rc_pattern_owner_duplicated", StringComparison.Ordinal));
        lookup.Instructions.OfType<IrInst.RcDup>().ShouldContain(dup => dup.RuntimeManaged);
        lookup.Instructions.OfType<IrInst.RcDrop>().ShouldContain(drop =>
            drop.RuntimeManaged && drop.OwnerSlot >= 0,
            "The binding owner must retain an ordinary Perceus final-drop anchor.");
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

        // Exactly one lexical owner in this shape (the escaping string "lit"). The tail transfer is
        // duplicated at the back edge, while the copy-typed "n" must never enter RC.
        lookup.Instructions
            .OfType<IrInst.Label>()
            .Count(label => label.Name.StartsWith("rc_pattern_owner_duplicated", StringComparison.Ordinal))
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
            .ShouldContain(label => label.Name.StartsWith("rc_pattern_owner_duplicated", StringComparison.Ordinal));
        findFirst.Instructions.OfType<IrInst.RcDup>().ShouldContain(dup => dup.RuntimeManaged);
    }

    // The adversarial counterpart -- proving a direct binding used ONLY as a further match scrutinee
    // ("pair") or ONLY as the unchanged argument at its own parameter's slot ("rest") is not
    // double-protected by this fix -- is Copy_typed_tuple_field_is_not_protected_as_runtime_managed
    // above: it asserts an EXACT protected-alias count of 1 (just "lit") for a shape where "pair" and
    // "rest" are exactly those two safe cases, and it is required to keep passing unmodified. A prior,
    // broader attempt at this same fix (replacing the exclusion instead of narrowing it with a positive
    // escape check) regressed exactly this test by also protecting "pair" and "rest"; this fix's own
    // stable PatternBindingOwnership facts keep it passing.

    [Test]
    public void Direct_binding_passed_only_to_a_plain_helper_call_is_not_protected_as_escaping()
    {
        // "s" is bound directly off tbl -- the same depth as "described" is derived from it -- and its
        // only appearance in this arm is as a plain (non-self, non-constructor) helper call's argument;
        // the value that actually gets embedded in the returned Box is "described" (the helper's own
        // result), never "s" itself. Splicing a protective dup for "s" here is not merely wasted: an
        // ordinary function's own recursive structural drop of its ADT parent uses a uniqueness check to
        // decide whether to recurse into a child list's own cons cells or stop after a single decrement,
        // and the spurious extra reference this dup would add makes that check see a shared owner where
        // there is really only one, permanently abandoning the rest of the list every time this shape
        // recurs -- an unbounded leak, not a redundant-but-harmless dup. This is the real shape
        // (`rotateFirst`/`getAt`/`setAt` consuming a TCO-parameter-derived binding as a plain call
        // argument) that caused a factorial-scaling regression in the fannkuch-redux challenge program.
        IrProgram program = LowerProgram("""
            type Box =
                | v: Str

            let describe x = x

            let table = ["a", "b", "c", "d"]

            let recursive walk n tbl =
                match tbl with
                    | [] -> None
                    | s :: rest ->
                        let described = describe(s)
                        in
                            if n == 0
                            then Some(Box(v = described))
                            else walk(n - 1)(rest)

            let r1 =
                match walk(0)(table) with
                    | Some(b) -> b.v
                    | None -> "?"

            let r2 =
                match walk(2)(table) with
                    | Some(b) -> b.v
                    | None -> "?"

            Ashes.IO.print(r1 + " " + r2)
            """);

        IrFunction walk = program.Functions.Single(function => function.Instructions
            .Any(instruction => instruction is IrInst.CmpIntEq));

        walk.Instructions
            .OfType<IrInst.Label>()
            .ShouldNotContain(label => label.Name.StartsWith("rc_pattern_owner_duplicated", StringComparison.Ordinal));
    }

    [Test]
    public void Early_resolved_direct_alias_is_not_also_protected_by_the_late_fixup()
    {
        IrProgram program = LowerProgram("""
            let recursive reverse : List((Str, Int)) -> List((Str, Int)) -> List((Str, Int)) =
                given values -> given reversed ->
                    match values with
                        | [] -> reversed
                        | head :: tail -> reverse(tail)(head :: reversed)

            match reverse([("x", 1)])([]) with
                | [] -> Ashes.IO.print(0)
                | (_, value) :: _ -> Ashes.IO.print(value)
            """);

        List<IrInst.Label> labels = program.Functions
            .SelectMany(function => function.Instructions)
            .OfType<IrInst.Label>()
            .ToList();

        labels.ShouldNotContain(label =>
            label.Name.StartsWith("rc_tco_alias_duplicated", StringComparison.Ordinal),
                "The source-name TCO alias path has been removed.");
        labels.Count(label =>
            label.Name.StartsWith("rc_pattern_owner_duplicated", StringComparison.Ordinal)).ShouldBe(1,
                "Only the head embedded in the rebuilt list needs a lexical Perceus owner.");
    }

    [Test]
    public void Qualified_inspection_of_a_direct_alias_does_not_transfer_ownership()
    {
        IrProgram program = LowerProgram("""
            let recursive consume : List(Str) -> Int -> Int = given values -> given total ->
                match values with
                    | [] -> total
                    | value :: tail ->
                        consume(tail)(total + Ashes.Text.byteLength(value))

            Ashes.IO.print(consume(["x", "y"])(0))
            """);

        List<IrInst.Label> labels = program.Functions
            .SelectMany(function => function.Instructions)
            .OfType<IrInst.Label>()
            .ToList();

        labels.ShouldNotContain(label =>
            label.Name.StartsWith("rc_tco_alias_duplicated", StringComparison.Ordinal),
                "Same-parameter transfer is handled at the exact back edge, without a binding alias table.");
        labels.ShouldNotContain(label =>
            label.Name.StartsWith("rc_pattern_owner_duplicated", StringComparison.Ordinal));
    }

    [Test]
    public void Same_named_binders_in_different_match_arms_keep_distinct_escape_verdicts()
    {
        IrProgram program = LowerProgram("""
            type Box =
                | v: Str

            let recursive find keep values =
                match values with
                    | [] -> None
                    | value :: tail when keep -> Some(Box(v = value))
                    | value :: tail -> find(keep)(tail)

            let result =
                match find(true)(["a", "b"]) with
                    | Some(box) -> box.v
                    | None -> "?"

            Ashes.IO.print(result)
            """);

        IrFunction find = program.Functions.Single(function => function.Instructions
            .Any(instruction => instruction is IrInst.CmpIntEq));

        find.Instructions
            .OfType<IrInst.Label>()
            .Count(label => label.Name.StartsWith("rc_pattern_owner_duplicated", StringComparison.Ordinal))
            .ShouldBe(1,
                "Only the guarded arm's escaping value receives a lexical owner; the tail transfers at its back edge.");
    }

    [Test]
    public void Nested_same_named_binder_does_not_make_the_outer_binding_escape()
    {
        IrProgram program = LowerProgram("""
            type Box =
                | v: Str

            let recursive find keep values =
                match values with
                    | [] -> None
                    | value :: tail ->
                        let inner = ["inner"]
                        in
                            match inner with
                                | [] -> find(keep)(tail)
                                | value :: _ ->
                                    if keep
                                    then Some(Box(v = value))
                                    else find(keep)(tail)

            let result =
                match find(true)(["outer"]) with
                    | Some(box) -> box.v
                    | None -> "?"

            Ashes.IO.print(result)
            """);

        IrFunction find = program.Functions.Single(function => function.Instructions
            .Any(instruction => instruction is IrInst.CmpIntEq));

        find.Instructions
            .OfType<IrInst.Label>()
            .ShouldNotContain(
                label => label.Name.StartsWith("rc_pattern_owner_duplicated", StringComparison.Ordinal),
                "The escaping inner value must not transfer its verdict to the unused outer value with the same source name.");
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
