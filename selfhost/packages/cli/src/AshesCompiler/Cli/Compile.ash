// The `ashes compile` and `ashes run` commands: compile one `.ash` file or a whole project to a
// linux-x64 executable through the self-hosted pipeline, and optionally run it with forwarded
// arguments.
//
// Invariants:
// - The pipeline is the same one `selfhost/tests/backend` proves end to end: the entry source and
//   the shipped standard-library texts go through `stitchWithShippedModules` (a project's modules,
//   its dependencies' modules, and the shipped texts through `stitchProject`), the stitched
//   program through `lowerCoreProgramWithSource` and `optimizeIrProgram`, the lowered `IrProgram`
//   through `codegenProgram`, and the emitted object through `linkLinuxExecutable`. Nothing here
//   parses, infers, lowers, or generates code on its own.
// - Matches stage 0's observable contract (docs/md/reference/cli.md#ashes-compile) for the
//   single-file and project forms: the positional input must end in `.ash`, `--project <manifest>`
//   compiles the project rooted at that manifest and cannot be combined with a positional input,
//   no input at all discovers `ashes.json` upward from the working directory, `-o`/`--out` selects
//   the output, the default output drops the `.ash` suffix (a project's is `<outDir>/<name>`, the
//   entry file's stem when the manifest names nothing), success prints the `OK Wrote <size> to
//   <output>` confirmation with its `Target:` line on stdout, diagnostics and command errors go to
//   stderr, and the exit codes are 0/1/2 for success, compilation or input failure, and usage
//   error. `ashes run` compiles to the host temporary directory, forwards everything after `--`
//   to the program, and propagates the program's own exit code.
// - Source locations in a project build are computed against the entry module's text: the
//   stitched program keeps every module's original spans, but the lowering's source context takes
//   one text, so a diagnostic or debug line in a non-entry module is not yet mapped to that
//   module's own lines.
// - `--explain <kind>[:<selector>]` is accepted by both commands, repeats, and prints the requested
//   compiler reports to stderr between optimization and code generation
//   (docs/md/reference/cli.md#compiler-reports). Reporting reads the decision snapshot and the
//   optimized program and writes neither, so the emitted image is the same whether or not a
//   report was asked for. An unknown kind or a missing value is a usage error listing the valid
//   values.
// - Deliberately narrower than stage 0 for now: only the linux-x64 target and the file and
//   project forms (`--expr`, target/optimization/debug options, and IR dumps are not parsed), no
//   automatic restore of a project's registry dependencies before compiling, no elapsed time in
//   the confirmation (no monotonic clock capability is shipped yet), a program's stdout and stderr
//   are relayed line by line rather than inherited, and the shipped standard library is located by
//   probing `lib/Ashes` beside the executable, beside its parent directory, and under the working
//   directory, in that order. Any program shape the backend does not support yet surfaces exactly
//   as `AshesCompiler.Backend.IrCodegen` reports it.

import Ashes.Byte
import Ashes.Ffi
import Ashes.Collection.List.append
import Ashes.Collection.List.reverse as reverseList
import Ashes.IO.Path
import AshesCompiler.Backend.Llvm
import AshesCompiler.Backend.IrCodegen
import AshesCompiler.Backend.ElfLinker
import AshesCompiler.Frontend.Syntax
import AshesCompiler.Frontend.Token
import AshesCompiler.Semantics.CoreLowering
import AshesCompiler.Semantics.DecisionSnapshot
import AshesCompiler.Semantics.IrOrigins
import AshesCompiler.Semantics.LoweringDiagnostics
import AshesCompiler.Semantics.ExplainReport
import AshesCompiler.Semantics.ExplainReportFormatter
import AshesCompiler.Semantics.Ir
import AshesCompiler.Semantics.IrExplainReporter
import AshesCompiler.Semantics.IrOptimizer
import AshesCompiler.Semantics.ModuleSemanticStitching
import AshesCompiler.Semantics.ProjectDiscovery
import AshesCompiler.Semantics.ProjectManifest
import AshesCompiler.Semantics.ProjectStitching
import AshesCompiler.Semantics.ProjectSyntaxStitching
import AshesCompiler.Semantics.ShippedModuleStitching
export (
    type CompileInput(..),
    type CompileArguments(..),
    type CompileParse(..),
    type CompileOutcome(..),
    type RunArguments(..),
    type RunParse(..),
    value parseCompileArguments,
    value parseRunArguments,
    value defaultOutputPath,
    value projectOutputPath,
    value formatByteSize,
    value inputStem,
    value explainValidValuesText,
    value compileFileToExecutable,
    value compileProjectToExecutable,
    value runCompileWithArguments,
    value runCompile,
    value runRun,
)

