import Ashes.IO
import Ashes.Test as test
import AshesCompiler.Frontend.Syntax
import AshesCompiler.Semantics.CoreBuiltinLowering
import AshesCompiler.Semantics.CoreLowering
import AshesCompiler.Semantics.Ir
import AshesCompiler.Semantics.IrInstructions
import AshesCompiler.Semantics.IrText
import AshesCompiler.Semantics.TypeSchemes
import AshesCompiler.Semantics.Types
export (
    value runCoreLoweringTests,
)

let loweredProgram expression =
    match lowerCoreExpression(expression) with
        | CoreLoweringResult { program = Some(program), error = None } -> program
        | CoreLoweringResult { error = Some(error) } ->
            error
            |> Ashes.Trait.Show.show
            |> (given (text) -> test.fail("core lowering failed: " + text))
        | _ -> test.fail("core lowering produced no program")

let loweredType expression =
    match lowerCoreExpression(expression) with
        | CoreLoweringResult { semanticType = semanticType, error = None } -> semanticType
        | CoreLoweringResult { error = Some(error) } ->
            error
            |> Ashes.Trait.Show.show
            |> (given (text) -> test.fail("core lowering failed: " + text))
        | _ -> test.fail("core lowering produced no type")

let dump expression =
    formatIr(loweredProgram(expression))(LoweredIr)(None)

let loweredProgramWithLayouts layouts expression =
    match lowerCoreExpressionWithLayouts(layouts)(expression) with
        | CoreLoweringResult { program = Some(program), error = None } -> program
        | CoreLoweringResult { error = Some(error) } ->
            error
            |> Ashes.Trait.Show.show
            |> (given (text) -> test.fail("core lowering with layouts failed: " + text))
        | _ -> test.fail("core lowering with layouts produced no program")

let entryInstructions program =
    match program with
        | IrProgram { entryFunction = IrFunction { instructions = instructions } } -> instructions

let entryLocalCount program =
    match program with
        | IrProgram { entryFunction = IrFunction { localCount = localCount } } -> localCount

let maybeIntType = SemNamed(40)("Maybe")([SemInt])

let pointType = SemNamed(41)("Point")([])

let userIdType = SemNamed(42)("UserId")([])

let pairType = SemNamed(43)("Pair")([])

let shadeType = SemNamed(43)("Shade")([])

let shadeLayout (name: Str) (tag: Int) =
    CoreConstructorLayout(
        name = name,
        tag = tag,
        scheme = TypeScheme(quantified = [], body = shadeType, constraints = []),
        fieldNames = [],
        isZeroCost = false,
        tagless = false
    )

let structuralLayouts =
    [
        shadeLayout("Red")(0),
        shadeLayout("Green")(1),
        shadeLayout("Blue")(2),
        shadeLayout("Cyan")(3),
        shadeLayout("Magenta")(4),
        CoreConstructorLayout(
            name = "None",
            tag = 0,
            scheme = TypeScheme(quantified = [], body = maybeIntType, constraints = []),
            fieldNames = [],
            isZeroCost = false,
            tagless = false
        ),
        CoreConstructorLayout(
            name = "Some",
            tag = 1,
            scheme = TypeScheme(
                quantified = [],
                body = SemFunction(SemInt)(maybeIntType)(None),
                constraints = []
            ),
            fieldNames = [],
            isZeroCost = false,
            tagless = false
        ),
        CoreConstructorLayout(
            name = "Point",
            tag = 0,
            scheme = TypeScheme(
                quantified = [],
                body = SemFunction(SemInt)(SemFunction(SemInt)(pointType)(None))(None),
                constraints = []
            ),
            fieldNames = ["x", "y"],
            isZeroCost = false,
            tagless = false
        ),
        CoreConstructorLayout(
            name = "UserId",
            tag = 0,
            scheme = TypeScheme(
                quantified = [],
                body = SemFunction(SemInt)(userIdType)(None),
                constraints = []
            ),
            fieldNames = [],
            isZeroCost = true,
            tagless = false
        ),
        CoreConstructorLayout(
            name = "Pair",
            tag = 0,
            scheme = TypeScheme(
                quantified = [],
                body = SemFunction(SemInt)(SemFunction(SemInt)(pairType)(None))(None),
                constraints = []
            ),
            fieldNames = [],
            isZeroCost = false,
            tagless = false
        )
    ]

let recursive containsAlloc size instructions =
    match instructions with
        | [] -> false
        | IrInstruction { instruction = Alloc(_target, candidate, _managed) } :: rest ->
            if size == candidate
            then true
            else containsAlloc(size)(rest)
        | _ :: rest -> containsAlloc(size)(rest)

let recursive containsAllocAdt tag fieldCount instructions =
    match instructions with
        | [] -> false
        | IrInstruction { instruction = AllocAdt(_target, candidateTag, candidateFields, _managed, _tagless) } :: rest ->
            let matches =
                if tag == candidateTag
                then fieldCount == candidateFields
                else false
            in
                if matches
                then true
                else containsAllocAdt(tag)(fieldCount)(rest)
        | _ :: rest -> containsAllocAdt(tag)(fieldCount)(rest)

let recursive containsLoadOffset offset instructions =
    match instructions with
        | [] -> false
        | IrInstruction { instruction = LoadMemOffset(_target, _base, candidate) } :: rest ->
            if offset == candidate
            then true
            else containsLoadOffset(offset)(rest)
        | _ :: rest -> containsLoadOffset(offset)(rest)

let recursive containsGetAdtField index instructions =
    match instructions with
        | [] -> false
        | IrInstruction { instruction = GetAdtField(_target, _base, candidate, _tagless) } :: rest ->
            if index == candidate
            then true
            else containsGetAdtField(index)(rest)
        | _ :: rest -> containsGetAdtField(index)(rest)

let recursive containsGetAdtTag instructions =
    match instructions with
        | [] -> false
        | IrInstruction { instruction = GetAdtTag(_, _) } :: _rest -> true
        | _ :: rest -> containsGetAdtTag(rest)

let recursive functionsContainAllocAdt tag fieldCount functions =
    match functions with
        | [] -> false
        | IrFunction { instructions = instructions } :: rest ->
            if containsAllocAdt(tag)(fieldCount)(instructions)
            then true
            else functionsContainAllocAdt(tag)(fieldCount)(rest)

