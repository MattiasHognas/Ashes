// Unit tests for the tagless single-constructor ADT layout: the predicate's exclusions (a second
// constructor, a nullary constructor, a compiler-provided type, a zero-cost newtype, a resource
// handle, and a resource reached through a field, a type argument, a list, a tuple, or an
// earlier declaration), the byte offsets both layouts share with the backend, and the lowering's
// use of the flag: a tagless constructor allocates, stores, and loads with the flag set and is
// matched without a tag read, while a nullary or resource-bearing type keeps its tag word.

import Ashes.Collection.List.append
import Ashes.Test as test
import AshesCompiler.Frontend.Parser
import AshesCompiler.Frontend.Syntax
import AshesCompiler.Semantics.CoreLowering
import AshesCompiler.Semantics.Ir
import AshesCompiler.Semantics.IrInstructions
import AshesCompiler.Semantics.TaglessAdtLayout
import AshesCompiler.Semantics.Types
export (
    value runTaglessAdtLayoutTests,
)

let namedType (name: Str) = SemNamed(0)(name)([])

let recursive curriedConstructorBody (fields: List(SemanticType)) (resultType: SemanticType) =
    match fields with
        | [] -> resultType
        | field :: rest ->
            SemFunction(field)(curriedConstructorBody(rest)(resultType))(None)

let constructorScheme (fields: List(SemanticType)) (resultType: SemanticType) = TypeScheme(quantified = [], body = curriedConstructorBody(fields)(resultType), constraints = [])

let pointScheme =
    "Point"
    |> namedType
    |> constructorScheme([SemInt, SemInt])

let circleScheme =
    "Shape"
    |> namedType
    |> constructorScheme([SemInt])

let squareScheme =
    "Shape"
    |> namedType
    |> constructorScheme([SemInt])

let markerScheme =
    "Marker"
    |> namedType
    |> constructorScheme([])

let userIdScheme =
    "UserId"
    |> namedType
    |> constructorScheme([SemInt])

let someScheme =
    [SemVariable(1)]
    |> SemNamed(0)("Maybe")
    |> constructorScheme([SemVariable(1)])

let wrappedScheme =
    "Wrapped"
    |> namedType
    |> constructorScheme([namedType("FileHandle")])

let outerScheme =
    "Outer"
    |> namedType
    |> constructorScheme([namedType("Wrapped")])

let holderScheme =
    "Holder"
    |> namedType
    |> constructorScheme([SemOpaque("Handle")])

let namedHolderScheme =
    "NamedHolder"
    |> namedType
    |> constructorScheme([namedType("Handle")])

let bagScheme =
    "Bag"
    |> namedType
    |> constructorScheme(["FileHandle"
    |> namedType
    |> SemList])

let pairingScheme =
    "Pairing"
    |> namedType
    |> constructorScheme([SemTuple([SemInt, namedType("FileHandle")])])

let chainScheme =
    "Chain"
    |> namedType
    |> constructorScheme([SemInt, namedType("Chain")])

let boxScheme =
    TypeScheme(
        quantified = [(7, "a")],
        body = SemFunction(SemVariable(7))(SemNamed(0)("Box")([SemVariable(7)]))(None),
        constraints = []
    )

let boxedHandleScheme =
    "BoxedHandle"
    |> namedType
    |> constructorScheme([SemNamed(0)("Box")([namedType("FileHandle")])])

let boxedIntScheme =
    "BoxedInt"
    |> namedType
    |> constructorScheme([SemNamed(0)("Box")([SemInt])])

let allSchemes =
    [
        pointScheme,
        circleScheme,
        squareScheme,
        markerScheme,
        userIdScheme,
        someScheme,
        wrappedScheme,
        outerScheme,
        holderScheme,
        namedHolderScheme,
        bagScheme,
        pairingScheme,
        chainScheme,
        boxScheme,
        boxedHandleScheme,
        boxedIntScheme
    ]

let noDeclaredResource (_name: Str) = false

let handleIsDeclaredResource (name: Str) = name == "Handle"

