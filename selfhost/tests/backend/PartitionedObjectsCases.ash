// Partitioned code generation on a hand-built program: four lifted functions split over three
// partitions so that every call between them crosses a partition (the entry in partition 0 calls
// partition 2, which calls partition 1, which calls back into partition 0), and a string literal
// partition 0 defines is printed from partition 1. The partitioned build must print what the
// single-module build prints, and two partitioned builds must produce the same executable.
import Ashes.Byte
import Ashes.Ffi
import Ashes.Collection.Map
import AshesCompiler.Semantics.Ir
import AshesCompiler.Semantics.IrInstructions
import AshesCompiler.Backend.Llvm
import AshesCompiler.Backend.IrCodegen
import AshesCompiler.Backend.ElfLinker
import AshesCompiler.Backend.ProgramPartitions
import AshesCompiler.Backend.PartitionedObjects
export (
    value runPartitionedObjectsCases,
    value assertPartitionedMatchesWhole,
    value partitionTargetMachine,
    value runExecutableLines,
)

let partitionTriple = "x86_64-unknown-linux-gnu"

let resolvePartitionTargetMachine unit =
    match getTargetFromTriple(partitionTriple) with
        | (_, None, _) -> Error("could not resolve a target for " + partitionTriple)
        | (_, Some(target), _) ->
            match hostCpuName(Unit) with
                | Error(message) -> Error(message)
                | Ok(cpu) ->
                    match hostCpuFeatures(Unit) with
                        | Error(message) -> Error(message)
                        | Ok(features) ->
                            codeModelDefault
                            |> createTargetMachine(target)(partitionTriple)(cpu)(features)(codeGenOptLevelDefault)(relocModeStatic)
                            |> Ok

// A fresh host target machine, as the compiler resolves one for every partition.
let partitionTargetMachine unit =
    (let _ = initializeX86Target(Unit)
    in resolvePartitionTargetMachine(Unit))

let partitionInstruction kind = IrInstruction(instruction = kind, location = None)

let partitionLifted label instructions tempCount =
    IrFunction(
        label = label,
        instructions = Ashes.Collection.List.map(partitionInstruction)(instructions),
        localCount = 2,
        tempCount = tempCount,
        hasEnvAndArgParams = true,
        coroutine = None,
        localNames = [],
        localTypes = [],
        origin = None,
        lifetimesPlaced = false
    )

let partitionEntryName = "selfhostBackendPartitioned"

// `chain(n) = addTen(greet(n))`, `greet` printing the shared literal, `addTen(n) = double(n + 10)`;
// the entry prints `chain(5)`. Balanced on instruction counts over three partitions the plan puts
// `double` in partition 0, `greet` and `addTen` in partition 1 and `chain` in partition 2.
let partitionedHandBuiltProgram unit =
    IrProgram(
        entryFunction = IrFunction(
            label = partitionEntryName,
            instructions = Ashes.Collection.List.map(partitionInstruction)([
                LoadConstInt(0)(0),
                LoadConstInt(1)(5),
                CallKnown(2)("part_chain")(0)(1)(-1)(false),
                PrintInt(2),
                LoadConstInt(3)(0),
                Return(3)
            ]),
            localCount = 0,
            tempCount = 4,
            hasEnvAndArgParams = false,
            coroutine = None,
            localNames = [],
            localTypes = [],
            origin = None,
            lifetimesPlaced = false
        ),
        functions = [
            partitionLifted("part_double")([LoadLocal(0)(1), LoadConstInt(1)(2), MulInt(2)(0)(1), Return(2)])(3),
            partitionLifted("part_greet")([LoadConstStr(0)("lit0"), PrintStr(0), LoadLocal(1)(1), Return(1)])(2),
            partitionLifted("part_add_ten")([LoadLocal(0)(1), LoadConstInt(1)(10), AddInt(2)(0)(1), LoadConstInt(3)(0), CallKnown(4)("part_double")(3)(2)(-1)(false), Return(4)])(5),
            partitionLifted("part_chain")([LoadLocal(0)(1), LoadConstInt(1)(0), CallKnown(2)("part_greet")(1)(0)(-1)(false), CallKnown(3)("part_add_ten")(1)(2)(-1)(false), LoadLocal(4)(0), Return(3)])(5)
        ],
        stringLiterals = [IrStringLiteral(label = "lit0", value = "partitioned")],
        externalFunctions = [],
        externalOpaqueTypes = [],
        usesPrintInt = true,
        usesPrintStr = true,
        usesPrintBool = false,
        usesConcatStr = false,
        usesClosures = false,
        usesAsync = false,
        capabilityHandlerGlobals = 0,
        traitEvidence = emptyTraitEvidenceAnnotations
    )

