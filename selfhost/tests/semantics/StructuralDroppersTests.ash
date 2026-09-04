// Unit tests for the synthesized structural and ADT droppers, checked instruction for instruction
// against stage 0's lowered IR text for the same types, the `GetAdtField` lines carrying the
// OPT-24 tagless flag of a sole-constructor type as stage 0's do. Each
// synthesized function is described once, as its header line, its origin line, and its
// instruction lines, and the whole description is compared in one assertion.

import Ashes.Collection.List.append
import Ashes.Collection.List.length
import Ashes.Collection.List.map
import Ashes.Test as test
import AshesCompiler.Semantics.Ir
import AshesCompiler.Semantics.IrInstructions
import AshesCompiler.Semantics.IrOrigins
import AshesCompiler.Semantics.IrText
import AshesCompiler.Semantics.StructuralDroppers
import AshesCompiler.Semantics.TypeInference
import AshesCompiler.Semantics.Types
export (
    value runStructuralDroppersTests,
)

// The types under test, declared as constructor definitions in declaration order (a constructor's
// tag is its index among its type's constructors):
//   type Found = | items: List(Int) | label: Str
//   type Tree = | Leaf | Node(Tree, Int, Tree)
//   type Cell = | value: Int
//   type Choice = | Empty | Full(Cell, Int)
let foundType = SemNamed(1)("Found")([])

let treeType = SemNamed(2)("Tree")([])

let cellType = SemNamed(3)("Cell")([])

let choiceType = SemNamed(4)("Choice")([])

let recursive constructorScheme (fieldTypes: List(SemanticType)) (resultType: SemanticType) =
    match fieldTypes with
        | [] -> resultType
        | fieldType :: rest ->
            SemFunction(fieldType)(constructorScheme(rest)(resultType))(None)

let constructorDefinition (name: Str) (fieldTypes: List(SemanticType)) (resultType: SemanticType) (fieldNames: List(Str)) =
    ConstructorInferenceDefinition(
        name = name,
        scheme = TypeScheme(quantified = [], body = constructorScheme(fieldTypes)(resultType), constraints = []),
        fieldNames = fieldNames
    )

let dropperTestEnvironment unit =
    emptyTypeEnvironment(Unit) with constructors = [
        constructorDefinition("Found")([SemList(SemInt), SemString])(foundType)(["items", "label"]),
        constructorDefinition("Leaf")([])(treeType)([]),
        constructorDefinition("Node")([treeType, SemInt, treeType])(treeType)([]),
        constructorDefinition("Cell")([SemInt])(cellType)(["value"]),
        constructorDefinition("Empty")([])(choiceType)([]),
        constructorDefinition("Full")([cellType, SemInt])(choiceType)([])
    ]

let environmentConstructors (environment: TypeEnvironment) =
    match environment with
        | TypeEnvironment { constructors = constructors } -> constructors

let namedType (typeName: Str) (_environment: TypeEnvironment) =
    if typeName == "Found"
    then foundType
    else
        if typeName == "Tree"
        then treeType
        else
            if typeName == "Cell"
            then cellType
            else choiceType

let structuralDropperFor (semanticType: SemanticType) (nextLambdaId: Int) (nextLabelId: Int) (environment: TypeEnvironment) =
    synthesizeStructuralOwnerDropper(semanticType)(environmentConstructors(environment))(emptyDropperLabelCache)(nextLambdaId)(nextLabelId)

let adtDropperFor (semanticType: SemanticType) (nextLambdaId: Int) (nextLabelId: Int) (environment: TypeEnvironment) =
    synthesizeRuntimeManagedAdtDropper(semanticType)(environmentConstructors(environment))(emptyDropperLabelCache)(nextLambdaId)(nextLabelId)

let originLine (origin: Maybe(IrFunctionOrigin)) =
    match origin with
        | Some(IrFunctionOrigin { originKind = originKind, compilerOwner = Some(CompilerFunctionOwner { ownerKind = ownerKind, ownerName = ownerName }), stableDiscriminator = Some(discriminator) }) -> "origin " + Ashes.Trait.Show.show(originKind) + " " + Ashes.Trait.Show.show(ownerKind) + " " + ownerName + " " + discriminator
        | _ -> "origin (missing type owner)"