let isTagless (scheme: TypeScheme) = isTaglessAdtConstructor(noDeclaredResource)(allSchemes)(false)(scheme)

let expectTaglessSingleConstructorRecord unit =
    pointScheme
    |> isTagless
    |> test.assertEqual(true)

let expectTaglessGenericSingleConstructor unit =
    boxScheme
    |> isTagless
    |> test.assertEqual(true)

let expectTaglessSelfRecursiveSingleConstructor unit =
    chainScheme
    |> isTagless
    |> test.assertEqual(true)

let expectTaglessGenericInstantiatedWithScalar unit =
    boxedIntScheme
    |> isTagless
    |> test.assertEqual(true)

let expectTaggedSecondConstructor unit =
    Unit
    |> (given (_) ->
        circleScheme
        |> isTagless
        |> test.assertEqual(false))
    |> (given (_) ->
        squareScheme
        |> isTagless
        |> test.assertEqual(false))

let expectTaggedNullaryConstructor unit =
    markerScheme
    |> isTagless
    |> test.assertEqual(false)

let expectTaggedZeroCostNewtype unit =
    userIdScheme
    |> isTaglessAdtConstructor(noDeclaredResource)(allSchemes)(true)
    |> test.assertEqual(false)

let expectTaggedCompilerProvidedType unit =
    someScheme
    |> isTagless
    |> test.assertEqual(false)

let expectTaggedResourceHandleField unit =
    wrappedScheme
    |> isTagless
    |> test.assertEqual(false)

let expectTaggedResourceReachedThroughEarlierType unit =
    outerScheme
    |> isTagless
    |> test.assertEqual(false)

let expectTaggedResourceReachedThroughTypeArgument unit =
    boxedHandleScheme
    |> isTagless
    |> test.assertEqual(false)

let expectTaggedResourceInsideList unit =
    bagScheme
    |> isTagless
    |> test.assertEqual(false)

let expectTaggedResourceInsideTuple unit =
    pairingScheme
    |> isTagless
    |> test.assertEqual(false)

let expectDeclaredResourceDecidedByCallback unit =
    Unit
    |> (given (_) ->
        holderScheme
        |> isTagless
        |> test.assertEqual(true))
    |> (given (_) ->
        namedHolderScheme
        |> isTagless
        |> test.assertEqual(true))
    |> (given (_) ->
        holderScheme
        |> isTaglessAdtConstructor(handleIsDeclaredResource)(allSchemes)(false)
        |> test.assertEqual(false))
    |> (given (_) ->
        namedHolderScheme
        |> isTaglessAdtConstructor(handleIsDeclaredResource)(allSchemes)(false)
        |> test.assertEqual(false))

let expectConstructorCountPerType unit =
    Unit
    |> (given (_) ->
        allSchemes
        |> countConstructorsOfType("Point")
        |> test.assertEqual(1))
    |> (given (_) ->
        allSchemes
        |> countConstructorsOfType("Shape")
        |> test.assertEqual(2))
    |> (given (_) ->
        allSchemes
        |> countConstructorsOfType("Absent")
        |> test.assertEqual(0))

let expectTaggedLayoutOffsets unit =
    Unit
    |> (given (_) -> test.assertEqual(0)(adtTagOffsetBytes))
    |> (given (_) ->
        false
        |> adtPayloadOffsetBytes
        |> test.assertEqual(8))
    |> (given (_) ->
        0
        |> adtFieldOffsetBytes(false)
        |> test.assertEqual(8))
    |> (given (_) ->
        2
        |> adtFieldOffsetBytes(false)
        |> test.assertEqual(24))
    |> (given (_) ->
        0
        |> adtAllocationSizeBytes(false)
        |> test.assertEqual(8))
    |> (given (_) ->
        2
        |> adtAllocationSizeBytes(false)
        |> test.assertEqual(24))