let partitionFail (message: Str) = Ashes.IO.panic("partitioned objects: " + message)

// The single-module build of `program`: codegen, the same passes, one object.
let wholeProgramObject (program: IrProgram) =
    (let context = contextCreate(Unit)
    in
        match codegenProgram(partitionEntryName)(context)(program) with
            | (module_, builder) ->
                match partitionTargetMachine(Unit) with
                    | Error(message) -> partitionFail(message)
                    | Ok(machine) ->
                        let _ = setTarget(module_)(partitionTriple)
                        in
                            let _ = applyDataLayout(module_)(machine)
                            in
                                match optimizeAndEmitPartition(machine)(module_)(context)(builder)(partitionTriple)([]) with
                                    | (true, objectBytes) -> objectBytes
                                    | (false, errorText) ->
                                        errorText
                                        |> Ashes.Byte.length
                                        |> Ashes.Byte.subText(errorText)(0)
                                        |> partitionFail)

let partitionedObject (program: IrProgram) (partitions: Int) =
    match emitPartitionedObject(false)(partitionEntryName)(partitionTriple)(partitionTargetMachine)([])(partitions)(program) with
        | Error(message) -> partitionFail(message)
        | Ok(merged) -> merged

let linkedExecutable (objectBytes: Bytes) =
    match linkLinuxExecutable(objectBytes)(partitionEntryName) with
        | Error(message) -> partitionFail("link: " + message)
        | Ok(executableBytes) -> executableBytes

let recursive readLines process (acc: List(Str)) =
    match Ashes.IO.Process.readStdoutLine(process) with
        | None -> (Ashes.Collection.List.reverse(acc), Ashes.IO.Process.waitForExit(process))
        | Some(line) -> readLines(process)(line :: acc)

// Writes and runs an executable: its stdout lines and exit code.
let runExecutableLines (path: Str) (executableBytes: Bytes) =
    match Ashes.IO.File.writeBytes(path)(executableBytes) with
        | Error(message) -> partitionFail(message)
        | Ok(_) ->
            match Ashes.IO.File.makeExecutable(path) with
                | Error(message) -> partitionFail(message)
                | Ok(_) ->
                    match Ashes.IO.Process.spawn("./" + path)([]) with
                        | Error(message) -> partitionFail(message)
                        | Ok(process) -> readLines(process)([])

let expectPartition (label: Str) (expected: Int) (assignment: MapTree(Str, Int)) =
    if partitionOfLabel(assignment)(label) == expected
    then Unit
    else
        partitionFail(label + " is in partition " + Ashes.Text.fromInt(partitionOfLabel(assignment)(label)) + ", expected " + Ashes.Text.fromInt(expected))

// The program built as one module and as `partitions` partitions: both executables print
// `expected` (lines joined with `|`) with the same exit code, and a second partitioned build
// produces the same executable. The merged object is kept as `<path>.o`.
let assertPartitionedMatchesWhole (path: Str) (program: IrProgram) (partitions: Int) (expected: Str) =
    (let merged = partitionedObject(program)(partitions)
    in
        let _ = Ashes.IO.File.writeBytes(path + ".o")(merged)
        in
            match (program
            |> wholeProgramObject
            |> linkedExecutable
            |> runExecutableLines(path + "_whole"), merged
            |> linkedExecutable
            |> runExecutableLines(path)) with
                | ((wholeLines, wholeExit), (partitionedLines, partitionedExit)) ->
                    if Ashes.Text.join("|")(wholeLines) != expected
                    then partitionFail(path + ": the single-module build printed " + Ashes.Text.join("|")(wholeLines))
                    else
                        if Ashes.Text.join("|")(partitionedLines) != expected || partitionedExit != wholeExit
                        then partitionFail(path + ": the partitioned build printed " + Ashes.Text.join("|")(partitionedLines) + " (exit " + Ashes.Text.fromInt(partitionedExit) + ")")
                        else
                            if Ashes.Byte.compare(partitions
                            |> partitionedObject(program)
                            |> linkedExecutable)(linkedExecutable(merged)) != 0
                            then partitionFail(path + ": two partitioned builds produced different executables")
                            else Unit)

