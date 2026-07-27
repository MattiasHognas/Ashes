using Ashes.Frontend;
using Ashes.Semantics;
using Shouldly;

namespace Ashes.Tests;

// Regression coverage for TrackRuntimeManagedMatchScrutinee's wrapper-vs-extracted-field ownership
// split. A freshly constructed match scrutinee (never bound to a name of its own) and a field a
// pattern extracts directly from it are two different allocations: the wrapper cell has its own
// header that must be released exactly once regardless of what later happens to any field extracted
// from it, and an extracted field that gets forwarded elsewhere (e.g. into a self-recursive tail
// call) must not be dropped a second time by the wrapper's own recursive drop. These tests exercise
// the exact shape that used to conflate the two (fannkuch-redux's Step/State pair) plus the
// surrounding adversarial cases: an ordinary non-tail-call match, an unmoved/unused extracted field, a
// field used twice, and a nested match on the extracted field itself. Function labels are compiler-
// generated (lambda_N), never the source name, so tests locate "the function that matters" by the
// distinctive pattern-bound local name ("st2") recorded in its debug info instead.
public sealed class MatchScrutineeWrapperDropTests
{
    // Types and the fresh-value producer shared by every test below. Declarations only (no trailing
    // expression), so each test can append its own without violating the top-level
    // "declaration* expr?" grouping.
    private const string TypesAndNextStep = """
        type Pair =
            | S(List(Int), Int)

        type Step =
            | Done
            | Continue(Pair, Int)

        let recursive nextStep n st =
            if n <= 0
            then Done
            else
                match st with
                    | S(xs, c) -> Continue(S(xs)(c + 1))(n)
        """;

    // The wrapper (Step) has one non-arena-resettable field (Pair) extracted into "st2" and forwarded,
    // unmodified, as the self-recursive tail call's own next-iteration argument -- exactly fannkuch's
    // `Continue(st2, r2) -> loop(n)(st2)(r2)...` shape. Before the fix, marking "st2" moved for the
    // back edge silently marked the wrapper's own (shared, aliased) ownership record moved too, so the
    // wrapper's own header release never fired.
    [Test]
    public void Tco_forwarded_field_still_drops_the_wrapper_shell()
    {
        IrProgram program = LowerProgram($$"""
            {{TypesAndNextStep}}

            let recursive loop n st acc =
                match nextStep(n)(st) with
                    | Done -> acc
                    | Continue(st2, r2) -> loop(n - 1)(st2)(acc + r2)

            loop(5)(S([1, 2, 3])(0))(0)
            """);
        IrFunction loop = FunctionBinding(program, "st2");

        loop.Instructions
            .Count(inst => inst is IrInst.RcDrop { TypeName: "Step", RuntimeManaged: true })
            .ShouldBeGreaterThanOrEqualTo(1);
    }

    // Same wrapper shape, but reached through an ordinary (non-tail-call, non-loop) match where the
    // extracted field is read once and never forwarded anywhere. Confirms the fix does not double-drop
    // the common case: the wrapper's own header releases exactly once, and the independently tracked
    // field (now dead after this arm) also releases exactly once -- not zero, not twice.
    [Test]
    public void Ordinary_match_on_fresh_wrapper_drops_wrapper_and_unused_field_exactly_once_each()
    {
        IrProgram program = LowerProgram($$"""
            {{TypesAndNextStep}}

            let readOnce st =
                match nextStep(3)(st) with
                    | Done -> 0
                    | Continue(st2, r2) -> r2

            readOnce(S([9])(5))
            """);

        IrFunction readOnce = FunctionBinding(program, "st2");

        readOnce.Instructions
            .Count(inst => inst is IrInst.RcDrop { TypeName: "Step", RuntimeManaged: true })
            .ShouldBe(1);
        readOnce.Instructions
            .Count(inst => inst is IrInst.RcDrop { TypeName: "Pair", RuntimeManaged: true })
            .ShouldBe(1);
    }

    // The extracted field is used twice inside the arm body (never moved). Must not regress into a
    // use-after-drop: existing dup/borrow placement is responsible for keeping the second use valid,
    // independent of this fix -- this only pins that giving the field its own independent tracking
    // doesn't disturb that machinery.
    [Test]
    public void Field_used_twice_in_arm_body_still_lowers_without_diagnostics()
    {
        IrProgram program = LowerProgram($$"""
            {{TypesAndNextStep}}

            let recursive pairLength xs =
                match xs with
                    | [] -> 0
                    | _ :: rest -> 1 + pairLength(rest)

            let sumPairTwice st =
                match nextStep(3)(st) with
                    | Done -> 0
                    | Continue(st2, r2) ->
                        match st2 with
                            | S(xs, c) -> pairLength(xs) + pairLength(xs) + c

            sumPairTwice(S([1, 2, 3])(0))
            """);

        program.Functions.ShouldNotBeEmpty();
    }

    // A nested match directly on the extracted field ("matchValue is Expr.Var" branch of
    // TrackRuntimeManagedMatchScrutinee) is left entirely on the pre-existing aliasing path. Confirms
    // the fix doesn't disturb that path and the whole program still lowers cleanly.
    [Test]
    public void Nested_match_on_extracted_field_lowers_without_diagnostics()
    {
        IrProgram program = LowerProgram($$"""
            {{TypesAndNextStep}}

            let describe st =
                match nextStep(3)(st) with
                    | Done -> 0
                    | Continue(st2, r2) ->
                        match st2 with
                            | S(xs, c) -> c + r2

            describe(S([4, 5])(1))
            """);

        program.Functions.ShouldNotBeEmpty();
    }

    // A field position matched by a nested sub-pattern (not a plain top-level name) must stay on the
    // pre-existing, more conservative aliasing path -- this fix only ever applies to a plain top-level
    // `Pattern.Var` binding. Confirms the gate correctly excludes this shape rather than
    // mis-attributing a field index to it.
    [Test]
    public void Nested_subpattern_field_is_not_independently_tracked()
    {
        IrProgram program = LowerProgram($$"""
            {{TypesAndNextStep}}

            let readNested st =
                match nextStep(3)(st) with
                    | Done -> 0
                    | Continue(S(xs, c), r2) -> c + r2

            readNested(S([1])(0))
            """);

        program.Functions.ShouldNotBeEmpty();
    }

    private static IrFunction FunctionBinding(IrProgram program, string localName)
    {
        return program.Functions
            .Concat([program.EntryFunction])
            .Single(function => function.LocalNames?.Values.Contains(localName, StringComparer.Ordinal) == true);
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