// What a compile or run command was pointed at: a positional `.ash` file, the project rooted at
// an explicit `--project` manifest, or (neither given) the `ashes.json` discovered upward from
// the working directory when the command runs.
type CompileInput =
    | CompileFile(Str)
    | CompileProject(Str)
    | CompileDiscoveredProject

// `disableReuse` is stage 0's hidden `--debug-disable-reuse`: the program is lowered with every
// reuse token withheld, so a reuse-related miscompile can be bisected against fresh allocation.
type CompileArguments =
    | input: CompileInput
    | outputPath: Maybe(Str)
    | explain: ExplainRequest
    | disableReuse: Bool

type CompileParse =
    | CompileHelpRequested
    | CompileInputError(Str)
    | CompileUsageError(Str)
    | CompileParsedArguments(CompileArguments)

type CompileOutcome =
    | CompileSucceeded(Int)
    | CompileFailed(Str)

type RunArguments =
    | runInput: CompileInput
    | programArguments: List(Str)
    | runExplain: ExplainRequest
    | runDisableReuse: Bool

// The input once its manifest, if any, is loaded.
type ResolvedCompileInput =
    | ResolvedFileInput(Str)
    | ResolvedProjectInput(ProjectLayout)

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

let recursive partitionCompileFlags args output inputs project explain disableReuse =
    match args with
        | [] -> Ok((output, inputs, project, explain, disableReuse))
        | "-o" :: value :: rest -> partitionCompileFlags(rest)(Some(value))(inputs)(project)(explain)(disableReuse)
        | "--out" :: value :: rest -> partitionCompileFlags(rest)(Some(value))(inputs)(project)(explain)(disableReuse)
        | "-o" :: [] -> Error("Missing value for -o.")
        | "--out" :: [] -> Error("Missing value for --out.")
        | "--project" :: value :: rest -> partitionCompileFlags(rest)(output)(inputs)(Some(value))(explain)(disableReuse)
        | "--project" :: [] -> Error("Missing value for --project.")
        | "--explain" :: value :: rest ->
            match addExplainOption(value)(explain) with
                | Error(message) -> Error(message)
                | Ok(added) -> partitionCompileFlags(rest)(output)(inputs)(project)(added)(disableReuse)
        | "--explain" :: [] -> Error("--explain requires a value." + explainValidValuesText)
        | "--debug-disable-reuse" :: rest -> partitionCompileFlags(rest)(output)(inputs)(project)(explain)(true)
        | other :: rest ->
            if isOptionLike(other)
            then Error("Unknown option '" + other + "'.")
            else
                partitionCompileFlags(rest)(output)(append(inputs)([other]))(project)(explain)(disableReuse)

let checkInputPath input =
    if hasAshExtension(input)
    then Ok(input)
    else Error("Input file must have a .ash extension: " + input)

type CompileInputSelection =
    | SelectedInput(CompileInput)
    | SelectionInputError(Str)
    | SelectionUsageError(Str)

// The positional inputs and the `--project` option select one `CompileInput`: neither is the
// project discovered when the command runs, `--project` alone is that project, exactly one
// positional file is the file form (an input error without the `.ash` extension), and the two
// together or several files are usage errors, with stage 0's own messages.
let selectCompileInput inputs project =
    match (inputs, project) with
        | ([], None) -> SelectedInput(CompileDiscoveredProject)
        | ([], Some(manifest)) -> SelectedInput(CompileProject(manifest))
        | (input :: [], None) ->
            match checkInputPath(input) with
                | Error(message) -> SelectionInputError(message)
                | Ok(checked) -> SelectedInput(CompileFile(checked))
        | (_input :: [], Some(_manifest)) -> SelectionUsageError("Cannot combine --project with input file or --expr.")
        | (_inputs, _project) -> SelectionUsageError("Provide exactly one input file.")