// Calls crossing every partition and a string literal partition 0 defines printed from partition 1;
// the runtime's console and region globals are partition 0's, used from the others.
let testPartitionedHandBuiltProgram unit =
    (let program = partitionedHandBuiltProgram(Unit)
    in
        let assignment = assignObjectPartitions(program)(3)
        in
            Unit
            |> (given (_) -> expectPartition("part_double")(0)(assignment))
            |> (given (_) -> expectPartition("part_greet")(1)(assignment))
            |> (given (_) -> expectPartition("part_add_ten")(1)(assignment))
            |> (given (_) -> expectPartition("part_chain")(2)(assignment))
            |> (given (_) -> assertPartitionedMatchesWhole("selfhost_backend_partitioned_e2e")(program)(3)("partitioned|30")))

let partitionEntry (instructions: List(IrInstructionKind)) (localCount: Int) (tempCount: Int) =
    IrFunction(
        label = partitionEntryName,
        instructions = Ashes.Collection.List.map(partitionInstruction)(instructions),
        localCount = localCount,
        tempCount = tempCount,
        hasEnvAndArgParams = false,
        coroutine = None,
        localNames = [],
        localTypes = [],
        origin = None,
        lifetimesPlaced = false
    )

let partitionProgram (entryFunction: IrFunction) (functions: List(IrFunction)) (stringLiterals: List(IrStringLiteral)) =
    IrProgram(
        entryFunction = entryFunction,
        functions = functions,
        stringLiterals = stringLiterals,
        externalFunctions = [],
        externalOpaqueTypes = [],
        usesPrintInt = true,
        usesPrintStr = true,
        usesPrintBool = true,
        usesConcatStr = false,
        usesClosures = true,
        usesAsync = false,
        capabilityHandlerGlobals = 0,
        traitEvidence = emptyTraitEvidenceAnnotations
    )

let addEnvironmentFunction (label: Str) = partitionLifted(label)([LoadEnv(0)(0), LoadLocal(1)(1), AddInt(2)(0)(1), Return(2)])(3)

// The backend suite's reference-counted closure copy (`buildCopyOutClosureRcNormalizerModule`)
// with its closures and the normalizer moved out of partition 0: the copy in the entry calls
// partition 0's `__ashes_closure_env_normalize`, which compares the code address of a closure
// partition 1 defines and calls a normalizer partition 2 defines. Prints `43`, `42`, `true`, `7`.
let closureNormalizerProgram unit =
    partitionProgram(partitionEntry([
        Alloc(0)(8)(true),
        LoadConstInt(1)(40),
        StoreMemOffset(0)(0)(1),
        MakeClosure(2)("copy_norm_env")(0)(8)(true)(false)(false)(false),
        CopyOutClosure(3)(2)(true)(RcNormalization),
        LoadConstInt(4)(100),
        StoreMemOffset(0)(0)(4),
        LoadConstInt(5)(2),
        CallClosure(6)(3)(5)(-1),
        PrintInt(6),
        Alloc(7)(8)(true),
        LoadConstInt(8)(40),
        StoreMemOffset(7)(0)(8),
        MakeClosure(9)("copy_raw_env")(7)(8)(true)(false)(false)(false),
        CopyOutClosure(10)(9)(true)(RcNormalization),
        StoreMemOffset(7)(0)(4),
        CallClosure(11)(10)(5)(-1),
        PrintInt(11),
        LoadMemOffset(12)(3)(8),
        RcIsUnique(13)(12),
        PrintBool(13),
        RcDrop(3)("Function")(0)(true)(false)(None),
        RcDrop(10)("Function")(0)(true)(false)(None),
        RcDrop(2)("Function")(0)(true)(false)(None),
        RcDrop(9)("Function")(0)(true)(false)(None),
        LoadConstInt(14)(7),
        PrintInt(14),
        Return(14)
    ])(0)(15))([
        addEnvironmentFunction("copy_norm_env"),
        partitionLifted("copy_norm_env$env_normalize")([LoadLocal(0)(0), LoadMemOffset(1)(0)(0), LoadConstInt(2)(1), AddInt(3)(1)(2), LoadLocal(4)(1), StoreMemOffset(4)(0)(3), LoadConstInt(5)(0), Return(5)])(6),
        addEnvironmentFunction("copy_raw_env")
    ])([])

