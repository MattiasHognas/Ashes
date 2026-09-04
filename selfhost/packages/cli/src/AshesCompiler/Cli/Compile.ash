// The `ashes compile` and `ashes run` commands: compile one `.ash` file to a linux-x64 executable
// through the self-hosted pipeline, and optionally run it with forwarded arguments.
//
// Invariants:
// - The pipeline is the same one `selfhost/tests/backend` proves end to end: the entry source and
//   the shipped standard-library texts go through `stitchWithShippedModules`, the stitched program
//   through `lowerCoreProgramWithSource` and `optimizeIrProgram`, the lowered `IrProgram` through
//   `codegenProgram`, and the emitted object through `linkLinuxExecutable`. Nothing here parses,
//   infers, lowers, or generates code on its own.
// - Matches stage 0's observable contract (docs/md/reference/cli.md#ashes-compile) for the
//   single-file form: the positional input must end in `.ash`, `-o`/`--out` selects the output,
//   the default output drops the `.ash` suffix, success prints the `OK Wrote <size> to <output>`
//   confirmation with its `Target:` line on stdout, diagnostics and command errors go to stderr,
//   and the exit codes are 0/1/2 for success, compilation or input failure, and usage error.
//   `ashes run` compiles to the host temporary directory, forwards everything after `--` to the
//   program, and propagates the program's own exit code.
// - `--explain <kind>[:<selector>]` is accepted by both commands, repeats, and prints the requested
//   compiler reports to stderr between optimization and code generation
//   (docs/md/reference/cli.md#compiler-reports). Reporting reads the decision snapshot and the
//   optimized program and writes neither, so the emitted image is the same whether or not a
//   report was asked for. An unknown kind or a missing value is a usage error listing the valid
//   values.
// - Deliberately narrower than stage 0 for now: only the linux-x64 target and the file form
//   (`--expr`, `--project`, target/optimization/debug options, and IR dumps are not parsed), no
//   elapsed time in the confirmation (no monotonic clock capability is shipped yet), a program's
//   stdout and stderr are relayed line by line rather than inherited, and the shipped standard
//   library is located by probing `lib/Ashes` beside the executable, beside its parent directory,
//   and under the working directory, in that order. Any program shape the backend does not
//   support yet surfaces exactly as `AshesCompiler.Backend.IrCodegen` reports it.

import Ashes.Byte
import Ashes.Ffi
import Ashes.Collection.List.append
import Ashes.Collection.List.reverse as reverseList
import Ashes.IO.Path
import AshesCompiler.Backend.Llvm
import AshesCompiler.Backend.IrCodegen
import AshesCompiler.Backend.ElfLinker
import AshesCompiler.Frontend.Syntax
import AshesCompiler.Semantics.CoreLowering
import AshesCompiler.Semantics.DecisionSnapshot
import AshesCompiler.Semantics.ExplainReport
import AshesCompiler.Semantics.ExplainReportFormatter
import AshesCompiler.Semantics.Ir
import AshesCompiler.Semantics.IrExplainReporter
import AshesCompiler.Semantics.IrOptimizer
import AshesCompiler.Semantics.ModuleSemanticStitching
import AshesCompiler.Semantics.ProjectSyntaxStitching
import AshesCompiler.Semantics.ShippedModuleStitching
export (
    type CompileArguments(..),
    type CompileParse(..),
    type CompileOutcome(..),
    type RunArguments(..),
    type RunParse(..),
    value parseCompileArguments,
    value parseRunArguments,
    value defaultOutputPath,
    value formatByteSize,
    value inputStem,
    value explainValidValuesText,
    value compileFileToExecutable,
    value runCompileWithArguments,
    value runCompile,
    value runRun,
)

type CompileArguments =
    | inputPath: Str
    | outputPath: Maybe(Str)
    | explain: ExplainRequest

type CompileParse =
    | CompileHelpRequested
    | CompileInputError(Str)
    | CompileUsageError(Str)
    | CompileParsedArguments(CompileArguments)

type CompileOutcome =
    | CompileSucceeded(Int)
    | CompileFailed(Str)