let programContainsAllocAdt tag fieldCount program =
    match program with
        | IrProgram { entryFunction = IrFunction { instructions = instructions }, functions = functions } ->
            if containsAllocAdt(tag)(fieldCount)(instructions)
            then true
            else functionsContainAllocAdt(tag)(fieldCount)(functions)

let constantLocalExpression =
    ExprLet(
        "x",
        ExprInt(7),
        ExprVar("x"),
        [],
        None,
        []
    )

let expectConstantAndLocal unit =
    ((given (_) ->
        constantLocalExpression
        |> dump
        |> test.assertEqual([
            "IR (lowered)",
            "============",
            "",
            "function _start_main  [ProgramEntry]",
            "  locals=4 temps=2",
            "    SaveArenaState        CursorLocalSlot=0 EndLocalSlot=1",
            "    LoadConstInt          Target=0 Value=7",
            "    StoreLocal            Slot=2 Source=0",
            "    LoadLocal             Target=1 Slot=2",
            "    RestoreArenaState     CursorLocalSlot=0 EndLocalSlot=1 PreRestoreEndSlot=3",
            "    ReclaimArenaChunks    SavedEndSlot=1 PreRestoreEndSlot=3",
            "    Return                Source=1",
            ""
        ])))(unit)

let expectScalarConstantTypes unit =
    unit
    |> (given (_) ->
        "255u8"
        |> ExprUInt(255)(8)
        |> loweredType
        |> test.assertEqual(SemUInt(8)))
    |> (given (_) ->
        "1.5"
        |> ExprFloat(1.5)
        |> loweredType
        |> test.assertEqual(SemFloat))
    |> (given (_) ->
        ExprString("text")
        |> loweredType
        |> test.assertEqual(SemString))
    |> (given (_) ->
        ExprRune(65)
        |> loweredType
        |> test.assertEqual(SemRune))
    |> (given (_) ->
        ExprBool(true)
        |> loweredType
        |> test.assertEqual(SemBool))

let captureExpression =
    ExprLet(
        "x",
        ExprInt(7),
        ExprLambda(
            "y",
            ExprVar("x"),
            None
        ),
        [],
        None,
        []
    )

let expectCaptureAndLiftedFunction unit =
    ((given (_) ->
        captureExpression
        |> dump
        |> test.assertEqual([
            "IR (lowered)",
            "============",
            "",
            "function lambda_0  [ClosureHelper]",
            "  locals=2 temps=1",
            "    LoadEnv               Target=0 Index=0",
            "    Return                Source=0",
            "",
            "function lambda_0$env_normalize  [ClosureEnvironmentNormalizer]",
            "  locals=2 temps=4",
            "    LoadLocal             Target=0 Slot=0",
            "    LoadLocal             Target=1 Slot=1",
            "    LoadMemOffset         Target=2 BasePtr=0 OffsetBytes=0",
            "    StoreMemOffset        BasePtr=1 OffsetBytes=0 Source=2",
            "    LoadConstInt          Target=3 Value=0",
            "    Return                Source=3",
            "",
            "function _start_main  [ProgramEntry]",
            "  locals=4 temps=4",
            "    SaveArenaState        CursorLocalSlot=0 EndLocalSlot=1",
            "    LoadConstInt          Target=0 Value=7",
            "    StoreLocal            Slot=2 Source=0",
            "    Alloc                 Target=1 SizeBytes=8",
            "    LoadLocal             Target=2 Slot=2",
            "    StoreMemOffset        BasePtr=1 OffsetBytes=0 Source=2",
            "    MakeClosure           Target=3 FuncLabel=lambda_0 EnvPtrTemp=1 EnvSizeBytes=8",
            "    Return                Source=3",
            ""
        ])))(unit)

let immediateCallExpression =
    ExprCall(
        ExprLambda(
            "x",
            ExprVar("x"),
            None
        ),
        ExprInt(41),
        false,
        callArgumentsInline
    )

let expectStrictImmediateCall unit =
    unit
    |> (given (_) ->
        immediateCallExpression
        |> dump
        |> test.assertEqual([
            "IR (lowered)",
            "============",
            "",
            "function lambda_0  [ClosureHelper]",
            "  locals=2 temps=1",
            "    LoadLocal             Target=0 Slot=1",
            "    Return                Source=0",
            "",
            "function _start_main  [ProgramEntry]",
            "  locals=3 temps=4",
            "    SaveArenaState        CursorLocalSlot=0 EndLocalSlot=1",
            "    LoadConstInt          Target=0 Value=0",
            "    MakeClosureStack      Target=1 FuncLabel=lambda_0 EnvPtrTemp=0 EnvSizeBytes=0",
            "    LoadConstInt          Target=2 Value=41",
            "    CallClosure           Target=3 ClosureTemp=1 ArgTemp=2",
            "    RestoreArenaState     CursorLocalSlot=0 EndLocalSlot=1 PreRestoreEndSlot=2",
            "    ReclaimArenaChunks    SavedEndSlot=1 PreRestoreEndSlot=2",
            "    Return                Source=3",
            ""
        ]))
    |> (given (_) ->
        immediateCallExpression
        |> loweredType
        |> test.assertEqual(SemInt))

let partialApplicationExpression =
    ExprLet(
        "choose",
        ExprLambda(
            "x",
            ExprLambda(
                "y",
                ExprVar("x"),
                None
            ),
            None
        ),
        ExprCall(
            ExprVar("choose"),
            ExprInt(1),
            false,
            callArgumentsInline
        ),
        [],
        None,
        []
    )

