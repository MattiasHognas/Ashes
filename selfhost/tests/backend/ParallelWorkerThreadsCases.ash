// The worker threads of `Ashes.Task.Parallel` programs: a program built with raw `clone` threads
// and one built with libc threads both produce an object that links and runs, and a program first
// built with the wrong guess is built again with the kind its imports call for. The libc program's
// workers allocate and free through libc at the same time, which corrupts the heap unless each is
// a libc thread.
import Ashes.Test as test
import AshesCompiler.Backend.Llvm
import AshesCompiler.Backend.IrCodegen
import AshesCompiler.Backend.ElfLinker
import AshesCompiler.Semantics.CoreLowering
import AshesCompiler.Semantics.CoreLowering.Model
import AshesCompiler.Semantics.CoreLowering.Raw
import AshesCompiler.Semantics.ProjectSyntaxStitching
import AshesCompiler.Semantics.ShippedModuleStitching
import PartitionedObjectsCases.partitionTargetMachine
import PartitionedObjectsCases.runExecutableLines
export (
    value runParallelWorkerThreadsCases,
)

let workerTriple = "x86_64-unknown-linux-gnu"

let workerFail (message: Str) = test.fail("parallel worker threads: " + message)

// Eight workers square their element; nothing in the program calls into a shared library.
let staticParallelSource = "import Ashes.Task.Parallel\n\nlet squares = Ashes.Task.Parallel.map(given (n: Int) -> n * n)([1, 2, 3, 4, 5, 6, 7, 8])\nAshes.IO.print(Ashes.Text.fromInt(Ashes.Collection.List.foldLeft(given (a: Int) -> given (b: Int) -> a + b)(0)(squares)))"

// Eight workers allocate and free through libc's `malloc` and `free` at the same time.
let libcParallelSource = "import Ashes.Task.Parallel\nexternal libcMalloc(Int) -> Int needs {} = \"malloc@libc.so.6\"\nexternal libcFree(Int) -> void needs {} = \"free@libc.so.6\"\n\nlet recursive churn (seed: Int) (left: Int) (count: Int) =\n    if left == 0\n    then count\n    else\n        let first = libcMalloc(16 + (seed + left) % 200)\n        in\n            let _ = libcFree(first)\n            in churn(seed)(left - 1)(count + 1)\n\nlet counts = Ashes.Task.Parallel.map(given (seed: Int) -> churn(seed)(100000)(0))([1, 2, 3, 4, 5, 6, 7, 8])\nAshes.IO.print(Ashes.Text.fromInt(Ashes.Collection.List.foldLeft(given (a: Int) -> given (b: Int) -> a + b)(0)(counts)))"

// The module optimized and emitted as a relocatable object.
let emitWorkerObject module_ builder context =
    match partitionTargetMachine(Unit) with
        | Error(message) -> Error(message)
        | Ok(machine) ->
            let _ = setTarget(module_)(workerTriple)
            in
                let _ = applyDataLayout(module_)(machine)
                in
                    let _ =
                        let options = createPassBuilderOptions(Unit)
                        in
                            let _ = runPasses(module_)("default<O2>")(machine)(options)
                            in disposePassBuilderOptions(options)
                    in
                        match targetMachineEmitToMemoryBuffer(machine)(module_)(objectFileType) with
                            | (true, _, _) -> Error("LLVM reported the module as broken during emission")
                            | (false, _, None) -> Error("expected an emitted buffer")
                            | (false, _, Some(buffer)) ->
                                let copied =
                                    buffer
                                    |> getBufferSize
                                    |> Ashes.Ffi.copyBytes(getBufferStart(buffer))
                                in
                                    Unit
                                    |> (given (_) -> disposeMemoryBuffer(buffer))
                                    |> (given (_) -> disposeTargetMachine(machine))
                                    |> (given (_) -> disposeBuilder(builder))
                                    |> (given (_) -> disposeModule(module_))
                                    |> (given (_) -> contextDispose(context))
                                    |> (given (_) -> copied)