// The header, origin, and instruction lines of one synthesized function.
let describeFunction (function: IrFunction) =
    match function with
        | IrFunction { label = label, instructions = instructions, localCount = localCount, tempCount = tempCount, hasEnvAndArgParams = hasEnvAndArgParams, origin = origin } ->
            append(
                [
                    "function " + label + " locals=" + Ashes.Text.fromInt(localCount) + " temps=" + Ashes.Text.fromInt(tempCount) + (if hasEnvAndArgParams
                    then " env-and-arg"
                    else ""),
                    originLine(origin)
                ]
            )(
                map(formatIrInstruction)(instructions)
            )

// The description of the one function a synthesis produced, headed by its label and counters.
let describeSynthesis (synthesis: DropperSynthesis) =
    match synthesis with
        | DropperSynthesis { label = label, functions = function :: [], nextLambdaId = nextLambdaId, nextLabelId = nextLabelId } -> "synthesis " + Ashes.Trait.Show.show(label) + " nextLambda=" + Ashes.Text.fromInt(nextLambdaId) + " nextLabel=" + Ashes.Text.fromInt(nextLabelId) :: describeFunction(function)
        | DropperSynthesis { functions = functions } ->
            test.fail("expected exactly one synthesized function, got " + Ashes.Text.fromInt(length(functions)))

let recursive describeFunctions (functions: List(IrFunction)) =
    match functions with
        | [] -> []
        | function :: rest ->
            rest
            |> describeFunctions
            |> append(describeFunction(function))

// A record whose fields are a list of scalars and a string, released as stage 0 releases it: the
// list spine is walked from a slot, the string dropped once, and the record cell last.
let expectRecordWithListAndStringMatchesStageZero unit =
    Unit
    |> dropperTestEnvironment
    |> (given (environment) ->
        structuralDropperFor(namedType("Found")(environment))(1)(15)(environment))
    |> describeSynthesis
    |> test.assertEqual([
        "synthesis Some(\"__rcdrop_structural_1\") nextLambda=2 nextLabel=19",
        "function __rcdrop_structural_1 locals=3 temps=10 env-and-arg",
        "origin StructuralOwnerDropperOrigin TypeFunctionOwner Found Found",
        "    LoadLocal             Target=0 Slot=1",
        "    RcIsUnique            Target=1 SourceTemp=0",
        "    JumpIfFalse           CondTemp=1 Target=rc_drop_shared_15",
        "    GetAdtField           Target=2 Ptr=0 FieldIndex=0 Tagless=true",
        "    StoreLocal            Slot=2 Source=2",
        "  rcdrop_list_16:",
        "    LoadLocal             Target=3 Slot=2",
        "    LoadConstInt          Target=4 Value=0",
        "    CmpIntNe              Target=5 Left=3 Right=4",
        "    JumpIfFalse           CondTemp=5 Target=rcdrop_list_end_18",
        "    RcIsUnique            Target=6 SourceTemp=3",
        "    JumpIfFalse           CondTemp=6 Target=rcdrop_list_shared_17",
        "    LoadMemOffset         Target=7 BasePtr=3 OffsetBytes=8",
        "    RcDrop                SourceTemp=3 TypeName=List RuntimeManaged=true",
        "    StoreLocal            Slot=2 Source=7",
        "    Jump                  Target=rcdrop_list_16",
        "  rcdrop_list_shared_17:",
        "    RcDrop                SourceTemp=3 TypeName=List RuntimeManaged=true",
        "    Jump                  Target=rcdrop_list_end_18",
        "  rcdrop_list_end_18:",
        "    GetAdtField           Target=8 Ptr=0 FieldIndex=1 Tagless=true",
        "    RcDrop                SourceTemp=8 TypeName=String RuntimeManaged=true",
        "  rc_drop_shared_15:",
        "    RcDrop                SourceTemp=0 TypeName=Found RuntimeManaged=true",
        "    LoadConstInt          Target=9 Value=0",
        "    Return                Source=9"
    ])

