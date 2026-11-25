// Partitioned code generation, stage 0's `CompileParallelObjects`: a program is split into
// partitions (`ProgramPartitions`), each built as its own LLVM module in its own context with its
// own target machine (`IrCodegen.codegenProgramPartition`), verified, optimized and emitted as an
// object, and the objects are merged into one relocatable object (`ElfRelocatableMerge`, partition 0
// first) for the ordinary single-object linker. The vendored bitcode payloads are linked into
// partition 0 alone after its passes; the other partitions reach them through the merge.
//
// Every partition module is built first, while the program is read; then the modules are
// verified, optimized and emitted one after another, each disposed once its object exists. A
// partition's optimize-and-emit step takes only LLVM handles and the payload paths and answers
// `(Bool, Bytes)` (the object, or the UTF-8 error text), the shape a worker thread can run. All
// unoptimized modules are alive together while the first ones are optimized; the peak is that plus
// one partition's optimization, never the whole program's.
import Ashes.Byte
import Ashes.Ffi
import Ashes.Collection.Map
import AshesCompiler.Semantics.Ir
import AshesCompiler.Backend.Llvm
import AshesCompiler.Backend.IrCodegen
import AshesCompiler.Backend.ProgramPartitions
import AshesCompiler.Backend.ElfRelocatableMerge
import AshesCompiler.Backend.PartitionProcesses
import AshesCompiler.Backend.PartitionDiagnostics
export (
    value optimizeAndEmitPartition,
    value emitPartitionedObject,
    value emitPartitionObjects,
    value buildPartitionModules,
    value mergePartitionModules,
    value mergePartitionModulesWith,
    value emitPartitionedObjectWith,
    value partitionWorkDirectory,
    type PartitionModule,
)

let failedPartition (message: Str) = (false, Ashes.Byte.fromText(message))

let recursive linkPartitionPayloads context module_ triple (payloadPaths: List(Str)) =
    match payloadPaths with
        | [] -> Ok(Unit)
        | path :: rest ->
            match linkBitcodeFile(context)(module_)(triple)(path) with
                | Error(message) -> Error(message)
                | Ok(_) -> linkPartitionPayloads(context)(module_)(triple)(rest)

// Optimizes one verified partition module, links the payloads at `payloadPaths` into it (partition 0
// only), emits its object and disposes the module, its builder, its target machine and its context.
let optimizeAndEmitPartition machine module_ context builder (triple: Str) (payloadPaths: List(Str)) =
    (let options = createPassBuilderOptions(Unit)
    in
        let _ = runPasses(module_)("default<O2>")(machine)(options)
        in
            let _ = reportPartitionPhase("partition optimized")
            in
                let _ = disposePassBuilderOptions(options)
                in
                    let emitted =
                        match linkPartitionPayloads(context)(module_)(triple)(payloadPaths) with
                            | Error(message) -> failedPartition(message)
                            | Ok(_) ->
                                match targetMachineEmitToMemoryBuffer(machine)(module_)(objectFileType) with
                                    | (true, _, _) -> failedPartition("LLVM reported a partition module as broken during emission.")
                                    | (false, _, None) -> failedPartition("LLVM produced no object buffer for a partition.")
                                    | (false, _, Some(buffer)) ->
                                        let copied =
                                            buffer
                                            |> getBufferSize
                                            |> Ashes.Ffi.copyBytes(getBufferStart(buffer))
                                        in
                                            let _ = disposeMemoryBuffer(buffer)
                                            in
                                                match copied with
                                                    | Ok(bytes) -> (true, bytes)
                                                    | Error(message) -> failedPartition(message)
                    in
                        Unit
                        |> (given (_) -> disposeTargetMachine(machine))
                        |> (given (_) -> disposeBuilder(builder))
                        |> (given (_) -> disposeModule(module_))
                        |> (given (_) -> contextDispose(context))
                        |> (given (_) -> emitted))