let expectPartialApplicationOrder unit =
    ((given (_) ->
        partialApplicationExpression
        |> dump
        |> test.assertEqual([
            "IR (lowered)",
            "============",
            "",
            "function lambda_1  [ClosureHelper from choose]",
            "  locals=2 temps=1",
            "    LoadEnv               Target=0 Index=0",
            "    Return                Source=0",
            "",
            "function lambda_0  [SourceFunction from choose]",
            "  locals=2 temps=3",
            "    Alloc                 Target=0 SizeBytes=8",
            "    LoadLocal             Target=1 Slot=1",
            "    StoreMemOffset        BasePtr=0 OffsetBytes=0 Source=1",
            "    MakeClosure           Target=2 FuncLabel=lambda_1 EnvPtrTemp=0 EnvSizeBytes=8",
            "    Return                Source=2",
            "",
            "function _start_main  [ProgramEntry]",
            "  locals=8 temps=8",
            "    SaveArenaState        CursorLocalSlot=0 EndLocalSlot=1",
            "    LoadConstInt          Target=0 Value=0",
            "    MakeClosureStack      Target=1 FuncLabel=lambda_0 EnvPtrTemp=0 EnvSizeBytes=0",
            "    StoreLocal            Slot=2 Source=1",
            "    SaveArenaState        CursorLocalSlot=3 EndLocalSlot=4",
            "    LoadLocal             Target=2 Slot=2",
            "    Borrow                Target=3 SourceTemp=2",
            "    LoadConstInt          Target=4 Value=1",
            "    CallClosure           Target=5 ClosureTemp=3 ArgTemp=4",
            "    StoreLocal            Slot=6 Source=5",
            "    LoadLocal             Target=6 Slot=2",
            "    CleanupResource       SourceTemp=6 TypeName=Function",
            "    LoadLocal             Target=7 Slot=6",
            "    Return                Source=7",
            ""
        ])))(unit)

let expectStringInterning unit =
    ((given (_) ->
        []
        |> ExprLet(
            "first",
            ExprString("same"),
            ExprLet(
                "second",
                ExprString("same"),
                ExprVar("second"),
                [],
                None,
                []
            ),
            [],
            None
        )
        |> loweredProgram
        |> (given (program) ->
            match program with
                | IrProgram { stringLiterals = literal :: [] } ->
                    match literal with
                        | IrStringLiteral { label = "str_0", value = "same" } -> Unit
                        | _ -> test.fail("equal string constants should retain the first literal")
                | _ -> test.fail("equal string constants should share one deterministic literal"))))(unit)

let conditionalExpression =
    ExprIf(
        ExprBool(true),
        ExprInt(1),
        ExprInt(2)
    )

let expectConditionalControlFlow unit =
    ((given (_) ->
        conditionalExpression
        |> dump
        |> test.assertEqual([
            "IR (lowered)",
            "============",
            "",
            "function _start_main  [ProgramEntry]",
            "  locals=1 temps=4",
            "    LoadConstBool         Target=0 Value=true",
            "    JumpIfFalse           CondTemp=0 Target=else_0",
            "    LoadConstInt          Target=1 Value=1",
            "    StoreLocal            Slot=0 Source=1",
            "    Jump                  Target=endif_1",
            "  else_0:",
            "    LoadConstInt          Target=2 Value=2",
            "    StoreLocal            Slot=0 Source=2",
            "  endif_1:",
            "    LoadLocal             Target=3 Slot=0",
            "    Return                Source=3",
            ""
        ])))(unit)

let guardedMatchExpression =
    ExprMatch(
        ExprInt(3),
        [
            (PatternInt(2), ExprInt(20), None),
            (PatternVar("n"), ExprVar("n"), Some(ExprBool(false))),
            (PatternWildcard, ExprInt(40), None)
        ],
        None
    )

// Each arm carries its own arena bracket and cleanup block, stage 0's exact shape: the label
// chain runs cleanup_3 -> next_2 -> cleanup_5 -> next_4 -> cleanup_6 -> none_1, and every arm
// restores on both exits (before its jump to the end, and again in its cleanup block).
let expectGuardedMatchOrder unit =
    ((given (_) ->
        guardedMatchExpression
        |> dump
        |> test.assertEqual([
            "IR (lowered)",
            "============",
            "",
            "function _start_main  [ProgramEntry]",
            "  locals=14 temps=9",
            "    LoadConstInt          Target=0 Value=3",
            "    SaveArenaState        CursorLocalSlot=1 EndLocalSlot=2",
            "    LoadConstInt          Target=1 Value=2",
            "    CmpIntEq              Target=2 Left=0 Right=1",
            "    JumpIfFalse           CondTemp=2 Target=match_arm_cleanup_3",
            "    LoadConstInt          Target=3 Value=20",
            "    StoreLocal            Slot=0 Source=3",
            "    RestoreArenaState     CursorLocalSlot=1 EndLocalSlot=2 PreRestoreEndSlot=3",
            "    ReclaimArenaChunks    SavedEndSlot=2 PreRestoreEndSlot=3",
            "    Jump                  Target=match_end_0",
            "  match_arm_cleanup_3:",
            "    RestoreArenaState     CursorLocalSlot=1 EndLocalSlot=2 PreRestoreEndSlot=4",
            "    ReclaimArenaChunks    SavedEndSlot=2 PreRestoreEndSlot=4",
            "    Jump                  Target=match_next_2",
            "  match_next_2:",
            "    SaveArenaState        CursorLocalSlot=5 EndLocalSlot=6",
            "    StoreLocal            Slot=7 Source=0",
            "    LoadConstBool         Target=4",
            "    JumpIfFalse           CondTemp=4 Target=match_arm_cleanup_5",
            "    LoadLocal             Target=5 Slot=7",
            "    StoreLocal            Slot=0 Source=5",
            "    RestoreArenaState     CursorLocalSlot=5 EndLocalSlot=6 PreRestoreEndSlot=8",
            "    ReclaimArenaChunks    SavedEndSlot=6 PreRestoreEndSlot=8",
            "    Jump                  Target=match_end_0",
            "  match_arm_cleanup_5:",
            "    RestoreArenaState     CursorLocalSlot=5 EndLocalSlot=6 PreRestoreEndSlot=9",
            "    ReclaimArenaChunks    SavedEndSlot=6 PreRestoreEndSlot=9",
            "    Jump                  Target=match_next_4",
            "  match_next_4:",
            "    SaveArenaState        CursorLocalSlot=10 EndLocalSlot=11",
            "    LoadConstInt          Target=6 Value=40",
            "    StoreLocal            Slot=0 Source=6",
            "    RestoreArenaState     CursorLocalSlot=10 EndLocalSlot=11 PreRestoreEndSlot=12",
            "    ReclaimArenaChunks    SavedEndSlot=11 PreRestoreEndSlot=12",
            "    Jump                  Target=match_end_0",
            "  match_arm_cleanup_6:",
            "    RestoreArenaState     CursorLocalSlot=10 EndLocalSlot=11 PreRestoreEndSlot=13",
            "    ReclaimArenaChunks    SavedEndSlot=11 PreRestoreEndSlot=13",
            "    Jump                  Target=match_none_1",
            "  match_none_1:",
            "    LoadConstInt          Target=7 Value=0",
            "    StoreLocal            Slot=0 Source=7",
            "  match_end_0:",
            "    LoadLocal             Target=8 Slot=0",
            "    Return                Source=8",
            ""
        ])))(unit)