// The program at `source` stitched with the shipped modules, lowered and built with the given
// worker thread kind, answering its object and its external libraries.
let parallelObject shipped (name: Str) (source: Str) (libcThreads: Bool) =
    match stitchWithShippedModules(name)(name + ".ash")(source)(shipped) with
        | Error(error) -> Error("shipped-module stitching failed: " + Ashes.Trait.Show.show(error))
        | Ok(StitchedSyntaxProject { program = program }) ->
            match lowerCoreProgramWithSource(name + ".ash")(source)(program) with
                | CoreLoweringResult { program = Some(lowered), error = None } ->
                    let _ = initializeX86Target(Unit)
                    in
                        let context = contextCreate(Unit)
                        in
                            match codegenProgramWithThreads(false)(libcThreads)(name)(context)(lowered) with
                                | (module_, builder) ->
                                    match emitWorkerObject(module_)(builder)(context) with
                                        | Error(message) -> Error(message)
                                        | Ok(objectBytes) -> Ok((objectBytes, externalLibraries(lowered)))
                | CoreLoweringResult { error = Some(error) } -> Error("lowering failed: " + Ashes.Trait.Show.show(error))
                | _ -> Error("lowering produced no program")

let objectOfBuilt built =
    match built with
        | (objectBytes, _libraries) -> objectBytes

// Links and runs a built object, expecting `expected` as its one line and a zero exit.
let expectBuiltRuns (path: Str) (name: Str) (expected: Str) built =
    match built with
        | (objectBytes, libraries) ->
            match linkLinuxExecutableWithImports(objectBytes)(name)(libraries) with
                | Error(message) -> workerFail(path + ": link: " + message)
                | Ok(executableBytes) ->
                    match runExecutableLines(path)(executableBytes) with
                        | (lines, exitCode) ->
                            if lines != [expected] || exitCode != 0
                            then workerFail(path + " printed " + Ashes.Text.join("|")(lines) + " (exit " + Ashes.Text.fromInt(exitCode) + "), expected " + expected)
                            else Unit

let expectLoadsSharedLibraries (path: Str) (expected: Bool) built =
    match built with
        | (objectBytes, libraries) ->
            if linuxObjectLoadsSharedLibraries(objectBytes)(libraries) == expected
            then Unit
            else
                workerFail(path + ": the object " + (if expected
                then "imports nothing from a shared library"
                else "imports from a shared library"))

// Raw `clone` threads: the program built with them links and runs. Its object still imports
// `malloc`, `free` and `memcpy` (the reference-counted runtime and LLVM's copies), so the choice
// builds every self-hosted image with libc threads; this keeps the raw path itself working.
let testRawWorkerThreads shipped unit =
    match parallelObject(shipped)("selfhostBackendRawWorkers")(staticParallelSource)(false) with
        | Error(message) -> workerFail("raw threads: " + message)
        | Ok(built) ->
            Unit
            |> (given (_) -> expectLoadsSharedLibraries("selfhost_backend_raw_workers")(true)(built))
            |> (given (_) -> expectBuiltRuns("selfhost_backend_raw_workers")("selfhostBackendRawWorkers")("204")(built))

// libc threads: the program that calls libc loads shared libraries and its workers run safely.
let testLibcWorkerThreads shipped unit =
    match parallelObject(shipped)("selfhostBackendLibcWorkers")(libcParallelSource)(true) with
        | Error(message) -> workerFail("libc threads: " + message)
        | Ok(built) ->
            Unit
            |> (given (_) -> expectLoadsSharedLibraries("selfhost_backend_libc_workers")(true)(built))
            |> (given (_) -> expectBuiltRuns("selfhost_backend_libc_workers")("selfhostBackendLibcWorkers")("800000")(built))

// A first guess of raw threads for the program that calls libc is built again with libc threads.
let testWorkerThreadsRebuiltForImports shipped unit =
    match objectWithWorkerThreads(parallelObject(shipped)("selfhostBackendRebuiltWorkers")(libcParallelSource))(objectOfBuilt)(false)([("malloc", "libc.so.6"), ("free", "libc.so.6")]) with
        | Error(message) -> workerFail("rebuild: " + message)
        | Ok(built) ->
            Unit
            |> (given (_) -> expectLoadsSharedLibraries("selfhost_backend_rebuilt_workers")(true)(built))
            |> (given (_) -> expectBuiltRuns("selfhost_backend_rebuilt_workers")("selfhostBackendRebuiltWorkers")("800000")(built))

let runParallelWorkerThreadsCases shipped unit =
    unit
    |> testRawWorkerThreads(shipped)
    |> testLibcWorkerThreads(shipped)
    |> testWorkerThreadsRebuiltForImports(shipped)