let partitionText (index: Int) = "partition " + Ashes.Text.fromInt(index)

let bytesText (bytes: Bytes) =
    bytes
    |> Ashes.Byte.length
    |> Ashes.Byte.subText(bytes)(0)

// One partition's module as code generation left it, before verification and optimization.
type PartitionModule =
    | partitionModuleIndex: Int
    | partitionModuleContext: LLVMContextRef
    | partitionModuleRef: LLVMModuleRef
    | partitionModuleBuilder: LLVMBuilderRef

// With the statistics switch on, the size of a partition module as code generation left it.
let reportPartitionBuilt (index: Int) module_ =
    if partitionStatsRequested(Unit)
    then reportPartitionPhase(partitionText(index) + " built " + partitionModuleSizeText(module_))
    else Unit

let recursive buildPartitionModulesFrom (leakCheck: Bool) (entryName: Str) (assignment: MapTree(Str, Int)) (program: IrProgram) (index: Int) (count: Int) (acc: List(PartitionModule)) =
    if index >= count
    then Ashes.Collection.List.reverse(acc)
    else
        let context = contextCreate(Unit)
        in
            match codegenProgramPartition(leakCheck)(index)(assignment)(entryName)(context)(program) with
                | (module_, builder) ->
                    let _ = reportPartitionBuilt(index)(module_)
                    in buildPartitionModulesFrom(leakCheck)(entryName)(assignment)(program)(index + 1)(count)(PartitionModule(partitionModuleIndex = index, partitionModuleContext = context, partitionModuleRef = module_, partitionModuleBuilder = builder) :: acc)

// Every partition's module of `partitions` partitions of `program`, partition 0 first, each in its
// own context. This is the only step that reads the program: once it returns, a caller holding
// the program in a frame of its own can release it before the modules are optimized.
let buildPartitionModules (leakCheck: Bool) (entryName: Str) (partitions: Int) (program: IrProgram) =
    (let count = clampObjectPartitions(partitions)
    in
        let assignment = assignObjectPartitions(program)(count)
        in
            let _ = reportPartitionPhase("partition plan " + Ashes.Text.fromInt(count) + " partitions")
            in buildPartitionModulesFrom(leakCheck)(entryName)(assignment)(program)(0)(count)([]))

// A partition with its own target machine, its module set to the machine's triple and data layout.
type PartitionJob =
    | partitionJobIndex: Int
    | partitionJobContext: LLVMContextRef
    | partitionJobModule: LLVMModuleRef
    | partitionJobBuilder: LLVMBuilderRef
    | partitionJobMachine: LLVMTargetMachineRef

let preparePartitionJob (triple: Str) resolveMachine (partition: PartitionModule) =
    match partition with
        | PartitionModule { partitionModuleIndex = index, partitionModuleContext = context, partitionModuleRef = module_, partitionModuleBuilder = builder } ->
            match resolveMachine(Unit) with
                | Error(message) -> Error(message)
                | Ok(machine) ->
                    let _ = setTarget(module_)(triple)
                    in
                        let _ = applyDataLayout(module_)(machine)
                        in Ok(PartitionJob(partitionJobIndex = index, partitionJobContext = context, partitionJobModule = module_, partitionJobBuilder = builder, partitionJobMachine = machine))

let verificationText (index: Int) report =
    match report with
        | Ok(Some(text)) -> "LLVM module verification failed in " + partitionText(index) + ": " + text
        | _ -> "LLVM module verification failed in " + partitionText(index) + ": unknown error"