let expectTaglessLayoutOffsets unit =
    Unit
    |> (given (_) ->
        true
        |> adtPayloadOffsetBytes
        |> test.assertEqual(0))
    |> (given (_) ->
        0
        |> adtFieldOffsetBytes(true)
        |> test.assertEqual(0))
    |> (given (_) ->
        2
        |> adtFieldOffsetBytes(true)
        |> test.assertEqual(16))
    |> (given (_) ->
        2
        |> adtAllocationSizeBytes(true)
        |> test.assertEqual(16))

let parsedProgram (source: Str) =
    match parseProgram(source) with
        | ProgramParseResult { program = program, diagnostics = [] } -> program
        | ProgramParseResult { diagnostics = diagnostics } -> test.fail("program should parse cleanly: " + Ashes.Trait.Show.show(diagnostics))

let loweredProgramSource (source: Str) =
    match source
    |> parsedProgram
    |> lowerCoreProgram with
        | CoreLoweringResult { program = Some(program), error = None } -> program
        | CoreLoweringResult { error = Some(error) } ->
            error
            |> Ashes.Trait.Show.show
            |> (given (text) -> test.fail("program lowering failed: " + text))
        | _ -> test.fail("program lowering produced no program")

let recursive liftedInstructions (functions: List(IrFunction)) =
    match functions with
        | [] -> []
        | IrFunction { instructions = instructions } :: rest ->
            rest
            |> liftedInstructions
            |> append(instructions)

let allInstructions (program: IrProgram) =
    match program with
        | IrProgram { entryFunction = IrFunction { instructions = entry }, functions = functions } ->
            functions
            |> liftedInstructions
            |> append(entry)

let recursive countMatching predicate (instructions: List(IrInstruction)) =
    match instructions with
        | [] -> 0
        | IrInstruction { instruction = instruction } :: rest ->
            if predicate(instruction)
            then 1 + countMatching(predicate)(rest)
            else countMatching(predicate)(rest)

let isAllocAdtWith (tagless: Bool) (instruction: IrInstructionKind) =
    match instruction with
        | AllocAdt(_target, _tag, _fieldCount, _runtimeManaged, candidate) -> candidate == tagless
        | _ -> false

let isSetAdtFieldWith (tagless: Bool) (instruction: IrInstructionKind) =
    match instruction with
        | SetAdtField(_ptr, _fieldIndex, _source, candidate) -> candidate == tagless
        | _ -> false

let isGetAdtFieldWith (tagless: Bool) (instruction: IrInstructionKind) =
    match instruction with
        | GetAdtField(_target, _ptr, _fieldIndex, candidate) -> candidate == tagless
        | _ -> false

let isGetAdtTag (instruction: IrInstructionKind) =
    match instruction with
        | GetAdtTag(_target, _ptr) -> true
        | _ -> false

let isSwitchTag (instruction: IrInstructionKind) =
    match instruction with
        | SwitchTag(_tag, _cases, _default) -> true
        | _ -> false

let isCmpIntNe (instruction: IrInstructionKind) =
    match instruction with
        | CmpIntNe(_target, _left, _right) -> true
        | _ -> false

let taglessRecordSource = "type Point =\n    | x: Int\n    | y: Int\n\nlet p = Point(x = 3, y = 4)\n\nmatch p with\n    | Point(a, b) -> a + b"

// A tagless constructor allocates, stores, and loads with the flag set; its match keeps the
// `ptr != 0` guard and emits no tag read.
let expectTaglessRecordLoweredWithoutTagRead unit =
    (let instructions =
        taglessRecordSource
        |> loweredProgramSource
        |> allInstructions
    in
        Unit
        |> (given (_) ->
            instructions
            |> countMatching(isAllocAdtWith(true))
            |> test.assertEqual(1))
        |> (given (_) ->
            instructions
            |> countMatching(isAllocAdtWith(false))
            |> test.assertEqual(0))
        |> (given (_) ->
            instructions
            |> countMatching(isSetAdtFieldWith(true))
            |> test.assertEqual(2))
        |> (given (_) ->
            instructions
            |> countMatching(isGetAdtFieldWith(true))
            |> test.assertEqual(2))
        |> (given (_) ->
            instructions
            |> countMatching(isGetAdtFieldWith(false))
            |> test.assertEqual(0))
        |> (given (_) ->
            instructions
            |> countMatching(isGetAdtTag)
            |> test.assertEqual(0))
        |> (given (_) ->
            instructions
            |> countMatching(isCmpIntNe)
            |> test.assertEqual(1)))