type RunArguments =
    | runInputPath: Str
    | programArguments: List(Str)
    | runExplain: ExplainRequest

type RunParse =
    | RunHelpRequested
    | RunInputError(Str)
    | RunUsageError(Str)
    | RunParsedArguments(RunArguments)

let linuxX64Triple = "x86_64-unknown-linux-gnu"

let entrySymbolName = "ashes_entry"

let hasAshExtension path =
    Ashes.Text.asciiLower(Ashes.IO.Path.extension(Ashes.IO.Path.Unix)(path)) == ".ash"

let isOptionLike argument = Ashes.Text.length(argument) > 0 && Ashes.Text.substring(argument)(0)(1) == "-"

// The valid `--explain` values as stage 0 lists them under a usage error.
let explainValidValuesText = "\n\nValid values:\n  " + Ashes.Text.join("\n  ")(explainValidValues)

// One `--explain <kind>[:<selector>]` value added to the request; repeats deduplicate and a later
// selector replaces an earlier one.
let addExplainOption (value: Str) (explain: ExplainRequest) =
    match parseExplainValue(value) with
        | Ok((kind, selector)) ->
            explain
            |> addExplainKind(kind)(selector)
            |> Ok
        | Error(message) -> Error(message + explainValidValuesText)

let recursive partitionCompileFlags args output inputs explain =
    match args with
        | [] -> Ok((output, inputs, explain))
        | "-o" :: value :: rest -> partitionCompileFlags(rest)(Some(value))(inputs)(explain)
        | "--out" :: value :: rest -> partitionCompileFlags(rest)(Some(value))(inputs)(explain)
        | "-o" :: [] -> Error("Missing value for -o.")
        | "--out" :: [] -> Error("Missing value for --out.")
        | "--explain" :: value :: rest ->
            match addExplainOption(value)(explain) with
                | Error(message) -> Error(message)
                | Ok(added) -> partitionCompileFlags(rest)(output)(inputs)(added)
        | "--explain" :: [] -> Error("--explain requires a value." + explainValidValuesText)
        | other :: rest ->
            if isOptionLike(other)
            then Error("Unknown option '" + other + "'.")
            else
                partitionCompileFlags(rest)(output)(append(inputs)([other]))(explain)

let checkInputPath input =
    if hasAshExtension(input)
    then Ok(input)
    else Error("Input file must have a .ash extension: " + input)

// A bare `--help`/`-h` short-circuits; no arguments at all is a missing input (exit 1, like stage
// 0's own "no input" error); an unknown option, a bad `--explain` value, or more than one
// positional input is a usage error (exit 2); a positional input without the `.ash` extension is
// an input error (exit 1).
let parseCompileArguments args =
    match args with
        | "--help" :: [] -> CompileHelpRequested
        | "-h" :: [] -> CompileHelpRequested
        | [] -> CompileInputError("Missing input: provide a .ash file.")
        | _ ->
            match partitionCompileFlags(args)(None)([])(explainRequestNone) with
                | Error(message) -> CompileUsageError(message)
                | Ok((_, [], _)) -> CompileInputError("Missing input: provide a .ash file.")
                | Ok((output, input :: [], explain)) ->
                    match checkInputPath(input) with
                        | Error(message) -> CompileInputError(message)
                        | Ok(checked) -> CompileParsedArguments(CompileArguments(inputPath = checked, outputPath = output, explain = explain))
                | Ok((_, _, _)) -> CompileUsageError("Provide exactly one input file.")

let recursive splitProgramArguments args before =
    match args with
        | [] -> (reverseList(before), [])
        | "--" :: rest -> (reverseList(before), rest)
        | other :: rest -> splitProgramArguments(rest)(other :: before)

let recursive partitionRunFlags args inputs explain =
    match args with
        | [] -> Ok((inputs, explain))
        | "--explain" :: value :: rest ->
            match addExplainOption(value)(explain) with
                | Error(message) -> Error(message)
                | Ok(added) -> partitionRunFlags(rest)(inputs)(added)
        | "--explain" :: [] -> Error("--explain requires a value." + explainValidValuesText)
        | other :: rest ->
            if isOptionLike(other)
            then Error("Unknown option '" + other + "'.")
            else
                partitionRunFlags(rest)(append(inputs)([other]))(explain)

