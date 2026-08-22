import Ashes.Test as test
import AshesCompiler.Frontend.Syntax
import AshesCompiler.Semantics.CoreLowering
import AshesCompiler.Semantics.Ir
import AshesCompiler.Semantics.IrText
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
            "  locals=1 temps=2",
            "    LoadConstInt          Target=0 Value=7",
            "    StoreLocal            Slot=0 Source=0",
            "    LoadLocal             Target=1 Slot=0",
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
            "function _start_main  [ProgramEntry]",
            "  locals=1 temps=4",
            "    LoadConstInt          Target=0 Value=7",
            "    StoreLocal            Slot=0 Source=0",
            "    Alloc                 Target=1 SizeBytes=8",
            "    LoadLocal             Target=2 Slot=0",
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
            "  locals=0 temps=4",
            "    LoadConstInt          Target=0 Value=0",
            "    MakeClosureStack      Target=1 FuncLabel=lambda_0 EnvPtrTemp=0 EnvSizeBytes=0",
            "    LoadConstInt          Target=2 Value=41",
            "    CallClosure           Target=3 ClosureTemp=1 ArgTemp=2",
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
            "function lambda_1  [ClosureHelper]",
            "  locals=2 temps=1",
            "    LoadEnv               Target=0 Index=0",
            "    Return                Source=0",
            "",
            "function lambda_0  [ClosureHelper]",
            "  locals=2 temps=3",
            "    Alloc                 Target=0 SizeBytes=8",
            "    LoadLocal             Target=1 Slot=1",
            "    StoreMemOffset        BasePtr=0 OffsetBytes=0 Source=1",
            "    MakeClosure           Target=2 FuncLabel=lambda_1 EnvPtrTemp=0 EnvSizeBytes=8",
            "    Return                Source=2",
            "",
            "function _start_main  [ProgramEntry]",
            "  locals=1 temps=5",
            "    LoadConstInt          Target=0 Value=0",
            "    MakeClosure           Target=1 FuncLabel=lambda_0 EnvPtrTemp=0 EnvSizeBytes=0",
            "    StoreLocal            Slot=0 Source=1",
            "    LoadLocal             Target=2 Slot=0",
            "    LoadConstInt          Target=3 Value=1",
            "    CallClosure           Target=4 ClosureTemp=2 ArgTemp=3",
            "    Return                Source=4",
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

let runCoreLoweringTests unit =
    unit
    |> expectConstantAndLocal
    |> expectScalarConstantTypes
    |> expectCaptureAndLiftedFunction
    |> expectStrictImmediateCall
    |> expectPartialApplicationOrder
    |> expectStringInterning
    |> (given (_) -> Ashes.IO.print("all self-hosted core lowering tests passed"))