// A list of records: the outer spine loop releases each unique head through the record's own
// walk (its inner list loop and string) before advancing to the tail.
let expectListOfRecordsMatchesStageZero unit =
    Unit
    |> dropperTestEnvironment
    |> (given (environment) ->
        structuralDropperFor(environment
        |> namedType("Found")
        |> SemList)(1)(18)(environment))
    |> describeSynthesis
    |> test.assertEqual([
        "synthesis Some(\"__rcdrop_structural_1\") nextLambda=2 nextLabel=25",
        "function __rcdrop_structural_1 locals=4 temps=16 env-and-arg",
        "origin StructuralOwnerDropperOrigin TypeFunctionOwner List(Found) List(Found)",
        "    LoadLocal             Target=0 Slot=1",
        "    StoreLocal            Slot=2 Source=0",
        "  rcdrop_list_18:",
        "    LoadLocal             Target=1 Slot=2",
        "    LoadConstInt          Target=2 Value=0",
        "    CmpIntNe              Target=3 Left=1 Right=2",
        "    JumpIfFalse           CondTemp=3 Target=rcdrop_list_end_20",
        "    RcIsUnique            Target=4 SourceTemp=1",
        "    JumpIfFalse           CondTemp=4 Target=rcdrop_list_shared_19",
        "    LoadMemOffset         Target=5 BasePtr=1 OffsetBytes=0",
        "    RcIsUnique            Target=6 SourceTemp=5",
        "    JumpIfFalse           CondTemp=6 Target=rc_drop_shared_21",
        "    GetAdtField           Target=7 Ptr=5 FieldIndex=0 Tagless=true",
        "    StoreLocal            Slot=3 Source=7",
        "  rcdrop_list_22:",
        "    LoadLocal             Target=8 Slot=3",
        "    LoadConstInt          Target=9 Value=0",
        "    CmpIntNe              Target=10 Left=8 Right=9",
        "    JumpIfFalse           CondTemp=10 Target=rcdrop_list_end_24",
        "    RcIsUnique            Target=11 SourceTemp=8",
        "    JumpIfFalse           CondTemp=11 Target=rcdrop_list_shared_23",
        "    LoadMemOffset         Target=12 BasePtr=8 OffsetBytes=8",
        "    RcDrop                SourceTemp=8 TypeName=List RuntimeManaged=true",
        "    StoreLocal            Slot=3 Source=12",
        "    Jump                  Target=rcdrop_list_22",
        "  rcdrop_list_shared_23:",
        "    RcDrop                SourceTemp=8 TypeName=List RuntimeManaged=true",
        "    Jump                  Target=rcdrop_list_end_24",
        "  rcdrop_list_end_24:",
        "    GetAdtField           Target=13 Ptr=5 FieldIndex=1 Tagless=true",
        "    RcDrop                SourceTemp=13 TypeName=String RuntimeManaged=true",
        "  rc_drop_shared_21:",
        "    RcDrop                SourceTemp=5 TypeName=Found RuntimeManaged=true",
        "    LoadMemOffset         Target=14 BasePtr=1 OffsetBytes=8",
        "    RcDrop                SourceTemp=1 TypeName=List RuntimeManaged=true",
        "    StoreLocal            Slot=2 Source=14",
        "    Jump                  Target=rcdrop_list_18",
        "  rcdrop_list_shared_19:",
        "    RcDrop                SourceTemp=1 TypeName=List RuntimeManaged=true",
        "    Jump                  Target=rcdrop_list_end_20",
        "  rcdrop_list_end_20:",
        "    LoadConstInt          Target=15 Value=0",
        "    Return                Source=15"
    ])

