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

// The standard implementations live in `Ashes.Trait`, which the stitcher always loads; a bare
// program only has the seeded placeholder bodies, whose binding names nothing defines.
let expectListEqualityWithoutTheTraitModuleNamesThePlaceholder unit =
    match loweringErrorFor(shapeSource + "let empty (list: List(Shape)) = list == []\n\nempty([Circle])") with
        | UnknownLoweringBinding(name) ->
            "__ashes_standard_trait_Eq_equal_list"
            |> Ashes.Text.startsWith(name)
            |> test.assertEqual(true)
        | other -> test.fail("expected UnknownLoweringBinding, got " + Ashes.Trait.Show.show(other))

let standardTraitSource unit =
    match Ashes.IO.File.readText("lib/Ashes/Trait.ash") with
        | Ok(source) -> source
        | Error(message) -> test.fail("could not read lib/Ashes/Trait.ash: " + message)

let withStandardTraits (source: Str) = standardTraitSource(Unit) + "\n\n" + source

// `==` on a list of a derived type goes through `Ashes.Trait`'s `Eq(List(a))`: its `equal` is
// lowered once as a generic closure taking the element's `equal` and `notEqual` as hidden
// parameters, the site applies it to the derived `Shape` methods (two calls) and calls the
// result on the operands (two more), and the `x == y` inside `equalLists` reaches the element
// evidence through those parameters. Both sites share the generic body.
let expectListEqualityDispatchesThroughTheStandardImplementation unit =
    match shapeSource + "let empty (list: List(Shape)) = list == []\n\nlet same (left: List(Shape)) (right: List(Shape)) = left == right\n\nempty([Circle]) && same([Circle])([Square])"
    |> withStandardTraits
    |> loweredDump with
        | dump ->
            Unit
            |> (given (_) ->
                dump
                |> occurrences("CallClosure")
                |> (given (count) -> test.assertEqual(true)(count >= 8)))
            |> (given (_) ->
                dump
                |> occurrences("[ClosureHelper from empty]")
                |> (given (count) -> test.assertEqual(true)(count >= 2)))

// A derived implementation of a parameterized type requires the parameter's `Eq`: comparing two
// `Box(Shape)` values applies the generic `Box` equality to the derived `Shape` methods.
let expectDerivedEqualityOfAParameterizedTypeThreadsTheElementEvidence unit =
    shapeSource + "type Box(a) =\n    | Box(a)\n    | Empty\n    deriving {Eq}\n\nlet same (a: Box(Shape)) (b: Box(Shape)) = a == b\n\nsame(Box(Circle))(Empty)"
    |> loweredDump
    |> occurrences("CallClosure")
    |> (given (count) -> test.assertEqual(true)(count >= 6))

// The requirement dictionaries nest: a list of lists applies the generic list equality to the
// generic list equality applied to the element's methods.
let expectNestedListEqualityNestsTheEvidence unit =
    shapeSource + "let same (a: List(List(Shape))) (b: List(List(Shape))) = a == b\n\nsame([[Circle]])([[Square]])"
    |> withStandardTraits
    |> loweredDump
    |> occurrences("CallClosure")
    |> (given (count) -> test.assertEqual(true)(count >= 8))

// `!=` inside a generic body reaches the requirement's `notEqual` through the active evidence.
let expectNotEqualInAGenericBodyUsesTheActiveEvidence unit =
    "type Pair(a) =\n    | Pair(a, a)\n\nimplement Eq(Pair(a)) requires {Eq(a)} =\n    | equal = given (left) -> given (right) -> match (left, right) with | (Pair(x, y), Pair(u, v)) -> !(x != u) && !(y != v)\n\nlet same (a: Pair(Int)) (b: Pair(Int)) = a == b\n\nsame(Pair(1, 2))(Pair(1, 2))"
    |> withStandardTraits
    |> loweredDump
    |> occurrences("CallClosure")
    |> (given (count) -> test.assertEqual(true)(count >= 4))

// `Ord` requires `Eq`, so its evidence plan always carries a supertrait plan. Those are ignored
// rather than rejected: a comparison at a concrete head resolves whatever it needs from the
// environment, and rejecting them made every comparison operator fail before reaching a method.
let expectComparisonAtAConcreteHeadDispatchesThroughOrd unit =
    "let ordered (a: Str) (b: Str) = a <= b\n\nordered(\"a\")(\"b\")"
    |> withStandardTraits
    |> loweredDump
    |> occurrences("CallClosure")
    |> (given (count) -> test.assertEqual(true)(count >= 2))

// The four comparison methods are trait defaults whose bodies call the sibling `Ord.compare`, a
// name lowering cannot resolve. They are emitted instead as a dispatch of `compare` followed by a
// tag test of the `Ordering` it returns, so the lowered body reads a tag it never allocates.
let expectComparisonReadsTheOrderingTag unit =
    match "let ordered (a: Str) (b: Str) = a < b\n\nordered(\"a\")(\"b\")"
    |> withStandardTraits
    |> loweredDump with
        | dump ->
            Unit
            |> (given (_) ->
                dump
                |> occurrences("GetAdtTag")
                |> (given (count) -> test.assertEqual(true)(count >= 1)))
            |> (given (_) ->
                dump
                |> occurrences("Ord.compare")
                |> test.assertEqual(0))

// `lessOrEqual` accepts `Less` or `Equal` and `greaterOrEqual` accepts `Greater` or `Equal`, so
// each ors two tag tests together while `<` and `>` need only one. `Unordered` is why the second
// pair cannot be a single range test.
let expectInclusiveComparisonsTestTwoTags unit =
    match ("let ordered (a: Str) (b: Str) = a >= b\n\nordered(\"a\")(\"b\")"
    |> withStandardTraits
    |> loweredDump, "let ordered (a: Str) (b: Str) = a > b\n\nordered(\"a\")(\"b\")"
    |> withStandardTraits
    |> loweredDump) with
        | (inclusive, strict) ->
            Unit
            |> (given (_) ->
                inclusive
                |> occurrences("OrInt")
                |> test.assertEqual(2))
            |> (given (_) ->
                strict
                |> occurrences("OrInt")
                |> test.assertEqual(1))

let runCoreTraitDispatchLoweringTests unit =
    Unit
    |> expectDerivedEqualityDispatchesThroughOneHelper
    |> expectEqualityUnifiesAVariableOperandWithTheOtherSide
    |> expectNotEqualNegatesTheDerivedEquality
    |> expectDerivedRecordEqualityComparesFieldsDirectly
    |> expectSuppliedNotEqualIsCalled
    |> expectRecursiveDerivedEqualityCallsItselfThroughTheCache
    |> expectMissingEqualityEvidenceIsReported
    |> expectListEqualityWithoutTheTraitModuleNamesThePlaceholder
    |> expectListEqualityDispatchesThroughTheStandardImplementation
    |> expectDerivedEqualityOfAParameterizedTypeThreadsTheElementEvidence
    |> expectNestedListEqualityNestsTheEvidence
    |> expectNotEqualInAGenericBodyUsesTheActiveEvidence
    |> expectComparisonAtAConcreteHeadDispatchesThroughOrd
    |> expectComparisonReadsTheOrderingTag
    |> expectInclusiveComparisonsTestTwoTags
    |> (given (_) -> Ashes.IO.print("all self-hosted core trait dispatch lowering tests passed"))
