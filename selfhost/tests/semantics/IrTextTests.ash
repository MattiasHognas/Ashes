import Ashes.Test as test
import AshesCompiler.Semantics.Ir
import AshesCompiler.Semantics.IrInstructions
import AshesCompiler.Semantics.IrOrigins
import AshesCompiler.Semantics.IrText
export (
    value runIrTextTests,
)

let sourceOrigin =
    SourceFunctionOrigin(
        functionSourceName = "build",
        functionQualifiedName = Some("App.Work.build"),
        declarationLocation = None,
        declarationOffset = 100
    )

let generatedOrigin =
    IrFunctionOrigin(
        generatedLabel = "reuse_build_1",
        originKind = ReuseSpecializationOrigin,
        sourceOrigin = Some(sourceOrigin),
        parentGeneratedLabel = Some("build_lambda_0"),
        compilerOwner = None,
        stableDiscriminator = Some("reuse:1"),
        generationLocation = None
    )

let entryOrigin =
    IrFunctionOrigin(
        generatedLabel = "main",
        originKind = ProgramEntryOrigin,
        sourceOrigin = None,
        parentGeneratedLabel = None,
        compilerOwner = None,
        stableDiscriminator = None,
        generationLocation = None
    )

let instruction kind location =
    IrInstruction(
        instruction = kind,
        location = location
    )

let function label instructions localCount tempCount origin =
    IrFunction(
        label = label,
        instructions = instructions,
        localCount = localCount,
        tempCount = tempCount,
        hasEnvAndArgParams = false,
        coroutine = None,
        localNames = [],
        localTypes = [],
        origin = origin,
        lifetimesPlaced = false
    )

let helperFunction =
    function("reuse_build_1")(
        [
            instruction(Label("loop"))(None),
            instruction(
                LoadConstInt(0)(42)
            )(
                Some(IrSourceLocation(filePath = "app.ash", line = 3, column = 5))
            ),
            instruction(SaveArenaState(0)(1)(true))(None),
            instruction(TcoResetPending(1)([0, 1])([]))(None),
            instruction(LoadConstBool(0)(false))(None)
        ]
    )(
        2
    )(
        3
    )(
        Some(generatedOrigin)
    )

let entryFunction =
    function("main")(
        [instruction(Return(0))(None)]
    )(
        0
    )(
        1
    )(
        Some(entryOrigin)
    )

let traitEvidence =
    TraitEvidenceAnnotations(
        dictionaryParameters = [
            TraitDictionaryAbiAnnotation(
                functionName = "build",
                functionSource = "app.ash",
                functionOffset = 100,
                parameterIndex = 2,
                traitName = "Hash",
                methods = ["hash", "salt"],
                supertraits = ["Eq"]
            )
        ],
        resolvedImplementations = [
            TraitResolutionAnnotation(
                requirement = "Hash(Int)",
                implementationModule = "Ashes.Int",
                implementationSource = "Int.ash",
                implementationOffset = 20
            )
        ]
    )

let program =
    IrProgram(
        entryFunction = entryFunction,
        functions = [helperFunction],
        stringLiterals = [],
        externalFunctions = [],
        externalOpaqueTypes = [],
        usesPrintInt = false,
        usesPrintStr = false,
        usesPrintBool = false,
        usesConcatStr = false,
        usesClosures = false,
        usesAsync = false,
        capabilityHandlerGlobals = 0,
        traitEvidence = traitEvidence
    )

let dictionaryEvidenceLinePrefix = "  dictionary-parameter function=build source=app.ash:100 index=2 trait=Hash "

let dictionaryEvidenceLine = dictionaryEvidenceLinePrefix + "methods=[hash,salt] supertraits=[Eq]"

let expectInstructionText unit =
    unit
    |> (given (_) ->
        Some(IrSourceLocation(filePath = "app.ash", line = 3, column = 5))
        |> instruction(
            LoadConstInt(0)(42)
        )
        |> formatIrInstruction
        |> test.assertEqual("    LoadConstInt          Target=0 Value=42   (app.ash:3:5)"))
    |> (given (_) ->
        None
        |> instruction(LoadConstBool(0)(false))
        |> formatIrInstruction
        |> test.assertEqual("    LoadConstBool         Target=0"))
    |> (given (_) ->
        None
        |> instruction(SaveArenaState(0)(1)(true))
        |> formatIrInstruction
        |> test.assertEqual("    SaveArenaState        CursorLocalSlot=0 EndLocalSlot=1 CoroutineLoop=true"))
    |> (given (_) ->
        None
        |> instruction(Label("loop"))
        |> formatIrInstruction
        |> test.assertEqual("  loop:"))

let expectFullDump unit =
    ((given (_) ->
        None
        |> formatIr(program)(LoweredIr)
        |> test.assertEqual([
            "IR (lowered)",
            "============",
            "",
            "trait evidence",
            dictionaryEvidenceLine,
            "  resolved requirement=Hash(Int) implementation=Ashes.Int (Int.ash:20)",
            "",
            "function reuse_build_1  [ReuseSpecialization from build]",
            "  locals=2 temps=3",
            "  loop:",
            "    LoadConstInt          Target=0 Value=42   (app.ash:3:5)",
            "    SaveArenaState        CursorLocalSlot=0 EndLocalSlot=1 CoroutineLoop=true",
            "    TcoResetPending       Id=1 UsedTemps=[2] ReadLocalSlots=[0]",
            "    LoadConstBool         Target=0",
            "",
            "function main  [ProgramEntry]",
            "  locals=0 temps=1",
            "    Return                Source=0",
            ""
        ])))(unit)

let expectFilteredDump unit =
    unit
    |> (given (_) ->
        Some("APP.WORK")
        |> formatIr(program)(FinalIr)
        |> test.assertEqual([
            "IR (final)",
            "==========",
            "",
            "trait evidence",
            dictionaryEvidenceLine,
            "  resolved requirement=Hash(Int) implementation=Ashes.Int (Int.ash:20)",
            "",
            "function reuse_build_1  [ReuseSpecialization from build]",
            "  locals=2 temps=3",
            "  loop:",
            "    LoadConstInt          Target=0 Value=42   (app.ash:3:5)",
            "    SaveArenaState        CursorLocalSlot=0 EndLocalSlot=1 CoroutineLoop=true",
            "    TcoResetPending       Id=1 UsedTemps=[2] ReadLocalSlots=[0]",
            "    LoadConstBool         Target=0",
            ""
        ]))
    |> (given (_) ->
        Some("missing")
        |> formatIr(program)(FinalIr)
        |> test.assertEqual([
            "IR (final)",
            "==========",
            "",
            "trait evidence",
            dictionaryEvidenceLine,
            "  resolved requirement=Hash(Int) implementation=Ashes.Int (Int.ash:20)",
            "",
            "  (no functions matched)"
        ]))

let runIrTextTests unit =
    unit
    |> expectInstructionText
    |> expectFullDump
    |> expectFilteredDump
    |> (given (_) -> Ashes.IO.print("all self-hosted IR text tests passed"))