// A bare `--help`/`-h` short-circuits; an unknown option, a bad `--explain` value, a missing
// option value, or a conflicting input selection is a usage error (exit 2); a positional input
// without the `.ash` extension is an input error (exit 1). No input at all parses as the
// discovered project: whether an `ashes.json` exists is only known when the command runs.
let parseCompileArguments args =
    match args with
        | "--help" :: [] -> CompileHelpRequested
        | "-h" :: [] -> CompileHelpRequested
        | _ ->
            match partitionCompileFlags(args)(None)([])(None)(explainRequestNone)(false) with
                | Error(message) -> CompileUsageError(message)
                | Ok((output, inputs, project, explain, disableReuse)) ->
                    match selectCompileInput(inputs)(project) with
                        | SelectionInputError(message) -> CompileInputError(message)
                        | SelectionUsageError(message) -> CompileUsageError(message)
                        | SelectedInput(input) -> CompileParsedArguments(CompileArguments(input = input, outputPath = output, explain = explain, disableReuse = disableReuse))

let recursive splitProgramArguments args before =
    match args with
        | [] -> (reverseList(before), [])
        | "--" :: rest -> (reverseList(before), rest)
        | other :: rest -> splitProgramArguments(rest)(other :: before)

let recursive partitionRunFlags args inputs project explain disableReuse =
    match args with
        | [] -> Ok((inputs, project, explain, disableReuse))
        | "--project" :: value :: rest -> partitionRunFlags(rest)(inputs)(Some(value))(explain)(disableReuse)
        | "--project" :: [] -> Error("Missing value for --project.")
        | "--explain" :: value :: rest ->
            match addExplainOption(value)(explain) with
                | Error(message) -> Error(message)
                | Ok(added) -> partitionRunFlags(rest)(inputs)(project)(added)(disableReuse)
        | "--explain" :: [] -> Error("--explain requires a value." + explainValidValuesText)
        | "--debug-disable-reuse" :: rest -> partitionRunFlags(rest)(inputs)(project)(explain)(true)
        | other :: rest ->
            if isOptionLike(other)
            then Error("Unknown option '" + other + "'.")
            else
                partitionRunFlags(rest)(append(inputs)([other]))(project)(explain)(disableReuse)

let parseRunArguments args =
    match args with
        | "--help" :: [] -> RunHelpRequested
        | "-h" :: [] -> RunHelpRequested
        | _ ->
            match splitProgramArguments(args)([]) with
                | (before, programArguments) ->
                    match partitionRunFlags(before)([])(None)(explainRequestNone)(false) with
                        | Error(message) -> RunUsageError(message)
                        | Ok((inputs, project, explain, disableReuse)) ->
                            match selectCompileInput(inputs)(project) with
                                | SelectionInputError(message) -> RunInputError(message)
                                | SelectionUsageError(message) -> RunUsageError(message)
                                | SelectedInput(input) -> RunParsedArguments(RunArguments(runInput = input, programArguments = programArguments, runExplain = explain, runDisableReuse = disableReuse))

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
let locationPrefix (location: Maybe(IrSourceLocation)) =
    match location with
        | Some(IrSourceLocation { filePath = filePath, line = line, column = column }) -> filePath + ":" + Ashes.Text.fromInt(line) + ":" + Ashes.Text.fromInt(column) + " "
        | None -> ""

let codePrefix (code: Maybe(Str)) =
    match code with
        | Some(text) -> text + " "
        | None -> ""