let parseRunArguments args =
    match args with
        | "--help" :: [] -> RunHelpRequested
        | "-h" :: [] -> RunHelpRequested
        | [] -> RunInputError("Missing input: provide a .ash file.")
        | _ ->
            match splitProgramArguments(args)([]) with
                | (before, programArguments) ->
                    match partitionRunFlags(before)([])(explainRequestNone) with
                        | Error(message) -> RunUsageError(message)
                        | Ok(([], _)) -> RunInputError("Missing input: provide a .ash file.")
                        | Ok((input :: [], explain)) ->
                            match checkInputPath(input) with
                                | Error(message) -> RunInputError(message)
                                | Ok(checked) -> RunParsedArguments(RunArguments(runInputPath = checked, programArguments = programArguments, runExplain = explain))
                        | Ok((_, _)) -> RunUsageError("Provide exactly one input file.")

// `examples/hello.ash` compiles to `examples/hello`: the `.ash` suffix is dropped in place.
let defaultOutputPath inputPath = Ashes.Text.substring(inputPath)(0)(Ashes.Text.length(inputPath) - 4)

// The input's basename without its `.ash` suffix: the entry module's name for stitching and the
// name of a `run` temporary executable.
let inputStem inputPath =
    (let base = Ashes.IO.Path.basename(Ashes.IO.Path.Unix)(inputPath)
    in Ashes.Text.substring(base)(0)(Ashes.Text.length(base) - 4))

let formatTenths tenths = Ashes.Text.fromInt(tenths / 10) + "." + Ashes.Text.fromInt(tenths % 10)

// Stage 0's human-readable size: whole bytes below 1 KiB, otherwise one decimal in KB or MB.
let formatByteSize bytes =
    if bytes < 1024
    then Ashes.Text.fromInt(bytes) + " B"
    else
        if bytes < 1048576
        then formatTenths((bytes * 10 + 512) / 1024) + " KB"
        else formatTenths((bytes * 10 + 524288) / 1048576) + " MB"

let isAshSourceName name = Ashes.Text.length(name) > 4 && Ashes.Text.substring(name)(Ashes.Text.length(name) - 4)(4) == ".ash"

// Every `<Module.Path>.ash` under the shipped standard-library root, as the in-memory texts
// `stitchWithShippedModules` resolves `import Ashes.*` against; only the modules a program
// reaches are ever parsed.
let recursive readShippedModules root names loaded =
    match names with
        | [] ->
            loaded
            |> reverseList
            |> Ok
        | name :: rest ->
            if isAshSourceName(name) == false
            then readShippedModules(root)(rest)(loaded)
            else
                let path = Ashes.IO.Path.join(Ashes.IO.Path.Unix)(root)(name)
                in
                    match Ashes.IO.File.readText(path) with
                        | Error(message) -> Error("Could not read shipped module " + path + ": " + message)
                        | Ok(source) ->
                            readShippedModules(root)(rest)(
                                ShippedModuleText(
                                    moduleName = "Ashes." + Ashes.Text.substring(name)(0)(Ashes.Text.length(name) - 4),
                                    sourcePath = path,
                                    source = source
                                ) :: loaded
                            )

let recursive firstReadableShippedRoot candidates =
    match candidates with
        | [] -> Error("Could not locate the shipped standard library: no lib/Ashes directory beside the executable, beside its parent directory, or under the working directory.")
        | candidate :: rest ->
            match Ashes.IO.Directory.entries(candidate) with
                | Ok(names) -> readShippedModules(candidate)(names)([])
                | Error(_) -> firstReadableShippedRoot(rest)

let shippedRootCandidates unit =
    match Ashes.IO.Environment.executableDirectory(Unit) with
        | Ok(directory) ->
            [
                Ashes.IO.Path.join(Ashes.IO.Path.Unix)(directory)("lib/Ashes"),
                Ashes.IO.Path.join(Ashes.IO.Path.Unix)(Ashes.IO.Path.parent(Ashes.IO.Path.Unix)(directory))("lib/Ashes"),
                "lib/Ashes"
            ]
        | Error(_) -> ["lib/Ashes"]