// Verifies, optimizes and emits a prepared partition in this process, then disposes it; the
// payloads are linked into partition 0 alone.
let emitPartitionInProcess (triple: Str) (payloadPaths: List(Str)) (job: PartitionJob) =
    match job with
        | PartitionJob { partitionJobIndex = index, partitionJobContext = context, partitionJobModule = module_, partitionJobBuilder = builder, partitionJobMachine = machine } ->
            match verifyModule(module_)(verifierReturnStatusAction) with
                | (true, report) ->
                    report
                    |> verificationText(index)
                    |> Error
                | (false, _) ->
                    let paths =
                        if index == 0
                        then payloadPaths
                        else []
                    in
                        let _ = reportPartitionPhase(partitionText(index) + " verified")
                        in
                            match optimizeAndEmitPartition(machine)(module_)(context)(builder)(triple)(paths) with
                                | (true, objectBytes) ->
                                    let _ = reportPartitionPhase(partitionText(index) + " emitted")
                                    in Ok(objectBytes)
                                | (false, errorText) -> Error(partitionText(index) + ": " + bytesText(errorText))

// Verifies, optimizes and emits one partition module with a fresh target machine, then disposes it.
let emitPartitionModule (triple: Str) resolveMachine (payloadPaths: List(Str)) (partition: PartitionModule) =
    match preparePartitionJob(triple)(resolveMachine)(partition) with
        | Error(message) -> Error(message)
        | Ok(job) -> emitPartitionInProcess(triple)(payloadPaths)(job)

let recursive emitPartitionModulesFrom (triple: Str) resolveMachine (payloadPaths: List(Str)) (partitions: List(PartitionModule)) (acc: List(Bytes)) =
    match partitions with
        | [] ->
            acc
            |> Ashes.Collection.List.reverse
            |> Ok
        | partition :: rest ->
            match emitPartitionModule(triple)(resolveMachine)(payloadPaths)(partition) with
                | Error(message) -> Error(message)
                | Ok(objectBytes) -> emitPartitionModulesFrom(triple)(resolveMachine)(payloadPaths)(rest)(objectBytes :: acc)

// Every partition's object, in partition order, each module optimized and emitted (and disposed)
// one after another. `resolveMachine` makes a fresh target machine per partition; `payloadPaths`
// are the bitcode payload files partition 0 links.
let emitPartitionModules (triple: Str) resolveMachine (payloadPaths: List(Str)) (partitions: List(PartitionModule)) = emitPartitionModulesFrom(triple)(resolveMachine)(payloadPaths)(partitions)([])

let recursive preparePartitionJobs (triple: Str) resolveMachine (partitions: List(PartitionModule)) (acc: List(PartitionJob)) =
    match partitions with
        | [] ->
            acc
            |> Ashes.Collection.List.reverse
            |> Ok
        | partition :: rest ->
            match preparePartitionJob(triple)(resolveMachine)(partition) with
                | Error(message) -> Error(message)
                | Ok(job) -> preparePartitionJobs(triple)(resolveMachine)(rest)(job :: acc)

let disposePartitionJob (job: PartitionJob) =
    match job with
        | PartitionJob { partitionJobContext = context, partitionJobModule = module_, partitionJobBuilder = builder, partitionJobMachine = machine } ->
            Unit
            |> (given (_) -> disposeTargetMachine(machine))
            |> (given (_) -> disposeBuilder(builder))
            |> (given (_) -> disposeModule(module_))
            |> (given (_) -> contextDispose(context))

let runPartitionPasses machine module_ =
    (let options = createPassBuilderOptions(Unit)
    in
        let _ = runPasses(module_)("default<O2>")(machine)(options)
        in disposePassBuilderOptions(options))

// The temporary directory of this run's partition child processes, under `tempRoot`.
let partitionWorkDirectory (tempRoot: Str) =
    tempRoot + "/ashes-partitions-" + Ashes.Text.fromInt(currentProcessId(Unit))

let partitionObjectPath (directory: Str) (index: Int) = directory + "/partition" + Ashes.Text.fromInt(index) + ".o"

let partitionDonePath (directory: Str) (index: Int) = directory + "/partition" + Ashes.Text.fromInt(index) + ".done"

let partitionErrorPath (directory: Str) (index: Int) = directory + "/partition" + Ashes.Text.fromInt(index) + ".error"