let testPartitionedClosureNormalizer unit =
    (let program = closureNormalizerProgram(Unit)
    in
        let assignment = assignObjectPartitions(program)(3)
        in
            Unit
            |> (given (_) -> expectPartition("copy_norm_env")(1)(assignment))
            |> (given (_) -> expectPartition("copy_norm_env$env_normalize")(2)(assignment))
            |> (given (_) -> assertPartitionedMatchesWhole("selfhost_backend_partitioned_normalizer_e2e")(program)(3)("43|42|true|7")))

// `Ashes.IO.Environment.get("")` in two functions of two partitions: each body creates the runtime
// message constant `.str_msg_env_get_empty_msg`, which stays each partition's own. Prints the
// message twice and `2`.
let environmentGetFunction (label: Str) = partitionLifted(label)([LoadConstStr(0)("lit_empty"), EnvironmentGet(1)(0), GetAdtField(2)(1)(0)(false), PrintStr(2), GetAdtTag(3)(1), Return(3)])(4)

let messageConstantProgram unit =
    partitionProgram(partitionEntry([
        LoadConstInt(0)(0),
        CallKnown(1)("env_get_first")(0)(0)(-1)(false),
        CallKnown(2)("env_get_second")(0)(0)(-1)(false),
        AddInt(3)(1)(2),
        PrintInt(3),
        LoadConstInt(4)(0),
        Return(4)
    ])(0)(5))([environmentGetFunction("env_get_first"), environmentGetFunction("env_get_second")])([IrStringLiteral(label = "lit_empty", value = "")])

let testPartitionedMessageConstants unit =
    (let program = messageConstantProgram(Unit)
    in
        let assignment = assignObjectPartitions(program)(2)
        in
            Unit
            |> (given (_) -> expectPartition("env_get_first")(0)(assignment))
            |> (given (_) -> expectPartition("env_get_second")(1)(assignment))
            |> (given (_) ->
                match program
                |> wholeProgramObject
                |> linkedExecutable
                |> runExecutableLines("selfhost_backend_partition_messages_whole") with
                    | (lines, _) ->
                        match lines with
                            | first :: second :: total :: [] ->
                                if first == second && total == "2"
                                then
                                    lines
                                    |> Ashes.Text.join("|")
                                    |> assertPartitionedMatchesWhole("selfhost_backend_partition_messages_e2e")(program)(2)
                                else partitionFail("the single-module message program printed " + Ashes.Text.join("|")(lines))
                            | _ -> partitionFail("the single-module message program printed " + Ashes.Text.join("|")(lines))))

// The plan: one partition per 1024 functions (or the requested count), at least one, at most 16.
let expectPlan (label: Str) (expected: Int) (actual: Int) =
    if expected == actual
    then Unit
    else partitionFail("plan " + label + ": expected " + Ashes.Text.fromInt(expected) + " partitions, got " + Ashes.Text.fromInt(actual))

let testObjectPartitionPlan unit =
    (let program = partitionedHandBuiltProgram(Unit)
    in
        Unit
        |> (given (_) ->
            program
            |> requestedObjectPartitions(None)
            |> expectPlan("unset")(1))
        |> (given (_) ->
            program
            |> requestedObjectPartitions(Some("auto"))
            |> expectPlan("auto for four functions")(1))
        |> (given (_) ->
            program
            |> requestedObjectPartitions(Some("3"))
            |> expectPlan("three")(3))
        |> (given (_) ->
            program
            |> requestedObjectPartitions(Some("40"))
            |> expectPlan("clamped to 16")(16))
        |> (given (_) ->
            program
            |> requestedObjectPartitions(Some("many"))
            |> expectPlan("not a number")(1))
        |> (given (_) ->
            program
            |> sizedObjectPartitions(2)
            |> expectPlan("one per two functions")(2)))

let runPartitionedObjectsCases unit =
    unit
    |> testObjectPartitionPlan
    |> testPartitionedHandBuiltProgram
    |> testPartitionedClosureNormalizer
    |> testPartitionedMessageConstants