let recursiveExpression =
    ExprLetRecursive(
        "loop",
        ExprLambda(
            "n",
            ExprCall(
                ExprVar("loop"),
                ExprVar("n"),
                false,
                callArgumentsInline
            ),
            None
        ),
        ExprVar("loop"),
        [],
        None,
        []
    )

let expectRecursiveSelfReference unit =
    ((given (_) ->
        recursiveExpression
        |> dump
        |> test.assertEqual([
            "IR (lowered)",
            "============",
            "",
            "function lambda_0  [SourceFunction from loop]",
            "  locals=5 temps=4",
            "    SaveArenaState        CursorLocalSlot=2 EndLocalSlot=3",
            "    LoadLocal             Target=1 Slot=0",
            "    MakeClosure           Target=0 FuncLabel=lambda_0 EnvPtrTemp=1 EnvSizeBytes=0",
            "    LoadLocal             Target=2 Slot=1",
            "    CallClosure           Target=3 ClosureTemp=0 ArgTemp=2",
            "    Return                Source=3",
            "",
            "function _start_main  [ProgramEntry]",
            "  locals=1 temps=3",
            "    LoadConstInt          Target=0 Value=0",
            "    MakeClosure           Target=1 FuncLabel=lambda_0 EnvPtrTemp=0 EnvSizeBytes=0",
            "    StoreLocal            Slot=0 Source=1",
            "    LoadLocal             Target=2 Slot=0",
            "    Return                Source=2",
            ""
        ])))(unit)

let mutualBinding name sibling =
    (name, ExprLambda(
        "n",
        ExprCall(
            ExprVar(sibling),
            ExprVar("n"),
            false,
            callArgumentsInline
        ),
        None
    ))

let recursive hasClosureFor expectedLabel instructions =
    match instructions with
        | [] -> false
        | IrInstruction { instruction = MakeClosure(_target, label, _environment, _size, _managed, _cell, _topLevel) } :: rest ->
            if label == expectedLabel
            then true
            else hasClosureFor(expectedLabel)(rest)
        | _ :: rest -> hasClosureFor(expectedLabel)(rest)

let expectMutualMembers functions =
    match functions with
        | IrFunction { label = "recgroup_0_even", instructions = evenInstructions } :: IrFunction { label = "recgroup_1_odd", instructions = oddInstructions } :: [] ->
            Unit
            |> (given (_) ->
                evenInstructions
                |> hasClosureFor("recgroup_1_odd")
                |> test.assertEqual(true))
            |> (given (_) ->
                oddInstructions
                |> hasClosureFor("recgroup_0_even")
                |> test.assertEqual(true))
        | _ -> test.fail("mutual recursion should retain source member order and labels")

let expectMutualRecursiveGroup unit =
    ((given (_) ->
        ExprVar("even")
        |> lowerCoreRecursiveGroup([
            mutualBinding("even")("odd"),
            mutualBinding("odd")("even")
        ])
        |> (given (result) ->
            match result with
                | CoreLoweringResult { program = Some(IrProgram { functions = functions }), error = None } ->
                    expectMutualMembers(
                        functions
                    )
                | CoreLoweringResult { error = Some(error) } ->
                    error
                    |> Ashes.Trait.Show.show
                    |> (given (text) -> test.fail("mutual recursion lowering failed: " + text))
                | _ -> test.fail("mutual recursion lowering produced no program"))))(unit)

let expectTupleListAndStringLayouts unit =
    ((given (_) ->
        ExprTuple([
            ExprInt(1),
            ExprList([
                ExprInt(2),
                ExprInt(3)
            ])(true),
            ExprString("tail")
        ])
        |> loweredProgram
        |> entryInstructions
        |> (given (instructions) ->
            Unit
            |> (given (_) ->
                instructions
                |> containsAlloc(16)
                |> test.assertEqual(true))
            |> (given (_) ->
                instructions
                |> containsAlloc(24)
                |> test.assertEqual(true)))))(unit)

let expectOrdinaryAndZeroCostConstructors unit =
    unit
    |> (given (_) ->
        callArgumentsInline
        |> ExprCall(
            ExprVar("Some"),
            ExprInt(7),
            false
        )
        |> loweredProgramWithLayouts(structuralLayouts)
        |> entryInstructions
        |> containsAllocAdt(1)(1)
        |> test.assertEqual(true))
    |> (given (_) ->
        callArgumentsInline
        |> ExprCall(
            ExprVar("UserId"),
            ExprInt(7),
            false
        )
        |> loweredProgramWithLayouts(structuralLayouts)
        |> entryInstructions
        |> containsAllocAdt(0)(1)
        |> test.assertEqual(false))
    |> (given (_) ->
        []
        |> ExprLet(
            "partial",
            ExprCall(
                ExprVar("Pair"),
                ExprInt(1),
                false,
                callArgumentsInline
            ),
            ExprCall(
                ExprVar("partial"),
                ExprInt(2),
                false,
                callArgumentsInline
            ),
            [],
            None
        )
        |> loweredProgramWithLayouts(structuralLayouts)
        |> programContainsAllocAdt(0)(2)
        |> test.assertEqual(true))

let listPatternExpression =
    ExprMatch(
        ExprList([ExprInt(7)])(false),
        [
            (PatternCons(PatternVar("head"))(PatternVar("tail")), ExprVar("head"), None),
            (PatternEmptyList, ExprInt(0), None)
        ],
        None
    )

let tuplePatternExpression =
    ExprMatch(
        ExprTuple([ExprInt(1), ExprInt(2)]),
        [
            (PatternTuple([PatternVar("first"), PatternWildcard]), ExprVar("first"), None)
        ],
        None
    )