// A tuple with a list element and a string element: elements are read at word offsets and the
// tuple cell released last.
let expectTupleWithListMatchesStageZero unit =
    Unit
    |> dropperTestEnvironment
    |> structuralDropperFor(SemTuple([SemList(SemInt), SemString]))(1)(15)
    |> describeSynthesis
    |> test.assertEqual([
        "synthesis Some(\"__rcdrop_structural_1\") nextLambda=2 nextLabel=19",
        "function __rcdrop_structural_1 locals=3 temps=10 env-and-arg",
        "origin StructuralOwnerDropperOrigin TypeFunctionOwner (List(Int), Str) (List(Int), Str)",
        "    LoadLocal             Target=0 Slot=1",
        "    RcIsUnique            Target=1 SourceTemp=0",
        "    JumpIfFalse           CondTemp=1 Target=rc_drop_tuple_shared_15",
        "    LoadMemOffset         Target=2 BasePtr=0 OffsetBytes=0",
        "    StoreLocal            Slot=2 Source=2",
        "  rcdrop_list_16:",
        "    LoadLocal             Target=3 Slot=2",
        "    LoadConstInt          Target=4 Value=0",
        "    CmpIntNe              Target=5 Left=3 Right=4",
        "    JumpIfFalse           CondTemp=5 Target=rcdrop_list_end_18",
        "    RcIsUnique            Target=6 SourceTemp=3",
        "    JumpIfFalse           CondTemp=6 Target=rcdrop_list_shared_17",
        "    LoadMemOffset         Target=7 BasePtr=3 OffsetBytes=8",
        "    RcDrop                SourceTemp=3 TypeName=List RuntimeManaged=true",
        "    StoreLocal            Slot=2 Source=7",
        "    Jump                  Target=rcdrop_list_16",
        "  rcdrop_list_shared_17:",
        "    RcDrop                SourceTemp=3 TypeName=List RuntimeManaged=true",
        "    Jump                  Target=rcdrop_list_end_18",
        "  rcdrop_list_end_18:",
        "    LoadMemOffset         Target=8 BasePtr=0 OffsetBytes=8",
        "    RcDrop                SourceTemp=8 TypeName=String RuntimeManaged=true",
        "  rc_drop_tuple_shared_15:",
        "    RcDrop                SourceTemp=0 TypeName=Tuple RuntimeManaged=true",
        "    LoadConstInt          Target=9 Value=0",
        "    Return                Source=9"
    ])

// A recursive ADT's dropper switches on the constructor tag and calls itself for each
// self-typed field of a unique cell.
let expectRecursiveAdtDropperMatchesStageZero unit =
    Unit
    |> dropperTestEnvironment
    |> (given (environment) ->
        adtDropperFor(namedType("Tree")(environment))(0)(6)(environment))
    |> describeSynthesis
    |> test.assertEqual([
        "synthesis Some(\"__rcdrop_0\") nextLambda=1 nextLabel=9",
        "function __rcdrop_0 locals=2 temps=10 env-and-arg",
        "origin RuntimeManagedAdtDropperOrigin TypeFunctionOwner Tree Tree",
        "    LoadLocal             Target=0 Slot=1",
        "    RcIsUnique            Target=1 SourceTemp=0",
        "    JumpIfFalse           CondTemp=1 Target=rcdrop_shared_6",
        "    GetAdtTag             Target=2 Ptr=0",
        "    SwitchTag             TagTemp=2 Cases=[2] DefaultLabel=rcdrop_shared_6",
        "  rcdrop_ctor_7:",
        "    Jump                  Target=rcdrop_shared_6",
        "  rcdrop_ctor_8:",
        "    GetAdtField           Target=3 Ptr=0 FieldIndex=0",
        "    LoadConstInt          Target=4 Value=0",
        "    CallKnown             Target=5 FuncLabel=__rcdrop_0 EnvTemp=4 ArgTemp=3",
        "    GetAdtField           Target=6 Ptr=0 FieldIndex=2",
        "    LoadConstInt          Target=7 Value=0",
        "    CallKnown             Target=8 FuncLabel=__rcdrop_0 EnvTemp=7 ArgTemp=6",
        "    Jump                  Target=rcdrop_shared_6",
        "  rcdrop_shared_6:",
        "    RcDrop                SourceTemp=0 TypeName=Tree RuntimeManaged=true",
        "    LoadConstInt          Target=9 Value=0",
        "    Return                Source=9"
    ])