// A lowering error with a stage-0 rendering prints as stage 0 prints it, `path:line:col CODE
// message`; every other error prints its structural form.
let loweringErrorText (error: CoreLoweringError) =
    match loweringErrorDiagnostic(error) with
        | Some(DiagnosticEntry { message = message, code = code }) ->
            locationPrefix(loweringErrorLocation(error)) + codePrefix(code) + message
        | None -> Ashes.Trait.Show.show(error)

let lowerStitchedProgram reuseEnabled inputPath source program =
    match lowerCoreProgramWithSourceAndReuse(reuseEnabled)(inputPath)(source)(program) with
        | CoreLoweringResult { program = Some(lowered), error = None, valuePlacements = valuePlacements, joinRepresentations = joinRepresentations } -> Ok((lowered, optimizeIrProgram(lowered), (valuePlacements, joinRepresentations)))
        | CoreLoweringResult { error = Some(error) } ->
            error
            |> loweringErrorText
            |> Error
        | _ -> Error("Lowering produced no program.")

// The stitched project lowered and optimized, with the placement facts the reports read.
let lowerStitched reuseEnabled inputPath source stitched =
    match stitched with
        | StitchedSyntaxProject { program = program } ->
            match lowerStitchedProgram(reuseEnabled)(inputPath)(source)(program) with
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
// lowering and the placement facts recorded against it — every value's placement, and the
// representation the lowering decided for each control-flow join — and the RC counts read the
// optimized program the backend is about to receive.
let explainReportLines (explain: ExplainRequest) (stitched: StitchedSyntaxProject) (lowered: IrProgram) placementFacts (optimized: IrProgram) =
    match (stitched, placementFacts) with
        | (StitchedSyntaxProject { program = program }, (valuePlacements, joinRepresentations)) ->
            joinRepresentations
            |> captureDecisionSnapshot(stitchedQualifiedName(stitched))(program)(lowered)(valuePlacements)
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

// Mirrors stage 0's `RunLlvmOptimizationPasses`/`RunLlvmPassPipeline`: LLVM's new-pass-manager
// default pipeline for the selected level, run once per emitted module right before codegen. A
// call frame this backend's own codegen leaves fully materialized on the stack (this package
// otherwise never runs mem2reg/SROA, unlike stage 0's default O2 build) is exactly the kind of
// thing this pipeline exists to eliminate.
let runOptimizationPasses module_ machine =
    (let options = createPassBuilderOptions(Unit)
    in
        let _ = runPasses(module_)("default<O2>")(machine)(options)
        in disposePassBuilderOptions(options))

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
            let _ = runOptimizationPasses(module_)(machine)
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
// LLVM's default optimization level and static relocations, matching stage 0's own default build
// (`BackendOptimizationLevel.O2`).
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
                            |> createTargetMachine(target)(triple)(cpu)(features)(codeGenOptLevelDefault)(relocModeStatic)
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

// The output's directory is created first, as stage 0 does, so a fresh project's
// `<outDir>/<name>` needs no prior `mkdir`.
let ensureOutputDirectory outputPath =
    (let directory = Ashes.IO.Path.parent(Ashes.IO.Path.Unix)(outputPath)
    in
        if directory == "" || directory == "."
        then Ok(Unit)
        else
            match Ashes.IO.Directory.createAll(directory) with
                | Error(message) -> Error("Could not create " + directory + ": " + message)
                | Ok(_) -> Ok(Unit))

let writeExecutable outputPath executableBytes =
    match ensureOutputDirectory(outputPath) with
        | Error(message) -> Error(message)
        | Ok(_) ->
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

// Lowers, reports, emits, and links an already stitched program to `outputPath`, returning the
// written byte count; `inputPath` and `source` are the text source locations are computed from.
let compileStitchedToExecutable outputPath (explain: ExplainRequest) (reuseEnabled: Bool) inputPath source stitched =
    match lowerStitched(reuseEnabled)(inputPath)(source)(stitched) with
        | Error(message) -> Error(message)
        | Ok((stitchedProject, lowered, optimized, valuePlacements)) ->
            optimized
            |> writeExplainReport(explain)(stitchedProject)(lowered)(valuePlacements)
            |> (given (_) -> emitAndLink(outputPath)(optimized))

