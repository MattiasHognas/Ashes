import Ashes.Test as test
import Ashes.Collection.List.length
import AshesCompiler.Frontend.Parser
import AshesCompiler.Frontend.Syntax
import AshesCompiler.Semantics.CoreLowering
import AshesCompiler.Semantics.Ir
import AshesCompiler.Semantics.IrText
import AshesCompiler.Semantics.Types
export (
    value runCoreTraitDispatchLoweringTests,
)

let parsedProgram source =
    match parseProgram(source) with
        | ProgramParseResult { program = program, diagnostics = [] } -> program
        | ProgramParseResult { diagnostics = diagnostics } -> test.fail("program should parse cleanly: " + Ashes.Trait.Show.show(diagnostics))

let loweredDump source =
    match source
    |> parsedProgram
    |> lowerCoreProgram with
        | CoreLoweringResult { program = Some(program), error = None } ->
            None
            |> formatIr(program)(LoweredIr)
            |> Ashes.Text.join("\n")
        | CoreLoweringResult { error = Some(error) } ->
            error
            |> Ashes.Trait.Show.show
            |> (given (text) -> test.fail("program lowering failed: " + text))
        | _ -> test.fail("program lowering produced no program")

let loweringErrorFor source =
    match source
    |> parsedProgram
    |> lowerCoreProgram with
        | CoreLoweringResult { error = Some(error) } -> error
        | CoreLoweringResult { error = None } -> test.fail("expected program lowering to fail, but it produced a program")

let occurrences (needle: Str) (text: Str) =
    length(Ashes.Text.split(text)(needle)) - 1

let shapeSource = "type Shape =\n    | Circle\n    | Square\n    deriving {Eq}\n\n"

// `==` on a type whose `Eq` is derived is not a primitive comparison: each site calls the
// derived `equal` through two closure calls (two sites here, plus the entry calling `same`), and
// the derived body is lowered once as its two curried helpers, the second site rebuilding the
// outer closure from its cached label: with `same` itself, its inner lambda, and the entry, the
// program has five functions rather than seven.
let expectDerivedEqualityDispatchesThroughOneHelper unit =
    match loweredDump(shapeSource + "let same (a: Shape) (b: Shape) = a == b && b == a\n\nsame(Circle)(Square)") with
        | dump ->
            Unit
            |> (given (_) ->
                dump
                |> occurrences("CallClosure")
                |> test.assertEqual(6))
            |> (given (_) ->
                dump
                |> occurrences("\nfunction ")
                |> test.assertEqual(5))

// The operands are unified before the dispatch is chosen, as stage 0's `Unify(leftType,
// rightType)`: a parameter whose type is still a variable takes the annotated side's type and
// the comparison still dispatches through the derived `equal`.
let expectEqualityUnifiesAVariableOperandWithTheOtherSide unit =
    shapeSource + "let same (a: Shape) b = a == b\n\nsame(Circle)(Square)"
    |> loweredDump
    |> occurrences("CallClosure")
    |> test.assertEqual(4)

// `!=` on a derived type has no supplied `notEqual`: the derived `equal` is called and its result
// negated, the default method's `!Eq.equal(left)(right)` without a lambda around it.
let expectNotEqualNegatesTheDerivedEquality unit =
    match loweredDump(shapeSource + "let differ (a: Shape) (b: Shape) = a != b\n\ndiffer(Circle)(Square)") with
        | dump ->
            Unit
            |> (given (_) ->
                dump
                |> occurrences("CallClosure")
                |> test.assertEqual(4))
            |> (given (_) ->
                "LoadConstBool"
                |> Ashes.Text.contains(dump)
                |> test.assertEqual(true))

// A derived record equality compares the fields through `Eq.equal(left)(right)` calls; at a
// primitive field type those lower to the direct comparison of the operator they stand for.
let expectDerivedRecordEqualityComparesFieldsDirectly unit =
    "type Live =\n    | lb: Int\n    | li: Bool\n    deriving {Eq}\n\nlet same (a: Live) (b: Live) = a == b\n\nsame(Live(lb = 1, li = true))(Live(lb = 1, li = false))"
    |> loweredDump
    |> occurrences("CmpIntEq")
    |> (given (count) -> test.assertEqual(true)(count >= 2))