let loadShippedModules unit =
    Unit
    |> shippedRootCandidates
    |> firstReadableShippedRoot

// The lowered program, its optimized form (the one handed to code generation), and every value's
// placement fact lowering recorded on the way — the explain report's memory representation needs
// that last one, correlated to the un-optimized `lowered` it was captured against.
let lowerStitchedProgram inputPath source program =
    match lowerCoreProgramWithSource(inputPath)(source)(program) with
        | CoreLoweringResult { program = Some(lowered), error = None, valuePlacements = valuePlacements } -> Ok((lowered, optimizeIrProgram(lowered), valuePlacements))
        | CoreLoweringResult { error = Some(error) } ->
            error
            |> Ashes.Trait.Show.show
            |> Error
        | _ -> Error("Lowering produced no program.")

let lowerFileSource inputPath source shipped =
    match stitchWithShippedModules(inputStem(inputPath))(inputPath)(source)(shipped) with
        | Error(error) ->
            error
            |> Ashes.Trait.Show.show
            |> Error
        | Ok(StitchedSyntaxProject { program = program } as stitched) ->
            match lowerStitchedProgram(inputPath)(source)(program) with
                | Error(message) -> Error(message)
                | Ok((lowered, optimized, valuePlacements)) -> Ok((stitched, lowered, optimized, valuePlacements))

// The module-qualified name of a stitched binding for the reports. The single-file form is stage
// 0's standalone layout, whose entry module is named `Main`; every other definition reports under
// the qualified name stitching assigned it.
let recursive qualifiedNameIn (entryModule: Str) (placements: List(StitchedDefinitionPlacement)) (name: Str) =
    match placements with
        | [] -> None
        | StitchedDefinitionPlacement { definition = StitchedDefinition { compilerName = compilerName, sourceName = sourceName, qualifiedName = qualifiedName, moduleName = moduleName } } :: rest ->
            if compilerName == name
            then
                Some(
                    if moduleName == entryModule
                    then "Main." + sourceName
                    else qualifiedName
                )
            else qualifiedNameIn(entryModule)(rest)(name)

let stitchedQualifiedName (stitched: StitchedSyntaxProject) =
    match stitched with
        | StitchedSyntaxProject { definitionPlacements = placements, entryModuleName = entryModule } -> qualifiedNameIn(entryModule)(placements)

// The requested reports as text lines: the decision snapshot pairs the stitched program with its
// lowering and the value placements recorded against it, and the RC counts read the optimized
// program the backend is about to receive.
let explainReportLines (explain: ExplainRequest) (stitched: StitchedSyntaxProject) (lowered: IrProgram) valuePlacements (optimized: IrProgram) =
    match stitched with
        | StitchedSyntaxProject { program = program } ->
            valuePlacements
            |> captureDecisionSnapshot(stitchedQualifiedName(stitched))(program)(lowered)
            |> (given (snapshot) -> buildExplainReport(snapshot)(optimized)(explain))
            |> (given (report) -> formatExplainReport(report)(explain))

let recursive writeErrorLines (lines: List(Str)) =
    match lines with
        | [] -> Unit
        | line :: rest ->
            let _ = Ashes.IO.writeErrorLine(line)
            in writeErrorLines(rest)

// Prints the requested reports to stderr, so a program's own stdout stays usable when it is
// compiled and run in one step.
let writeExplainReport (explain: ExplainRequest) stitched lowered valuePlacements optimized =
    if isExplainRequestEmpty(explain)
    then Unit
    else
        optimized
        |> explainReportLines(explain)(stitched)(lowered)(valuePlacements)
        |> writeErrorLines

let disposeEmission buffer machine builder module_ context =
    Unit
    |> (given (_) -> disposeMemoryBuffer(buffer))
    |> (given (_) -> disposeTargetMachine(machine))
    |> (given (_) -> disposeBuilder(builder))
    |> (given (_) -> disposeModule(module_))
    |> (given (_) -> contextDispose(context))