// Compiles `inputPath` to the executable at `outputPath`, printing the `explain` reports to stderr
// on the way, and returns the written byte count.
let compileFileToExecutable inputPath outputPath (explain: ExplainRequest) (reuseEnabled: Bool) =
    match Ashes.IO.File.readText(inputPath) with
        | Error(message) -> Error("Could not read " + inputPath + ": " + message)
        | Ok(source) ->
            match loadShippedModules(Unit) with
                | Error(message) -> Error(message)
                | Ok(shipped) ->
                    match stitchWithShippedModules(inputStem(inputPath))(inputPath)(source)(shipped) with
                        | Error(error) ->
                            error
                            |> Ashes.Trait.Show.show
                            |> Error
                        | Ok(stitched) -> compileStitchedToExecutable(outputPath)(explain)(reuseEnabled)(inputPath)(source)(stitched)

// Compiles the project at `layout` to the executable at `outputPath`: its modules, its
// dependencies' modules, and the shipped modules they reach are stitched into one program,
// lowered against the entry module's text, and emitted exactly like the single-file form.
let compileProjectToExecutable (layout: ProjectLayout) outputPath (explain: ExplainRequest) (reuseEnabled: Bool) =
    match Ashes.IO.File.readText(layout.entryPath) with
        | Error(message) -> Error("Could not read " + layout.entryPath + ": " + message)
        | Ok(source) ->
            match loadShippedModules(Unit) with
                | Error(message) -> Error(message)
                | Ok(shipped) ->
                    match stitchProject(Ashes.IO.Path.Unix)(layout)(shipped) with
                        | Error(error) ->
                            error
                            |> Ashes.Trait.Show.show
                            |> Error
                        | Ok(stitched) -> compileStitchedToExecutable(outputPath)(explain)(reuseEnabled)(layout.entryPath)(source)(stitched)

let compileResolvedInput resolved outputPath (explain: ExplainRequest) (reuseEnabled: Bool) =
    match resolved with
        | ResolvedFileInput(inputPath) -> compileFileToExecutable(inputPath)(outputPath)(explain)(reuseEnabled)
        | ResolvedProjectInput(layout) -> compileProjectToExecutable(layout)(outputPath)(explain)(reuseEnabled)

// A project's output name: the manifest's name, or the entry file's stem when it names nothing.
let projectOutputName (layout: ProjectLayout) =
    match layout with
        | ProjectLayout { entryPath = entryPath, manifest = ProjectManifest { name = name } } ->
            match name with
                | Some(text) ->
                    if Ashes.Text.trim(text) == ""
                    then inputStem(entryPath)
                    else text
                | None -> inputStem(entryPath)

// Stage 0's default project output, `<outDir>/<name>`, with the manifest's `outDir` already
// resolved against the project directory.
let projectOutputPath (layout: ProjectLayout) =
    layout
    |> projectOutputName
    |> Ashes.IO.Path.join(Ashes.IO.Path.Unix)(layout.outDir)

let defaultOutputFor resolved =
    match resolved with
        | ResolvedFileInput(inputPath) -> defaultOutputPath(inputPath)
        | ResolvedProjectInput(layout) -> projectOutputPath(layout)

// The name a `run` temporary executable takes: the file's stem, or the project's output name.
let resolvedInputStem resolved =
    match resolved with
        | ResolvedFileInput(inputPath) -> inputStem(inputPath)
        | ResolvedProjectInput(layout) -> projectOutputName(layout)

// `written` is the manifest path as the user gave it, named in stage 0's "Project file not
// found" message; `manifestPath` is where it resolved to.
let loadProjectInput written manifestPath =
    match loadProject(Ashes.IO.Path.Unix)(manifestPath) with
        | Error(ProjectReadError(_path, _message)) -> Error("Project file not found: " + written)
        | Error(error) ->
            error
            |> Ashes.Trait.Show.show
            |> Error
        | Ok(layout) -> Ok(ResolvedProjectInput(layout))