// An owned-child ADT's dropper releases the record child of its field-carrying constructor with
// a single drop, since that record owns no heap children of its own.
let expectOwnedChildAdtDropperMatchesStageZero unit =
    Unit
    |> dropperTestEnvironment
    |> (given (environment) ->
        adtDropperFor(namedType("Choice")(environment))(0)(7)(environment))
    |> describeSynthesis
    |> test.assertEqual([
        "synthesis Some(\"__rcdrop_0\") nextLambda=1 nextLabel=10",
        "function __rcdrop_0 locals=2 temps=5 env-and-arg",
        "origin RuntimeManagedAdtDropperOrigin TypeFunctionOwner Choice Choice",
        "    LoadLocal             Target=0 Slot=1",
        "    RcIsUnique            Target=1 SourceTemp=0",
        "    JumpIfFalse           CondTemp=1 Target=rcdrop_shared_7",
        "    GetAdtTag             Target=2 Ptr=0",
        "    SwitchTag             TagTemp=2 Cases=[2] DefaultLabel=rcdrop_shared_7",
        "  rcdrop_ctor_8:",
        "    Jump                  Target=rcdrop_shared_7",
        "  rcdrop_ctor_9:",
        "    GetAdtField           Target=3 Ptr=0 FieldIndex=0",
        "    RcDrop                SourceTemp=3 TypeName=Cell RuntimeManaged=true",
        "    Jump                  Target=rcdrop_shared_7",
        "  rcdrop_shared_7:",
        "    RcDrop                SourceTemp=0 TypeName=Choice RuntimeManaged=true",
        "    LoadConstInt          Target=4 Value=0",
        "    Return                Source=4"
    ])

let noDropperDescription (synthesis: DropperSynthesis) =
    match synthesis with
        | DropperSynthesis { label = label, functions = functions, nextLambdaId = nextLambdaId, nextLabelId = nextLabelId } ->
            "label=" + Ashes.Trait.Show.show(label) + " functions=" + Ashes.Text.fromInt(length(functions)) + " nextLambda=" + Ashes.Text.fromInt(nextLambdaId) + " nextLabel=" + Ashes.Text.fromInt(nextLabelId)

// A string, a record of scalars, or a tuple of scalars owns at most one allocation: no helper is
// named, nothing is synthesized, and no counter is consumed.
let expectSingleAllocationNeedsNoHelper unit =
    Unit
    |> (given (_) ->
        Unit
        |> dropperTestEnvironment
        |> structuralDropperFor(SemString)(1)(15)
        |> noDropperDescription
        |> test.assertEqual("label=None functions=0 nextLambda=1 nextLabel=15"))
    |> (given (_) ->
        Unit
        |> dropperTestEnvironment
        |> (given (environment) ->
            structuralDropperFor(namedType("Cell")(environment))(1)(15)(environment))
        |> noDropperDescription
        |> test.assertEqual("label=None functions=0 nextLambda=1 nextLabel=15"))
    |> (given (_) ->
        Unit
        |> dropperTestEnvironment
        |> structuralDropperFor(SemTuple([SemInt, SemBool]))(1)(15)
        |> noDropperDescription
        |> test.assertEqual("label=None functions=0 nextLambda=1 nextLabel=15"))

let resynthesizeThroughCache (environment: TypeEnvironment) (synthesis: DropperSynthesis) =
    match synthesis with
        | DropperSynthesis { label = Some(first), cache = cache, nextLambdaId = nextLambdaId, nextLabelId = nextLabelId } ->
            nextLabelId
            |> synthesizeStructuralOwnerDropper(namedType("Found")(environment))(environmentConstructors(environment))(cache)(nextLambdaId)
            |> noDropperDescription
            |> (given (description) -> "first=" + first + " second: " + description)
        | _ -> test.fail("expected a structural dropper for Found")

// The second request for the same type answers the cached label without synthesizing again or
// consuming counters.
let expectDropperIsSynthesizedOncePerType unit =
    Unit
    |> dropperTestEnvironment
    |> (given (environment) ->
        environment
        |> structuralDropperFor(namedType("Found")(environment))(1)(15)
        |> resynthesizeThroughCache(environment))
    |> test.assertEqual("first=__rcdrop_structural_1 second: label=Some(\"__rcdrop_structural_1\") functions=0 nextLambda=2 nextLabel=19")

let describeAllFunctions (synthesis: DropperSynthesis) =
    match synthesis with
        | DropperSynthesis { label = label, functions = functions } -> "synthesis " + Ashes.Trait.Show.show(label) :: describeFunctions(functions)