let emitObjectWithMachine machine builder module_ context =
    (let _ = setTarget(module_)(linuxX64Triple)
    in
        let _ = applyDataLayout(module_)(machine)
        in
            match targetMachineEmitToMemoryBuffer(machine)(module_)(objectFileType) with
                | (true, _, _) -> Error("LLVM reported the module as broken during emission.")
                | (false, _, None) -> Error("LLVM produced no object buffer.")
                | (false, _, Some(buffer)) ->
                    let bytesResult =
                        buffer
                        |> getBufferSize
                        |> Ashes.Ffi.copyBytes(getBufferStart(buffer))
                    in
                        let _ = disposeEmission(buffer)(machine)(builder)(module_)(context)
                        in bytesResult)

// A target machine for `triple` tuned to the host CPU, as `selfhost/tests/backend` resolves it:
// no LLVM optimization and static relocations, the only configuration the linker accepts today.
let resolveHostTargetMachine triple =
    match getTargetFromTriple(triple) with
        | (_, None, _) -> Error("Could not resolve an LLVM target for " + triple + ".")
        | (_, Some(target), _) ->
            match hostCpuName(Unit) with
                | Error(message) -> Error(message)
                | Ok(cpu) ->
                    match hostCpuFeatures(Unit) with
                        | Error(message) -> Error(message)
                        | Ok(features) ->
                            codeModelDefault
                            |> createTargetMachine(target)(triple)(cpu)(features)(codeGenOptLevelNone)(relocModeStatic)
                            |> Ok

// The lowered program as a linux-x64 relocatable object, through `codegenProgram` and real LLVM.
let emitObject lowered =
    (let _ = initializeX86Target(Unit)
    in
        let context = contextCreate(Unit)
        in
            match codegenProgram(entrySymbolName)(context)(lowered) with
                | (module_, builder) ->
                    match resolveHostTargetMachine(linuxX64Triple) with
                        | Error(message) -> Error(message)
                        | Ok(machine) -> emitObjectWithMachine(machine)(builder)(module_)(context))

let linkExecutable objectBytes = linkLinuxExecutable(objectBytes)(entrySymbolName)

let writeExecutable outputPath executableBytes =
    match Ashes.IO.File.writeBytes(outputPath)(executableBytes) with
        | Error(message) -> Error("Could not write " + outputPath + ": " + message)
        | Ok(_) ->
            match Ashes.IO.File.makeExecutable(outputPath) with
                | Error(message) -> Error("Could not make " + outputPath + " executable: " + message)
                | Ok(_) ->
                    executableBytes
                    |> Ashes.Byte.length
                    |> Ok

let emitAndLink outputPath optimized =
    match emitObject(optimized) with
        | Error(message) -> Error(message)
        | Ok(objectBytes) ->
            match linkExecutable(objectBytes) with
                | Error(message) -> Error(message)
                | Ok(executableBytes) -> writeExecutable(outputPath)(executableBytes)

// Compiles `inputPath` to the executable at `outputPath`, printing the `explain` reports to stderr
// on the way, and returns the written byte count.
let compileFileToExecutable inputPath outputPath (explain: ExplainRequest) =
    match Ashes.IO.File.readText(inputPath) with
        | Error(message) -> Error("Could not read " + inputPath + ": " + message)
        | Ok(source) ->
            match loadShippedModules(Unit) with
                | Error(message) -> Error(message)
                | Ok(shipped) ->
                    match lowerFileSource(inputPath)(source)(shipped) with
                        | Error(message) -> Error(message)
                        | Ok((stitched, lowered, optimized, valuePlacements)) ->
                            optimized
                            |> writeExplainReport(explain)(stitched)(lowered)(valuePlacements)
                            |> (given (_) -> emitAndLink(outputPath)(optimized))

let runCompileWithArguments arguments =
    match arguments with
        | CompileArguments { inputPath = inputPath, outputPath = outputPath, explain = explain } ->
            let output =
                match outputPath with
                    | Some(explicit) -> explicit
                    | None -> defaultOutputPath(inputPath)
            in
                match compileFileToExecutable(inputPath)(output)(explain) with
                    | Error(message) -> CompileFailed(message)
                    | Ok(size) ->
                        Unit
                        |> (given (_) -> Ashes.IO.print("OK Wrote " + formatByteSize(size) + " to " + output))
                        |> (given (_) -> Ashes.IO.print("     Target: linux-x64"))
                        |> (given (_) -> CompileSucceeded(0))