let currentDirectory unit =
    match Ashes.IO.Environment.currentDirectory(Unit) with
        | Error(message) -> Error("Could not determine the working directory: " + message)
        | Ok(directory) -> Ok(directory)

// The input once resolved: a file as given, an explicit manifest loaded relative to the working
// directory (as stage 0 resolves it), or the manifest discovered upward from the working
// directory; nothing to compile at all is stage 0's "Missing input file or --expr.".
let resolveCompileInput input =
    match input with
        | CompileFile(inputPath) -> Ok(ResolvedFileInput(inputPath))
        | CompileProject(written) ->
            match currentDirectory(Unit) with
                | Error(message) -> Error(message)
                | Ok(directory) ->
                    match selectProjectFile(Ashes.IO.Path.Unix)(directory)(Some(written)) with
                        | Ok(Some(manifestPath)) -> loadProjectInput(written)(manifestPath)
                        | _ -> Error("Project file not found: " + written)
        | CompileDiscoveredProject ->
            match currentDirectory(Unit) with
                | Error(message) -> Error(message)
                | Ok(directory) ->
                    match selectProjectFile(Ashes.IO.Path.Unix)(directory)(None) with
                        | Ok(Some(manifestPath)) -> loadProjectInput(manifestPath)(manifestPath)
                        | _ -> Error("Missing input file or --expr.")

let runCompileWithArguments arguments =
    match arguments with
        | CompileArguments { input = input, outputPath = outputPath, explain = explain, disableReuse = disableReuse } ->
            match resolveCompileInput(input) with
                | Error(message) -> CompileFailed(message)
                | Ok(resolved) ->
                    let output =
                        match outputPath with
                            | Some(explicit) -> explicit
                            | None -> defaultOutputFor(resolved)
                    in
                        match compileResolvedInput(resolved)(output)(explain)(disableReuse == false) with
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
            let _ = Ashes.IO.writeLine("Usage: ashes compile [--project <manifest>] [--explain <kind>] [-o <output>] [<input.ash>]")
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

let temporaryExecutablePath stem =
    match Ashes.IO.Environment.temporaryDirectory(Unit) with
        | Error(message) -> Error("Could not locate the temporary directory: " + message)
        | Ok(temporary) ->
            let directory = Ashes.IO.Path.join(Ashes.IO.Path.Unix)(temporary)("ashes")
            in
                match Ashes.IO.Directory.createAll(directory) with
                    | Error(message) -> Error("Could not create " + directory + ": " + message)
                    | Ok(_) ->
                        stem
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

let runProgram input programArguments (explain: ExplainRequest) (reuseEnabled: Bool) =
    match resolveCompileInput(input) with
        | Error(message) -> Error(message)
        | Ok(resolved) ->
            match resolved
            |> resolvedInputStem
            |> temporaryExecutablePath with
                | Error(message) -> Error(message)
                | Ok(executablePath) ->
                    match compileResolvedInput(resolved)(executablePath)(explain)(reuseEnabled) with
                        | Error(message) -> Error(message)
                        | Ok(_) -> spawnCompiledProgram(executablePath)(programArguments)

// The full `ashes run` entry point: compiles to the temporary directory, runs the program with the
// arguments after `--`, and returns the program's own exit code (1 for a failure before it starts).
let runRun args =
    match parseRunArguments(args) with
        | RunHelpRequested ->
            let _ = Ashes.IO.writeLine("Usage: ashes run [--project <manifest>] [--explain <kind>] [<input.ash>] [-- <args...>]")
            in 0
        | RunInputError(message) ->
            let _ = Ashes.IO.writeErrorLine(message)
            in 1
        | RunUsageError(message) ->
            let _ = Ashes.IO.writeErrorLine(message)
            in 2
        | RunParsedArguments(RunArguments { runInput = input, programArguments = programArguments, runExplain = explain, runDisableReuse = disableReuse }) ->
            match runProgram(input)(programArguments)(explain)(disableReuse == false) with
                | Error(message) ->
                    let _ = Ashes.IO.writeErrorLine(message)
                    in 1
                | Ok(exitCode) -> exitCode