let expectListAndTuplePatterns unit =
    unit
    |> (given (_) ->
        listPatternExpression
        |> loweredProgram
        |> entryInstructions
        |> (given (instructions) ->
            Unit
            |> (given (_) ->
                instructions
                |> containsLoadOffset(0)
                |> test.assertEqual(true))
            |> (given (_) ->
                instructions
                |> containsLoadOffset(8)
                |> test.assertEqual(true))))
    |> (given (_) ->
        tuplePatternExpression
        |> loweredProgram
        |> entryInstructions
        |> containsLoadOffset(8)
        |> test.assertEqual(true))

let adtPatternExpression =
    ExprMatch(
        ExprCall(
            ExprVar("Some"),
            ExprInt(7),
            false,
            callArgumentsInline
        ),
        [
            (PatternConstructor("Some")([PatternVar("value")]), ExprVar("value"), None),
            (PatternConstructor("None")([]), ExprInt(0), None)
        ],
        None
    )

let expectAdtAndZeroCostPatterns unit =
    unit
    |> (given (_) ->
        adtPatternExpression
        |> loweredProgramWithLayouts(structuralLayouts)
        |> entryInstructions
        |> (given (instructions) ->
            Unit
            |> (given (_) ->
                instructions
                |> containsGetAdtTag
                |> test.assertEqual(true))
            |> (given (_) ->
                instructions
                |> containsGetAdtField(0)
                |> test.assertEqual(true))))
    |> (given (_) ->
        None
        |> ExprMatch(
            ExprCall(
                ExprVar("UserId"),
                ExprInt(8),
                false,
                callArgumentsInline
            ),
            [
                (PatternConstructor("UserId")([PatternVar("value")]), ExprVar("value"), None)
            ]
        )
        |> loweredProgramWithLayouts(structuralLayouts)
        |> entryInstructions
        |> containsGetAdtTag
        |> test.assertEqual(false))

let pointExpression =
    ExprRecord(
        "Point",
        [
            ("y", ExprInt(2)),
            ("x", ExprInt(1))
        ],
        true
    )

let recordUpdateAndAccessExpression =
    ExprLet(
        "point",
        pointExpression,
        ExprLet(
            "updated",
            ExprRecordUpdate(ExprVar("point"))([("y", ExprInt(9))]),
            ExprQualifiedVar("updated")("x"),
            [],
            None,
            []
        ),
        [],
        None,
        []
    )

let expectRecordConstructionUpdateAndAccess unit =
    ((given (_) ->
        recordUpdateAndAccessExpression
        |> loweredProgramWithLayouts(structuralLayouts)
        |> entryInstructions
        |> (given (instructions) ->
            Unit
            |> (given (_) ->
                instructions
                |> containsAllocAdt(0)(2)
                |> test.assertEqual(true))
            |> (given (_) ->
                instructions
                |> containsGetAdtField(0)
                |> test.assertEqual(true)))))(unit)

let expectRecordAsAndOrPatterns unit =
    unit
    |> (given (_) ->
        None
        |> ExprMatch(
            pointExpression,
            [
                (PatternRecord("Point")([("y", PatternVar("value"))]), ExprVar("value"), None)
            ]
        )
        |> loweredProgramWithLayouts(structuralLayouts)
        |> entryInstructions
        |> containsGetAdtField(1)
        |> test.assertEqual(true))
    |> (given (_) ->
        None
        |> ExprMatch(
            ExprInt(2),
            [
                (PatternOr([
                    PatternAs(PatternInt(1))("value"),
                    PatternAs(PatternInt(2))("value")
                ]), ExprVar("value"), None),
                (PatternWildcard, ExprInt(0), None)
            ]
        )
        |> loweredProgram
        |> entryLocalCount
        // The result slot and the `value` binding, plus each of the two arms' own arena bracket:
        // two watermark slots at the save, and a `preRestoreEnd` slot at each of its two closes.
        |> test.assertEqual(10))

type CoreOperationClass =
    | AddIntClass
    | SubIntClass
    | MulIntClass
    | DivIntClass
    | DivUIntClass
    | AndIntClass
    | OrIntClass
    | XorIntClass
    | ShlIntClass
    | ShrIntClass
    | AddFloatClass
    | SubFloatClass
    | MulFloatClass
    | DivFloatClass
    | CmpIntGtClass
    | CmpIntGeClass
    | CmpIntLtClass
    | CmpIntLeClass
    | CmpIntEqClass
    | CmpUIntGtClass
    | CmpFloatEqClass
    | CmpStrNeClass
    | BigIntBinaryClass(Str)
    | ConcatStrClass
    | LoadProgramArgsClass
    | WriteStrClass
    deriving {Eq, Show}

let classifyCoreOperation operation =
    match operation with
        | AddInt(_, _, _) -> Some(AddIntClass)
        | SubInt(_, _, _) -> Some(SubIntClass)
        | MulInt(_, _, _) -> Some(MulIntClass)
        | DivInt(_, _, _) -> Some(DivIntClass)
        | DivUInt(_, _, _) -> Some(DivUIntClass)
        | AndInt(_, _, _) -> Some(AndIntClass)
        | OrInt(_, _, _) -> Some(OrIntClass)
        | XorInt(_, _, _) -> Some(XorIntClass)
        | ShlInt(_, _, _) -> Some(ShlIntClass)
        | ShrInt(_, _, _) -> Some(ShrIntClass)
        | AddFloat(_, _, _) -> Some(AddFloatClass)
        | SubFloat(_, _, _) -> Some(SubFloatClass)
        | MulFloat(_, _, _) -> Some(MulFloatClass)
        | DivFloat(_, _, _) -> Some(DivFloatClass)
        | CmpIntGt(_, _, _) -> Some(CmpIntGtClass)
        | CmpIntGe(_, _, _) -> Some(CmpIntGeClass)
        | CmpIntLt(_, _, _) -> Some(CmpIntLtClass)
        | CmpIntLe(_, _, _) -> Some(CmpIntLeClass)
        | CmpIntEq(_, _, _) -> Some(CmpIntEqClass)
        | CmpUIntGt(_, _, _) -> Some(CmpUIntGtClass)
        | CmpFloatEq(_, _, _) -> Some(CmpFloatEqClass)
        | CmpStrNe(_, _, _) -> Some(CmpStrNeClass)
        | BigIntBinary(_, _, _, name, _) -> Some(BigIntBinaryClass(name))
        | ConcatStr(_, _, _, _) -> Some(ConcatStrClass)
        | LoadProgramArgs(_) -> Some(LoadProgramArgsClass)
        | WriteStr(_) -> Some(WriteStrClass)
        | _ -> None