// A list of a recursive ADT: the list's structural dropper calls the ADT's own dropper for each
// unique head, and that dropper is completed first.
let expectStructuralDropperCallsNestedAdtDropper unit =
    Unit
    |> dropperTestEnvironment
    |> (given (environment) ->
        structuralDropperFor(environment
        |> namedType("Tree")
        |> SemList)(3)(20)(environment))
    |> describeAllFunctions
    |> test.assertEqual([
        "synthesis Some(\"__rcdrop_structural_3\")",
        "function __rcdrop_4 locals=2 temps=10 env-and-arg",
        "origin RuntimeManagedAdtDropperOrigin TypeFunctionOwner Tree Tree",
        "    LoadLocal             Target=0 Slot=1",
        "    RcIsUnique            Target=1 SourceTemp=0",
        "    JumpIfFalse           CondTemp=1 Target=rcdrop_shared_23",
        "    GetAdtTag             Target=2 Ptr=0",
        "    SwitchTag             TagTemp=2 Cases=[2] DefaultLabel=rcdrop_shared_23",
        "  rcdrop_ctor_24:",
        "    Jump                  Target=rcdrop_shared_23",
        "  rcdrop_ctor_25:",
        "    GetAdtField           Target=3 Ptr=0 FieldIndex=0",
        "    LoadConstInt          Target=4 Value=0",
        "    CallKnown             Target=5 FuncLabel=__rcdrop_4 EnvTemp=4 ArgTemp=3",
        "    GetAdtField           Target=6 Ptr=0 FieldIndex=2",
        "    LoadConstInt          Target=7 Value=0",
        "    CallKnown             Target=8 FuncLabel=__rcdrop_4 EnvTemp=7 ArgTemp=6",
        "    Jump                  Target=rcdrop_shared_23",
        "  rcdrop_shared_23:",
        "    RcDrop                SourceTemp=0 TypeName=Tree RuntimeManaged=true",
        "    LoadConstInt          Target=9 Value=0",
        "    Return                Source=9",
        "function __rcdrop_structural_3 locals=3 temps=10 env-and-arg",
        "origin StructuralOwnerDropperOrigin TypeFunctionOwner List(Tree) List(Tree)",
        "    LoadLocal             Target=0 Slot=1",
        "    StoreLocal            Slot=2 Source=0",
        "  rcdrop_list_20:",
        "    LoadLocal             Target=1 Slot=2",
        "    LoadConstInt          Target=2 Value=0",
        "    CmpIntNe              Target=3 Left=1 Right=2",
        "    JumpIfFalse           CondTemp=3 Target=rcdrop_list_end_22",
        "    RcIsUnique            Target=4 SourceTemp=1",
        "    JumpIfFalse           CondTemp=4 Target=rcdrop_list_shared_21",
        "    LoadMemOffset         Target=5 BasePtr=1 OffsetBytes=0",
        "    LoadConstInt          Target=6 Value=0",
        "    CallKnown             Target=7 FuncLabel=__rcdrop_4 EnvTemp=6 ArgTemp=5",
        "    LoadMemOffset         Target=8 BasePtr=1 OffsetBytes=8",
        "    RcDrop                SourceTemp=1 TypeName=List RuntimeManaged=true",
        "    StoreLocal            Slot=2 Source=8",
        "    Jump                  Target=rcdrop_list_20",
        "  rcdrop_list_shared_21:",
        "    RcDrop                SourceTemp=1 TypeName=List RuntimeManaged=true",
        "    Jump                  Target=rcdrop_list_end_22",
        "  rcdrop_list_end_22:",
        "    LoadConstInt          Target=9 Value=0",
        "    Return                Source=9"
    ])

let runStructuralDroppersTests unit =
    unit
    |> expectRecordWithListAndStringMatchesStageZero
    |> expectListOfRecordsMatchesStageZero
    |> expectTupleWithListMatchesStageZero
    |> expectRecursiveAdtDropperMatchesStageZero
    |> expectOwnedChildAdtDropperMatchesStageZero
    |> expectSingleAllocationNeedsNoHelper
    |> expectDropperIsSynthesizedOncePerType
    |> expectStructuralDropperCallsNestedAdtDropper
    |> (given (_) -> Ashes.IO.print("all self-hosted structural dropper tests passed"))