// The full `ashes compile` entry point: parses `args`, prints the help, usage-error, input-error,
// and failure messages, and returns the process exit code.
let runCompile args =
    match parseCompileArguments(args) with
        | CompileHelpRequested ->
            let _ = Ashes.IO.writeLine("Usage: ashes compile [--explain <kind>] [-o <output>] <input.ash>")
            in 0
        | CompileInputError(message) ->
            let _ = Ashes.IO.writeErrorLine(message)
            in 1
        | CompileUsageError(message) ->
            let _ = Ashes.IO.writeErrorLine(message)
            in 2
        | CompileParsedArguments(arguments) ->
            match runCompileWithArguments(arguments) with
                | CompileSucceeded(exitCode) -> exitCode
                | CompileFailed(message) ->
                    let _ = Ashes.IO.writeErrorLine(message)
                    in 1

// Relays the child's stdout line by line and hands the process back for the next stage.
let recursive relayStdout process =
    match Ashes.IO.Process.readStdoutLine(process) with
        | None -> process
        | Some(line) ->
            let _ = Ashes.IO.writeLine(line)
            in relayStdout(process)

let recursive relayStderr process =
    match Ashes.IO.Process.readStderrLine(process) with
        | None -> process
        | Some(line) ->
            let _ = Ashes.IO.writeErrorLine(line)
            in relayStderr(process)

let temporaryExecutablePath inputPath =
    match Ashes.IO.Environment.temporaryDirectory(Unit) with
        | Error(message) -> Error("Could not locate the temporary directory: " + message)
        | Ok(temporary) ->
            let directory = Ashes.IO.Path.join(Ashes.IO.Path.Unix)(temporary)("ashes")
            in
                match Ashes.IO.Directory.createAll(directory) with
                    | Error(message) -> Error("Could not create " + directory + ": " + message)
                    | Ok(_) ->
                        inputPath
                        |> inputStem
                        |> Ashes.IO.Path.join(Ashes.IO.Path.Unix)(directory)
                        |> Ok

// Runs the compiled program with `programArguments`, relaying its stdout and stderr line by line,
// and returns its exit code.
let spawnCompiledProgram executablePath programArguments =
    match Ashes.IO.Process.spawn(executablePath)(programArguments) with
        | Error(message) -> Error("Could not start " + executablePath + ": " + message)
        | Ok(process) ->
            process
            |> relayStdout
            |> relayStderr
            |> Ashes.IO.Process.waitForExit
            |> Ok

let runProgramFile inputPath programArguments (explain: ExplainRequest) =
    match temporaryExecutablePath(inputPath) with
        | Error(message) -> Error(message)
        | Ok(executablePath) ->
            match compileFileToExecutable(inputPath)(executablePath)(explain) with
                | Error(message) -> Error(message)
                | Ok(_) -> spawnCompiledProgram(executablePath)(programArguments)

// The full `ashes run` entry point: compiles to the temporary directory, runs the program with the
// arguments after `--`, and returns the program's own exit code (1 for a failure before it starts).
let runRun args =
    match parseRunArguments(args) with
        | RunHelpRequested ->
            let _ = Ashes.IO.writeLine("Usage: ashes run [--explain <kind>] <input.ash> [-- <args...>]")
            in 0
        | RunInputError(message) ->
            let _ = Ashes.IO.writeErrorLine(message)
            in 1
        | RunUsageError(message) ->
            let _ = Ashes.IO.writeErrorLine(message)
            in 2
        | RunParsedArguments(RunArguments { runInputPath = inputPath, programArguments = programArguments, runExplain = explain }) ->
            match runProgramFile(inputPath)(programArguments)(explain) with
                | Error(message) ->
                    let _ = Ashes.IO.writeErrorLine(message)
                    in 1
                | Ok(exitCode) -> exitCode