let recursive containsCoreOperation expected instructions =
    match instructions with
        | [] -> false
        | IrInstruction { instruction = operation } :: rest ->
            match classifyCoreOperation(operation) with
                | Some(actual) ->
                    if actual == expected
                    then true
                    else containsCoreOperation(expected)(rest)
                | None -> containsCoreOperation(expected)(rest)

let operatorCases =
    [
        (ExprAdd(ExprInt(1))(ExprInt(2)), AddIntClass),
        (ExprSubtract(ExprInt(3))(ExprInt(2)), SubIntClass),
        ("2.0"
        |> ExprFloat(2.0)
        |> ExprSubtract(ExprInt(0)), SubFloatClass),
        ("3.0"
        |> ExprFloat(3.0)
        |> ExprMultiply(ExprFloat(2.0)("2.0")), MulFloatClass),
        (ExprDivide(ExprInt(6))(ExprInt(2)), DivIntClass),
        (ExprModulo(ExprInt(7))(ExprInt(3)), SubIntClass),
        (ExprBitwiseAnd(ExprInt(7))(ExprInt(3)), AndIntClass),
        (ExprBitwiseOr(ExprInt(4))(ExprInt(1)), OrIntClass),
        (ExprBitwiseXor(ExprInt(7))(ExprInt(2)), XorIntClass),
        (ExprShiftLeft(ExprInt(1))(ExprInt(3)), ShlIntClass),
        ("3u8"
        |> ExprUInt(3)(8)
        |> ExprShiftLeft(ExprUInt(1)(8)("1u8")), ShlIntClass),
        ("3u8"
        |> ExprUInt(3)(8)
        |> ExprShiftLeft(ExprUInt(1)(8)("1u8")), ShlIntClass),
        (ExprShiftRight(ExprInt(8))(ExprInt(2)), ShrIntClass),
        (ExprBitwiseNot(ExprInt(1)), XorIntClass),
        (ExprLogicalNot(ExprBool(true)), CmpIntEqClass),
        ("1u8"
        |> ExprUInt(1)(8)
        |> ExprGreaterThan(ExprUInt(2)(8)("2u8")), CmpUIntGtClass),
        (ExprGreaterOrEqual(ExprInt(2))(ExprInt(1)), CmpIntGeClass),
        (ExprLessThan(ExprInt(1))(ExprInt(2)), CmpIntLtClass),
        (ExprLessOrEqual(ExprInt(1))(ExprInt(2)), CmpIntLeClass),
        ("1.0"
        |> ExprFloat(1.0)
        |> ExprEqual(ExprFloat(1.0)("1.0")), CmpFloatEqClass),
        (ExprNotEqual(ExprString("a"))(ExprString("b")), CmpStrNeClass),
        (ExprAdd(ExprBigInt("9223372036854775808"))(ExprBigInt("1")), BigIntBinaryClass("add")),
        (ExprAdd(ExprString("a"))(ExprString("b")), ConcatStrClass)
    ]

let expectOperatorCase case unit =
    match case with
        | (expression, expected) ->
            expression
            |> loweredProgram
            |> entryInstructions
            |> containsCoreOperation(expected)
            |> test.assertEqual(true)

let recursive expectOperatorCases cases unit =
    match cases with
        | [] -> unit
        | head :: tail ->
            unit
            |> expectOperatorCase(head)
            |> expectOperatorCases(tail)

let unitType = SemNamed(60)("Unit")([])

let builtinConstructorLayouts =
    [
        CoreConstructorLayout(
            name = "Unit",
            tag = 0,
            scheme = TypeScheme(quantified = [], body = unitType, constraints = []),
            fieldNames = [],
            isZeroCost = false,
            tagless = false
        )
    ]

let builtinLayouts =
    [
        CoreBuiltinLayout(
            moduleName = "Ashes.IO",
            memberName = "args",
            scheme = TypeScheme(quantified = [], body = SemList(SemString), constraints = []),
            kind = CoreProgramArgs
        ),
        CoreBuiltinLayout(
            moduleName = "Ashes.IO",
            memberName = "write",
            scheme = TypeScheme(
                quantified = [],
                body = SemFunction(SemString)(unitType)(None),
                constraints = []
            ),
            kind = CoreWrite
        )
    ]

let loweredProgramWithContext expression =
    match lowerCoreExpressionWithContext(builtinConstructorLayouts)(builtinLayouts)(expression) with
        | CoreLoweringResult { program = Some(program), error = None } -> program
        | CoreLoweringResult { error = Some(error) } ->
            error
            |> Ashes.Trait.Show.show
            |> (given (text) -> test.fail("core builtin integration failed: " + text))
        | _ -> test.fail("core builtin integration produced no program")

let programUsesConcat program =
    match program with
        | IrProgram { usesConcatStr = value } -> value

let expectBuiltinIntegration unit =
    unit
    |> (given (_) ->
        "args"
        |> ExprQualifiedVar("Ashes.IO")
        |> loweredProgramWithContext
        |> entryInstructions
        |> containsCoreOperation(LoadProgramArgsClass)
        |> test.assertEqual(true))
    |> (given (_) ->
        callArgumentsInline
        |> ExprCall(
            ExprQualifiedVar("Ashes.IO")("write"),
            ExprString("hello"),
            false
        )
        |> loweredProgramWithContext
        |> entryInstructions
        |> containsCoreOperation(WriteStrClass)
        |> test.assertEqual(true))
    |> (given (_) ->
        ExprString("b")
        |> ExprAdd(ExprString("a"))
        |> loweredProgram
        |> programUsesConcat
        |> test.assertEqual(true))

let pruneInstruction kind = IrInstruction(instruction = kind, location = None)

let recursive loadEnvIndices instructions =
    match instructions with
        | [] -> []
        | IrInstruction { instruction = LoadEnv(_, index) } :: rest -> index :: loadEnvIndices(rest)
        | _ :: rest -> loadEnvIndices(rest)

let expectDeadCapturesPrunedAndRenumbered unit =
    match pruneDeadCaptures(["a", "b", "c"])([2
    |> LoadEnv(0)
    |> pruneInstruction, 0
    |> LoadEnv(1)
    |> pruneInstruction, pruneInstruction(Return(1))]) with
        | (survivors, instructions) ->
            unit
            |> (given (_) -> test.assertEqual(["a", "c"])(survivors))
            |> (given (_) ->
                instructions
                |> loadEnvIndices
                |> test.assertEqual([1, 0]))