let taglessFieldAccessSource = "type Point =\n    | x: Int\n    | y: Int\n\nlet p = Point(x = 3, y = 4)\nlet q = p with x = 5\nq.x + p.y"

// Record field access and record update read and write the tagless layout.
let expectTaglessRecordAccessAndUpdate unit =
    (let instructions =
        taglessFieldAccessSource
        |> loweredProgramSource
        |> allInstructions
    in
        Unit
        |> (given (_) ->
            instructions
            |> countMatching(isAllocAdtWith(true))
            |> test.assertEqual(2))
        |> (given (_) ->
            instructions
            |> countMatching(isSetAdtFieldWith(true))
            |> test.assertEqual(4))
        |> (given (_) ->
            instructions
            |> countMatching(isGetAdtFieldWith(true))
            |> test.assertEqual(3))
        |> (given (_) ->
            instructions
            |> countMatching(isGetAdtTag)
            |> test.assertEqual(0)))

let taglessTagGroupSource = "type Point =\n    | x: Int\n    | y: Int\n\nlet p = Point(x = 3, y = 4)\n\nmatch p with\n    | Point(1, b) -> b\n    | Point(a, _) -> a"

// Two arms of one tagless constructor dispatch through the tag-group path, whose sole group
// falls straight through: no tag read and no switch, while each case still loads both fields
// for its own sub-pattern tests.
let expectTaglessTagGroupSkipsSwitch unit =
    (let instructions =
        taglessTagGroupSource
        |> loweredProgramSource
        |> allInstructions
    in
        Unit
        |> (given (_) ->
            instructions
            |> countMatching(isGetAdtTag)
            |> test.assertEqual(0))
        |> (given (_) ->
            instructions
            |> countMatching(isSwitchTag)
            |> test.assertEqual(0))
        |> (given (_) ->
            instructions
            |> countMatching(isGetAdtFieldWith(true))
            |> test.assertEqual(4)))

let nestedTaglessSource = "type Inner =\n    | a: Int\n    | b: Int\n\ntype Outer =\n    | Left(Inner)\n    | Right(Int)\n\nlet o = Left(Inner(a = 1, b = 2))\n\nmatch o with\n    | Left(Inner(a, b)) -> a + b\n    | Right(n) -> n"

// A tagless cell nested inside a two-constructor cell: the outer cell keeps its tag and its tag
// reads (the group switch and the nested case's own tag test), the inner cell has neither. The
// outer cell's tagged field loads are a lower bound: a synthesized dropper adds its own.
let expectNestedTaglessInsideTagged unit =
    (let instructions =
        nestedTaglessSource
        |> loweredProgramSource
        |> allInstructions
    in
        Unit
        |> (given (_) ->
            instructions
            |> countMatching(isAllocAdtWith(true))
            |> test.assertEqual(1))
        |> (given (_) ->
            instructions
            |> countMatching(isAllocAdtWith(false))
            |> test.assertEqual(1))
        |> (given (_) ->
            instructions
            |> countMatching(isGetAdtFieldWith(true))
            |> test.assertEqual(2))
        |> (given (_) ->
            instructions
            |> countMatching(isGetAdtFieldWith(false))
            |> (given (count) -> test.assertEqual(true)(count >= 2)))
        |> (given (_) ->
            instructions
            |> countMatching(isGetAdtTag)
            |> test.assertEqual(2)))

let nullarySource = "type Marker =\n    | Marker\n\nlet m = Marker\n\nmatch m with\n    | Marker -> 1"