// The body of a partition's child process: verify and optimize the partition, link the payloads
// into partition 0, emit it into its object file, record the outcome in a `.done` or `.error`
// file, and end the process with `_exit`, so none of the parent's exit paths run in the child. The
// steps are the in-process path's in the same order. It reads only the partition's LLVM handles and
// builds a few strings: it never walks or releases the parent's Ashes heap, so the child keeps
// sharing those pages copy-on-write and only its own module's pages become private.
let runPartitionChild (directory: Str) (triple: Str) (payloadPaths: List(Str)) (job: PartitionJob) =
    match job with
        | PartitionJob { partitionJobIndex = index, partitionJobContext = context, partitionJobModule = module_, partitionJobMachine = machine } ->
            let _ = reportPartitionPhase(partitionText(index) + " child started")
            in
                let outcome =
                    match verifyModule(module_)(verifierReturnStatusAction) with
                        | (true, report) ->
                            report
                            |> verificationText(index)
                            |> Error
                        | (false, _) ->
                            let _ = reportPartitionPhase(partitionText(index) + " child verified")
                            in
                                let _ = runPartitionPasses(machine)(module_)
                                in
                                    let _ = reportPartitionPhase(partitionText(index) + " child optimized")
                                    in
                                        let paths =
                                            if index == 0
                                            then payloadPaths
                                            else []
                                        in
                                            match linkPartitionPayloads(context)(module_)(triple)(paths) with
                                                | Error(message) -> Error(partitionText(index) + ": " + message)
                                                | Ok(_) ->
                                                    let _ = reportPartitionPhase(partitionText(index) + " child payloads linked")
                                                    in
                                                        match targetMachineEmitToFile(machine)(module_)(partitionObjectPath(directory)(index))(objectFileType) with
                                                            | (true, _) -> Error(partitionText(index) + ": LLVM could not emit the object file.")
                                                            | (false, _) -> Ok(Unit)
                in
                    let _ =
                        match outcome with
                            | Ok(_) ->
                                Ashes.IO.File.writeText(partitionDonePath(directory)(index))("")
                            | Error(message) ->
                                Ashes.IO.File.writeText(partitionErrorPath(directory)(index))(message)
                    in
                        let _ = reportPartitionPhase(partitionText(index) + " child emitted")
                        in exitChildProcess(0)

// A started partition child: its process id and the partition, whose handles the parent disposes
// once the child is reaped.
type PartitionChild =
    | childProcessId: Int
    | childJob: PartitionJob

let partitionIndexOf (job: PartitionJob) =
    match job with
        | PartitionJob { partitionJobIndex = index } -> index

// Forks the child for `job`: the child runs `runPartitionChild` and never returns.
let startPartitionChild (directory: Str) (triple: Str) (payloadPaths: List(Str)) (job: PartitionJob) =
    (let processId = forkProcess(Unit)
    in
        if processId == 0
        then
            let _ = runPartitionChild(directory)(triple)(payloadPaths)(job)
            in Error("a partition child process returned")
        else
            if processId < 0
            then
                Error(partitionText(partitionIndexOf(job)) + ": the child process could not be started.")
            else Ok(PartitionChild(childProcessId = processId, childJob = job)))

let recursive runningCount (running: List(PartitionChild)) (count: Int) =
    match running with
        | [] -> count
        | _ :: rest -> runningCount(rest)(count + 1)

let recursive childJobOf (processId: Int) (running: List(PartitionChild)) =
    match running with
        | [] -> None
        | PartitionChild { childProcessId = candidate, childJob = job } :: rest ->
            if candidate == processId
            then Some(job)
            else childJobOf(processId)(rest)

let recursive withoutChild (processId: Int) (running: List(PartitionChild)) =
    match running with
        | [] -> []
        | child :: rest ->
            match child with
                | PartitionChild { childProcessId = candidate } ->
                    if candidate == processId
                    then rest
                    else child :: withoutChild(processId)(rest)

