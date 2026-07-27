using Ashes.Frontend;
using Ashes.Semantics;
using Shouldly;

namespace Ashes.Tests;

// Validates the TCO promotion-profitability signal (Lowering.GetTcoPromotionProfitability) against
// the four cases the ownership-unification investigation identified: two real regressions found by
// removing the all-or-nothing TCO frame veto (a station-merge-shaped list/tree loop and a
// line-wrap-shaped string/list loop), the synthetic string/closure case that motivated wanting the
// veto gone in the first place, and a single-heap-parameter loop where the veto (and this signal)
// never had anything to do regardless.
public sealed class TcoPromotionCostSignalTests
{
    private static Lowering LowerProgram(string source)
    {
        var diagnostics = new Diagnostics();
        var program = new Parser(source, diagnostics).ParseProgram();
        var lowering = new Lowering(diagnostics);
        lowering.Lower(program);
        diagnostics.Errors.ShouldBeEmpty();
        return lowering;
    }

    [Test]
    public void Consumed_list_tail_alongside_a_self_recursive_multi_constructor_tree_is_not_profitable()
    {
        // Shape of challenges/1brc/brc.ash's mergeEntries: a list walked one cons cell at a time
        // (entries) alongside a self-recursive, multi-constructor accumulator (acc: Tree) that is
        // rebuilt fresh every iteration by a helper call and can never itself be RC-eligible.
        const string source =
            """
            type Tree =
                | Leaf
                | Node(Tree, Int, Tree)

            let recursive insert n acc =
                match acc with
                    | Leaf -> Node(Leaf)(n)(Leaf)
                    | Node(l, v, r) ->
                        if n < v
                        then Node(insert(n)(l))(v)(r)
                        else Node(l)(v)(insert(n)(r))

            let recursive mergeEntries entries acc =
                match entries with
                    | [] -> acc
                    | (key, value) :: tail -> mergeEntries(tail)(insert(value + Ashes.Text.byteLength(key))(acc))

            let recursive buildEntries n acc =
                if n <= 0
                then acc
                else buildEntries(n - 1)((Ashes.Text.fromInt(n), n) :: acc)

            let result = mergeEntries(buildEntries(50)([]))(Leaf)

            let recursive depth acc =
                match acc with
                    | Leaf -> 0
                    | Node(l, _, r) -> 1 + depth(l)

            Ashes.IO.print(Ashes.Text.fromInt(depth(result)))
            """;

        var lowering = LowerProgram(source);
        var verdicts = lowering.GetTcoPromotionProfitability("mergeEntries");

        verdicts.ShouldNotBeNull();
        verdicts.ShouldContainKey("entries");
        verdicts["entries"].Verdict.ShouldBe(TcoPromotionVerdict.NotProfitable);
    }

    [Test]
    public void Affine_string_accumulator_alongside_a_re_passed_unchanged_list_is_not_profitable()
    {
        // Shape of challenges/reverse-complement/reverse-complement.ash's emit: an affine-growth
        // string accumulator (buf, unconditionally RC-eligible by type) alongside a list (chars)
        // that is re-passed UNCHANGED at one of its own two tail-call sites (the column-wrap branch),
        // which disqualifies it from the consumed-tail exemption entirely, leaving it permanently
        // arena-only.
        const string source =
            """
            let recursive emit chars col buf =
                match chars with
                    | [] -> Ashes.Text.byteLength(buf)
                    | c :: rest ->
                        if col == 60
                        then emit(chars)(0)("")
                        else emit(rest)(col + 1)(buf + c)

            let recursive buildChars n acc =
                if n <= 0
                then acc
                else buildChars(n - 1)(Ashes.Text.fromInt(n) :: acc)

            let result = emit(buildChars(200)([]))(0)("")

            Ashes.IO.print(Ashes.Text.fromInt(result))
            """;

        var lowering = LowerProgram(source);
        var verdicts = lowering.GetTcoPromotionProfitability("emit");

        verdicts.ShouldNotBeNull();
        verdicts.ShouldContainKey("buf");
        verdicts["buf"].Verdict.ShouldBe(TcoPromotionVerdict.NotProfitable);
    }

    [Test]
    public void Affine_string_accumulator_alongside_a_sometimes_fresh_closure_is_profitable()
    {
        // The original synthetic adversarial case: a Str accumulator (unconditionally RC-eligible)
        // threaded alongside a closure that is a fresh lambda on some tail self-calls and threaded
        // unchanged on others -- the shape that made the all-or-nothing veto wipe the Str
        // accumulator's own eligibility purely because of the co-resident closure's unrelated
        // failure to be independently RC-eligible. A closure sibling must never count as
        // "permanently blocking" the way a self-recursive ADT or a non-arena-resettable list does:
        // a non-escaping closure is stack-allocated, not arena/RC at all.
        const string source =
            """
            let recursive loop n str fn =
                if n <= 0
                then str + Ashes.Text.fromInt(fn(n))
                else
                    if n % 2 == 0
                    then loop(n - 1)(str + "x")(given y -> y + 1)
                    else loop(n - 1)(str + "y")(fn)

            let result = loop(10)("")(given y -> y)

            Ashes.IO.print(result)
            """;

        var lowering = LowerProgram(source);
        var verdicts = lowering.GetTcoPromotionProfitability("loop");

        verdicts.ShouldNotBeNull();
        verdicts.ShouldContainKey("str");
        verdicts["str"].Verdict.ShouldBe(TcoPromotionVerdict.Profitable);
    }

    [Test]
    public void Single_heap_parameter_loop_is_unaffected_by_the_signal()
    {
        // fannkuch-redux's own loop/nextPerm shape: the sole heap-typed TCO parameter in the frame.
        // With no sibling to interact with, this signal must never mark it NotProfitable -- the
        // veto's own multi-parameter logic (and this signal's) never had anything to do here either
        // way, so this is a sanity check that the new machinery does not touch what it shouldn't.
        const string source =
            """
            type State =
                | S(List(Int), Int)

            let recursive loop n st =
                match st with
                    | S(perm, count) ->
                        if n <= 0
                        then count
                        else loop(n - 1)(S(perm)(count + 1))

            let result = loop(10)(S([1, 2, 3])(0))

            Ashes.IO.print(Ashes.Text.fromInt(result))
            """;

        var lowering = LowerProgram(source);
        var verdicts = lowering.GetTcoPromotionProfitability("loop");

        if (verdicts is not null && verdicts.TryGetValue("st", out var verdict))
        {
            verdict.Verdict.ShouldBe(TcoPromotionVerdict.Profitable);
        }
    }
}