// A nullary single-constructor type keeps its tag word and its tag test.
let expectNullarySingleConstructorStaysTagged unit =
    (let instructions =
        nullarySource
        |> loweredProgramSource
        |> allInstructions
    in
        Unit
        |> (given (_) ->
            instructions
            |> countMatching(isAllocAdtWith(false))
            |> test.assertEqual(1))
        |> (given (_) ->
            instructions
            |> countMatching(isAllocAdtWith(true))
            |> test.assertEqual(0))
        |> (given (_) ->
            instructions
            |> countMatching(isGetAdtTag)
            |> test.assertEqual(1)))

let resourceBearingSource = "type Holder =\n    | file: FileHandle\n    | count: Int\n\nlet countOf (holder: Holder) =\n    match holder with\n        | Holder(_, count) -> count\n\n0"

// A resource-bearing record keeps its tag word: its field loads and its tag test stay tagged.
let expectResourceBearingRecordStaysTagged unit =
    (let instructions =
        resourceBearingSource
        |> loweredProgramSource
        |> allInstructions
    in
        Unit
        |> (given (_) ->
            instructions
            |> countMatching(isGetAdtFieldWith(true))
            |> test.assertEqual(0))
        |> (given (_) ->
            instructions
            |> countMatching(isGetAdtFieldWith(false))
            |> test.assertEqual(2))
        |> (given (_) ->
            instructions
            |> countMatching(isGetAdtTag)
            |> test.assertEqual(1)))

let builtinHandleFieldSource = "type Owner =\n    | proc: Process\n    | count: Int\n\nlet countOf (owner: Owner) =\n    match owner with\n        | Owner(_, count) -> count\n\n0"

// A compiler-provided handle type named as a field stays that concrete type rather than becoming
// an implicit type parameter, so the type annotates a parameter without an arity mismatch.
let expectBuiltinHandleFieldStaysConcrete unit =
    (let instructions =
        builtinHandleFieldSource
        |> loweredProgramSource
        |> allInstructions
    in
        instructions
        |> countMatching(isGetAdtFieldWith(false))
        |> test.assertEqual(2))

let genericTaglessSource = "type Box(a) =\n    | value: a\n\nlet b = Box(value = 5)\n\nmatch b with\n    | Box(v) -> v"

// A generic single-constructor type is tagless whatever its argument.
let expectGenericSingleConstructorTagless unit =
    (let instructions =
        genericTaglessSource
        |> loweredProgramSource
        |> allInstructions
    in
        Unit
        |> (given (_) ->
            instructions
            |> countMatching(isAllocAdtWith(true))
            |> test.assertEqual(1))
        |> (given (_) ->
            instructions
            |> countMatching(isGetAdtFieldWith(true))
            |> test.assertEqual(1))
        |> (given (_) ->
            instructions
            |> countMatching(isGetAdtTag)
            |> test.assertEqual(0)))

let runTaglessAdtLayoutTests unit =
    Unit
    |> expectTaglessSingleConstructorRecord
    |> expectTaglessGenericSingleConstructor
    |> expectTaglessSelfRecursiveSingleConstructor
    |> expectTaglessGenericInstantiatedWithScalar
    |> expectTaggedSecondConstructor
    |> expectTaggedNullaryConstructor
    |> expectTaggedZeroCostNewtype
    |> expectTaggedCompilerProvidedType
    |> expectTaggedResourceHandleField
    |> expectTaggedResourceReachedThroughEarlierType
    |> expectTaggedResourceReachedThroughTypeArgument
    |> expectTaggedResourceInsideList
    |> expectTaggedResourceInsideTuple
    |> expectDeclaredResourceDecidedByCallback
    |> expectConstructorCountPerType
    |> expectTaggedLayoutOffsets
    |> expectTaglessLayoutOffsets
    |> expectTaglessRecordLoweredWithoutTagRead
    |> expectTaglessRecordAccessAndUpdate
    |> expectTaglessTagGroupSkipsSwitch
    |> expectNestedTaglessInsideTagged
    |> expectNullarySingleConstructorStaysTagged
    |> expectResourceBearingRecordStaysTagged
    |> expectBuiltinHandleFieldStaysConcrete
    |> expectGenericSingleConstructorTagless
    |> (given (_) -> Ashes.IO.print("tagless adt layout tests passed"))