let expectAllLiveCapturesUntouched unit =
    match pruneDeadCaptures(["a", "b"])([1
    |> LoadEnv(0)
    |> pruneInstruction, 0
    |> LoadEnv(1)
    |> pruneInstruction, pruneInstruction(Return(0))]) with
        | (survivors, instructions) ->
            unit
            |> (given (_) -> test.assertEqual(["a", "b"])(survivors))
            |> (given (_) ->
                instructions
                |> loadEnvIndices
                |> test.assertEqual([1, 0]))

let expectRepeatedReadsCountOnce unit =
    match pruneDeadCaptures(["a", "b"])([1
    |> LoadEnv(0)
    |> pruneInstruction, 1
    |> LoadEnv(1)
    |> pruneInstruction, pruneInstruction(Return(0))]) with
        | (survivors, instructions) ->
            unit
            |> (given (_) -> test.assertEqual(["b"])(survivors))
            |> (given (_) ->
                instructions
                |> loadEnvIndices
                |> test.assertEqual([0, 0]))

let expectUnreadEnvironmentEmptied unit =
    match pruneDeadCaptures(["a"])([1
    |> LoadLocal(0)
    |> pruneInstruction, pruneInstruction(Return(0))]) with
        | (survivors, instructions) ->
            unit
            |> (given (_) -> test.assertEqual([])(survivors))
            |> (given (_) ->
                instructions
                |> loadEnvIndices
                |> test.assertEqual([]))

let recursive countGetAdtTag instructions =
    match instructions with
        | [] -> 0
        | IrInstruction { instruction = GetAdtTag(_, _) } :: rest -> 1 + countGetAdtTag(rest)
        | _ :: rest -> countGetAdtTag(rest)

let recursive switchCaseCount instructions =
    match instructions with
        | [] -> None
        | IrInstruction { instruction = SwitchTag(_, cases, _) } :: _ ->
            cases
            |> Ashes.Collection.List.length
            |> Some
        | _ :: rest -> switchCaseCount(rest)

let recursive countLoadConstInt instructions (value: Int) =
    match instructions with
        | [] -> 0
        | IrInstruction { instruction = LoadConstInt(_, candidate) } :: rest ->
            if candidate == value
            then 1 + countLoadConstInt(rest)(value)
            else countLoadConstInt(rest)(value)
        | _ :: rest -> countLoadConstInt(rest)(value)

let shadeArm (name: Str) (value: Int) = (PatternConstructor(name)([]), ExprInt(value), None)

let fiveShadeMatch =
    ExprMatch(
        ExprVar("Red"),
        [shadeArm("Red")(10), shadeArm("Green")(20), shadeArm("Blue")(30), shadeArm("Cyan")(40), shadeArm("Magenta")(50)],
        None
    )

let expectFlatMatchAboveThresholdUsesOneTagSwitch unit =
    (let instructions =
        fiveShadeMatch
        |> loweredProgramWithLayouts(structuralLayouts)
        |> entryInstructions
    in
        unit
        |> (given (_) ->
            instructions
            |> switchCaseCount
            |> test.assertEqual(Some(5)))
        |> (given (_) ->
            instructions
            |> countGetAdtTag
            |> test.assertEqual(1)))

let expectSmallFlatMatchStaysLinear unit =
    (let instructions =
        adtPatternExpression
        |> loweredProgramWithLayouts(structuralLayouts)
        |> entryInstructions
    in
        unit
        |> (given (_) ->
            instructions
            |> switchCaseCount
            |> test.assertEqual(None))
        |> (given (_) ->
            instructions
            |> countGetAdtTag
            |> test.assertEqual(2)))

let repeatedTagMatch =
    ExprMatch(
        ExprCall(
            ExprVar("Some"),
            ExprInt(7),
            false,
            callArgumentsInline
        ),
        [
            (PatternConstructor("Some")([PatternInt(1)]), ExprInt(100), None),
            (PatternConstructor("Some")([PatternVar("value")]), ExprVar("value"), None),
            (PatternConstructor("None")([]), ExprInt(0), None)
        ],
        None
    )

let expectRepeatedTagSharesOneTagTest unit =
    (let instructions =
        repeatedTagMatch
        |> loweredProgramWithLayouts(structuralLayouts)
        |> entryInstructions
    in
        unit
        |> (given (_) ->
            instructions
            |> switchCaseCount
            |> test.assertEqual(Some(2)))
        |> (given (_) ->
            instructions
            |> countGetAdtTag
            |> test.assertEqual(3))
        |> (given (_) ->
            100
            |> countLoadConstInt(instructions)
            |> test.assertEqual(1)))

let trailingDefaultMatch =
    ExprMatch(
        ExprCall(
            ExprVar("Some"),
            ExprInt(7),
            false,
            callArgumentsInline
        ),
        [
            (PatternConstructor("Some")([PatternInt(1)]), ExprInt(100), None),
            (PatternWildcard, ExprInt(0), None)
        ],
        None
    )

let expectFailedNestedCaseFallsThroughToDefaultArm unit =
    (let instructions =
        trailingDefaultMatch
        |> loweredProgramWithLayouts(structuralLayouts)
        |> entryInstructions
    in
        unit
        |> (given (_) ->
            instructions
            |> switchCaseCount
            |> test.assertEqual(Some(1)))
        |> (given (_) ->
            100
            |> countLoadConstInt(instructions)
            |> test.assertEqual(1)))

let guardedMatch =
    ExprMatch(
        ExprVar("Red"),
        [
            (PatternConstructor("Red")([]), ExprInt(10), Some(ExprBool(true))),
            shadeArm("Red")(15),
            shadeArm("Green")(20),
            shadeArm("Blue")(30),
            shadeArm("Cyan")(40),
            shadeArm("Magenta")(50)
        ],
        None
    )

let expectGuardedMatchStaysLinear unit =
    (let instructions =
        guardedMatch
        |> loweredProgramWithLayouts(structuralLayouts)
        |> entryInstructions
    in
        instructions
        |> switchCaseCount
        |> test.assertEqual(None))