// While nothing failed, starts a child for the next pending partition as long as fewer than `limit`
// run; otherwise reaps the next child to exit and disposes the parent's copy of its partition; with
// nothing left running, stops. After a failure no further child starts, but every running one is
// still reaped. Answers the first failure.
let recursive drivePartitionChildren (directory: Str) (limit: Int) (triple: Str) (payloadPaths: List(Str)) (pending: List(PartitionJob)) (running: List(PartitionChild)) (failure: Maybe(Str)) =
    match (failure, pending) with
        | (None, job :: rest) when runningCount(running)(0) < limit ->
            match startPartitionChild(directory)(triple)(payloadPaths)(job) with
                | Error(message) -> drivePartitionChildren(directory)(limit)(triple)(payloadPaths)(rest)(running)(Some(message))
                | Ok(child) ->
                    let _ =
                        reportPartitionPhase(partitionText(partitionIndexOf(job)) + " forked")
                    in drivePartitionChildren(directory)(limit)(triple)(payloadPaths)(rest)(child :: running)(failure)
        | _ ->
            match running with
                | [] -> failure
                | _ ->
                    let processId = waitForAnyChild(Unit)
                    in
                        if processId < 0
                        then Some("waiting for a partition child process failed")
                        else
                            let _ =
                                match childJobOf(processId)(running) with
                                    | Some(job) ->
                                        let _ =
                                            reportPartitionPhase(partitionText(partitionIndexOf(job)) + " reaped")
                                        in
                                            let _ = disposePartitionJob(job)
                                            in
                                                reportPartitionPhase(partitionText(partitionIndexOf(job)) + " disposed")
                                    | None -> Unit
                            in
                                drivePartitionChildren(directory)(limit)(triple)(payloadPaths)(pending)(withoutChild(processId)(running))(failure)

// The object file a child wrote, read through an LLVM memory buffer and copied into managed bytes
// as the in-process path copies its emitted buffer.
let readPartitionObjectFile (index: Int) (path: Str) =
    match createMemoryBufferWithContentsOfFile(path) with
        | (false, Some(buffer), _message) ->
            let copied =
                buffer
                |> getBufferSize
                |> Ashes.Ffi.copyBytes(getBufferStart(buffer))
            in
                let _ = disposeMemoryBuffer(buffer)
                in
                    match copied with
                        | Ok(objectBytes) -> Ok(objectBytes)
                        | Error(message) -> Error(partitionText(index) + ": its object could not be copied: " + message)
        | _ -> Error(partitionText(index) + ": its object file " + path + " could not be read.")

// A reaped child's object, or the reason it has none: its error file, or that it died first.
let childPartitionObject (directory: Str) (index: Int) =
    match index
    |> partitionDonePath(directory)
    |> Ashes.IO.File.exists with
        | Ok(true) ->
            index
            |> partitionObjectPath(directory)
            |> readPartitionObjectFile(index)
        | _ ->
            match index
            |> partitionErrorPath(directory)
            |> Ashes.IO.File.readText with
                | Ok(message) -> Error(message)
                | Error(_) -> Error(partitionText(index) + ": its child process ended without an object.")

let recursive collectChildObjects (directory: Str) (jobs: List(PartitionJob)) (acc: List(Bytes)) =
    match jobs with
        | [] ->
            acc
            |> Ashes.Collection.List.reverse
            |> Ok
        | job :: rest ->
            match job
            |> partitionIndexOf
            |> childPartitionObject(directory) with
                | Error(message) -> Error(message)
                | Ok(objectBytes) -> collectChildObjects(directory)(rest)(objectBytes :: acc)