// A written implementation supplies its own methods: `!=` calls the supplied `notEqual` rather
// than negating `equal`, and the closure helper carries the implementation's own body.
let expectSuppliedNotEqualIsCalled unit =
    match loweredDump("type Wrap =\n    | Wrap(Int)\n\nimplement Eq(Wrap) =\n    | equal = given (left) -> given (right) -> true\n    | notEqual = given (left) -> given (right) -> false\n\nlet differ (a: Wrap) (b: Wrap) = a != b\n\ndiffer(Wrap(1))(Wrap(2))") with
        | dump ->
            Unit
            |> (given (_) ->
                dump
                |> occurrences("CallClosure")
                |> test.assertEqual(4))
            |> (given (_) ->
                "CmpIntEq"
                |> Ashes.Text.contains(dump)
                |> test.assertEqual(false))

// A recursive type's derived equality compares the subtrees through the same `equal`: the
// method is remembered under its predicted label before its body is lowered, so the two subtree
// comparisons inside the body rebuild the outer closure from that label (four calls) instead of
// lowering the body again without end. With the site's two calls and the entry's two, eight
// calls over the same five functions as a flat type.
let expectRecursiveDerivedEqualityCallsItselfThroughTheCache unit =
    match loweredDump("type Tree =\n    | Leaf\n    | Node(Tree, Int, Tree)\n    deriving {Eq}\n\nlet same (a: Tree) (b: Tree) = a == b\n\nsame(Leaf)(Leaf)") with
        | dump ->
            Unit
            |> (given (_) ->
                dump
                |> occurrences("CallClosure")
                |> test.assertEqual(8))
            |> (given (_) ->
                dump
                |> occurrences("\nfunction ")
                |> test.assertEqual(5))

// A type with neither a derived nor a written `Eq` has no evidence to dispatch through.
let expectMissingEqualityEvidenceIsReported unit =
    match loweringErrorFor("type Plain =\n    | Plain\n\nPlain == Plain") with
        | MissingCoreTraitEvidence(traitName, operandType) ->
            Unit
            |> (given (_) -> test.assertEqual("Eq")(traitName))
            |> (given (_) ->
                test.assertEqual(SemNamed(0)("Plain")([]))(operandType))
        | other -> test.fail("expected MissingCoreTraitEvidence, got " + Ashes.Trait.Show.show(other))

// The standard list implementation needs the element's dictionary threaded through: until that
// lands, a list comparison names the trait and the operand type it could not dispatch.
let expectListEqualityIsStillUnsupported unit =
    match loweringErrorFor(shapeSource + "let empty (list: List(Shape)) = list == []\n\nempty([Circle])") with
        | UnsupportedCoreTraitDispatch(traitName, operandType) ->
            Unit
            |> (given (_) -> test.assertEqual("Eq")(traitName))
            |> (given (_) ->
                test.assertEqual([]
                |> SemNamed(0)("Shape")
                |> SemList)(operandType))
        | other -> test.fail("expected UnsupportedCoreTraitDispatch, got " + Ashes.Trait.Show.show(other))

let runCoreTraitDispatchLoweringTests unit =
    Unit
    |> expectDerivedEqualityDispatchesThroughOneHelper
    |> expectEqualityUnifiesAVariableOperandWithTheOtherSide
    |> expectNotEqualNegatesTheDerivedEquality
    |> expectDerivedRecordEqualityComparesFieldsDirectly
    |> expectSuppliedNotEqualIsCalled
    |> expectRecursiveDerivedEqualityCallsItselfThroughTheCache
    |> expectMissingEqualityEvidenceIsReported
    |> expectListEqualityIsStillUnsupported
    |> (given (_) -> Ashes.IO.print("all self-hosted core trait dispatch lowering tests passed"))