let bareNullaryPatternMatch =
    ExprMatch(
        ExprCall(
            ExprVar("Some"),
            ExprInt(7),
            false,
            callArgumentsInline
        ),
        [
            (PatternVar("None"), ExprInt(0), None),
            (PatternConstructor("Some")([PatternVar("value")]), ExprVar("value"), None)
        ],
        None
    )

let expectBareNullaryConstructorPatternTestsItsTag unit =
    (let instructions =
        bareNullaryPatternMatch
        |> loweredProgramWithLayouts(structuralLayouts)
        |> entryInstructions
    in
        instructions
        |> countGetAdtTag
        |> test.assertEqual(2))

let someSeven =
    ExprCall(
        ExprVar("Some"),
        ExprInt(7),
        false,
        callArgumentsInline
    )

let trimmedArmsInstructions arms =
    None
    |> ExprMatch(someSeven)(arms)
    |> loweredProgramWithLayouts(structuralLayouts)
    |> entryInstructions

let expectDeadWildcardArmAfterCompleteConstructorsIsTrimmed unit =
    (let instructions =
        trimmedArmsInstructions(
            [
                (PatternConstructor("Some")([PatternVar("value")]), ExprVar("value"), None),
                (PatternVar("None"), ExprInt(0), None),
                (PatternWildcard, ExprInt(99), None)
            ]
        )
    in
        99
        |> countLoadConstInt(instructions)
        |> test.assertEqual(0))

let expectWildcardAfterNestedLiteralArmIsKept unit =
    (let instructions =
        trimmedArmsInstructions(
            [
                (PatternConstructor("Some")([PatternInt(1)]), ExprInt(1), None),
                (PatternVar("None"), ExprInt(0), None),
                (PatternWildcard, ExprInt(99), None)
            ]
        )
    in
        99
        |> countLoadConstInt(instructions)
        |> test.assertEqual(1))

let expectWildcardAfterIncompleteConstructorsIsKept unit =
    (let instructions =
        trimmedArmsInstructions(
            [
                (PatternConstructor("Some")([PatternWildcard]), ExprInt(1), None),
                (PatternWildcard, ExprInt(99), None)
            ]
        )
    in
        99
        |> countLoadConstInt(instructions)
        |> test.assertEqual(1))

let expectWildcardAfterBothBoolLiteralsIsTrimmed unit =
    (let instructions =
        None
        |> ExprMatch(ExprBool(true))([(PatternBool(true), ExprInt(1), None), (PatternBool(false), ExprInt(0), None), (PatternWildcard, ExprInt(99), None)])
        |> loweredProgramWithLayouts(structuralLayouts)
        |> entryInstructions
    in
        99
        |> countLoadConstInt(instructions)
        |> test.assertEqual(0))

let expectWildcardAfterTupleOfLiteralsIsKept unit =
    (let instructions =
        None
        |> ExprMatch(
            ExprTuple([ExprBool(true), ExprBool(false)]),
            [
                (PatternTuple([PatternBool(true), PatternBool(false)]), ExprInt(1), None),
                (PatternTuple([PatternBool(false), PatternBool(true)]), ExprInt(2), None),
                (PatternWildcard, ExprInt(99), None)
            ]
        )
        |> loweredProgramWithLayouts(structuralLayouts)
        |> entryInstructions
    in
        99
        |> countLoadConstInt(instructions)
        |> test.assertEqual(1))

let expectWildcardAfterEmptyAndConsIsTrimmed unit =
    (let instructions =
        None
        |> ExprMatch(
            ExprList([ExprInt(1)])(false),
            [(PatternEmptyList, ExprInt(0), None), (PatternCons(PatternWildcard)(PatternWildcard), ExprInt(1), None), (PatternWildcard, ExprInt(99), None)]
        )
        |> loweredProgramWithLayouts(structuralLayouts)
        |> entryInstructions
    in
        99
        |> countLoadConstInt(instructions)
        |> test.assertEqual(0))

let runCoreLoweringTests unit =
    unit
    |> expectConstantAndLocal
    |> expectScalarConstantTypes
    |> expectCaptureAndLiftedFunction
    |> expectStrictImmediateCall
    |> expectPartialApplicationOrder
    |> expectStringInterning
    |> expectConditionalControlFlow
    |> expectGuardedMatchOrder
    |> expectRecursiveSelfReference
    |> expectMutualRecursiveGroup
    |> expectTupleListAndStringLayouts
    |> expectOrdinaryAndZeroCostConstructors
    |> expectListAndTuplePatterns
    |> expectAdtAndZeroCostPatterns
    |> expectRecordConstructionUpdateAndAccess
    |> expectRecordAsAndOrPatterns
    |> expectOperatorCases(operatorCases)
    |> expectBuiltinIntegration
    |> (given (_) -> expectDeadCapturesPrunedAndRenumbered(Unit))
    |> (given (_) -> expectAllLiveCapturesUntouched(Unit))
    |> (given (_) -> expectRepeatedReadsCountOnce(Unit))
    |> (given (_) -> expectUnreadEnvironmentEmptied(Unit))
    |> (given (_) -> expectFlatMatchAboveThresholdUsesOneTagSwitch(Unit))
    |> (given (_) -> expectSmallFlatMatchStaysLinear(Unit))
    |> (given (_) -> expectRepeatedTagSharesOneTagTest(Unit))
    |> (given (_) -> expectFailedNestedCaseFallsThroughToDefaultArm(Unit))
    |> (given (_) -> expectGuardedMatchStaysLinear(Unit))
    |> (given (_) -> expectBareNullaryConstructorPatternTestsItsTag(Unit))
    |> (given (_) -> expectDeadWildcardArmAfterCompleteConstructorsIsTrimmed(Unit))
    |> (given (_) -> expectWildcardAfterNestedLiteralArmIsKept(Unit))
    |> (given (_) -> expectWildcardAfterIncompleteConstructorsIsKept(Unit))
    |> (given (_) -> expectWildcardAfterBothBoolLiteralsIsTrimmed(Unit))
    |> (given (_) -> expectWildcardAfterTupleOfLiteralsIsKept(Unit))
    |> (given (_) -> expectWildcardAfterEmptyAndConsIsTrimmed(Unit))
    |> (given (_) -> Ashes.IO.print("all self-hosted core lowering tests passed"))