// Every partition's object in partition order, each verified, optimized and emitted by a child
// process, at most `processes` at once, partition 0 with the payloads linked in. Each child works
// on the same module the in-process path would, with the same target machine, pass pipeline and
// payload step, so the objects are the in-process path's byte for byte and are merged in the same
// order. Every child is reaped and the temporary directory under `tempRoot` removed before this
// returns, whatever the outcome.
let emitPartitionModulesInProcesses (processes: Int) (tempRoot: Str) (triple: Str) resolveMachine (payloadPaths: List(Str)) (partitions: List(PartitionModule)) =
    match preparePartitionJobs(triple)(resolveMachine)(partitions)([]) with
        | Error(message) -> Error(message)
        | Ok(jobs) ->
            let directory = partitionWorkDirectory(tempRoot)
            in
                match Ashes.IO.Directory.createAll(directory) with
                    | Error(message) -> Error("Could not create the partition directory " + directory + ": " + message)
                    | Ok(_) ->
                        let objects =
                            match drivePartitionChildren(directory)(processes)(triple)(payloadPaths)(jobs)([])(None) with
                                | Some(message) -> Error(message)
                                | None ->
                                    let _ = reportPartitionPhase("partition children done")
                                    in collectChildObjects(directory)(jobs)([])
                        in
                            let _ = reportPartitionPhase("partition objects read")
                            in
                                let _ = Ashes.IO.Directory.removeTree(directory)
                                in objects

let mergePartitionObjects (objects: Result(Str, List(Bytes))) =
    match objects with
        | Error(message) -> Error(message)
        | Ok(parts) ->
            let _ = reportPartitionPhase("partition merge started")
            in
                let merged = mergeElfRelocatableObjects(parts)
                in
                    let _ = reportPartitionPhase("partition merge done")
                    in merged

// The merged relocatable object of the partition modules, built in this process one partition
// after another (`processes` 1), or with up to `processes` partitions at once in child processes.
let mergePartitionModulesWith (processes: Int) (tempRoot: Str) (triple: Str) resolveMachine (payloadPaths: List(Str)) (partitions: List(PartitionModule)) =
    if processes > 1
    then
        partitions
        |> emitPartitionModulesInProcesses(processes)(tempRoot)(triple)(resolveMachine)(payloadPaths)
        |> mergePartitionObjects
    else
        partitions
        |> emitPartitionModules(triple)(resolveMachine)(payloadPaths)
        |> mergePartitionObjects

// The merged relocatable object of the partition modules, built one after another in this process.
let mergePartitionModules (triple: Str) resolveMachine (payloadPaths: List(Str)) (partitions: List(PartitionModule)) = mergePartitionModulesWith(1)("")(triple)(resolveMachine)(payloadPaths)(partitions)

// Every partition's object, partition 0 first, for `partitions` partitions of `program`.
let emitPartitionObjects (leakCheck: Bool) (entryName: Str) (triple: Str) resolveMachine (payloadPaths: List(Str)) (partitions: Int) (program: IrProgram) =
    program
    |> buildPartitionModules(leakCheck)(entryName)(partitions)
    |> emitPartitionModules(triple)(resolveMachine)(payloadPaths)

// The merged relocatable object of a partitioned build of `program`, with up to `processes`
// partitions built at once in child processes (1: one after another in this process).
let emitPartitionedObjectWith (processes: Int) (tempRoot: Str) (leakCheck: Bool) (entryName: Str) (triple: Str) resolveMachine (payloadPaths: List(Str)) (partitions: Int) (program: IrProgram) =
    program
    |> buildPartitionModules(leakCheck)(entryName)(partitions)
    |> mergePartitionModulesWith(processes)(tempRoot)(triple)(resolveMachine)(payloadPaths)

// The merged relocatable object of a partitioned build of `program`, one partition after another.
let emitPartitionedObject (leakCheck: Bool) (entryName: Str) (triple: Str) resolveMachine (payloadPaths: List(Str)) (partitions: Int) (program: IrProgram) = emitPartitionedObjectWith(1)("")(leakCheck)(entryName)(triple)(resolveMachine)(payloadPaths)(partitions)(program)
