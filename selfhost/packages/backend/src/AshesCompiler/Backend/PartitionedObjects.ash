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
export (
    value optimizeAndEmitPartition,
    value emitPartitionedObject,
    value emitPartitionObjects,
    value buildPartitionModules,
    value mergePartitionModules,
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

let recursive buildPartitionModulesFrom (leakCheck: Bool) (entryName: Str) (assignment: MapTree(Str, Int)) (program: IrProgram) (index: Int) (count: Int) (acc: List(PartitionModule)) =
    if index >= count
    then Ashes.Collection.List.reverse(acc)
    else
        let context = contextCreate(Unit)
        in
            match codegenProgramPartition(leakCheck)(index)(assignment)(entryName)(context)(program) with
                | (module_, builder) -> buildPartitionModulesFrom(leakCheck)(entryName)(assignment)(program)(index + 1)(count)(PartitionModule(partitionModuleIndex = index, partitionModuleContext = context, partitionModuleRef = module_, partitionModuleBuilder = builder) :: acc)

// Every partition's module of `partitions` partitions of `program`, partition 0 first, each in its
// own context. This is the only step that reads the program: once it returns, a caller holding
// the program in a frame of its own can release it before the modules are optimized.
let buildPartitionModules (leakCheck: Bool) (entryName: Str) (partitions: Int) (program: IrProgram) =
    (let count = clampObjectPartitions(partitions)
    in
        buildPartitionModulesFrom(leakCheck)(entryName)(assignObjectPartitions(program)(count))(program)(0)(count)([]))

// Verifies, optimizes and emits one partition module with a fresh target machine, then disposes it.
let emitPartitionModule (triple: Str) resolveMachine (payloadPaths: List(Str)) (partition: PartitionModule) =
    match partition with
        | PartitionModule { partitionModuleIndex = index, partitionModuleContext = context, partitionModuleRef = module_, partitionModuleBuilder = builder } ->
            match resolveMachine(Unit) with
                | Error(message) -> Error(message)
                | Ok(machine) ->
                    let _ = setTarget(module_)(triple)
                    in
                        let _ = applyDataLayout(module_)(machine)
                        in
                            match verifyModule(module_)(verifierReturnStatusAction) with
                                | (true, report) ->
                                    let detail =
                                        match report with
                                            | Ok(Some(text)) -> text
                                            | _ -> "unknown error"
                                    in Error("LLVM module verification failed in " + partitionText(index) + ": " + detail)
                                | (false, _) ->
                                    let paths =
                                        if index == 0
                                        then payloadPaths
                                        else []
                                    in
                                        match optimizeAndEmitPartition(machine)(module_)(context)(builder)(triple)(paths) with
                                            | (true, objectBytes) -> Ok(objectBytes)
                                            | (false, errorText) -> Error(partitionText(index) + ": " + bytesText(errorText))

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

// The merged relocatable object of the partition modules.
let mergePartitionModules (triple: Str) resolveMachine (payloadPaths: List(Str)) (partitions: List(PartitionModule)) =
    match emitPartitionModules(triple)(resolveMachine)(payloadPaths)(partitions) with
        | Error(message) -> Error(message)
        | Ok(objects) -> mergeElfRelocatableObjects(objects)

// Every partition's object, partition 0 first, for `partitions` partitions of `program`.
let emitPartitionObjects (leakCheck: Bool) (entryName: Str) (triple: Str) resolveMachine (payloadPaths: List(Str)) (partitions: Int) (program: IrProgram) =
    program
    |> buildPartitionModules(leakCheck)(entryName)(partitions)
    |> emitPartitionModules(triple)(resolveMachine)(payloadPaths)

// The merged relocatable object of a partitioned build of `program`.
let emitPartitionedObject (leakCheck: Bool) (entryName: Str) (triple: Str) resolveMachine (payloadPaths: List(Str)) (partitions: Int) (program: IrProgram) =
    program
    |> buildPartitionModules(leakCheck)(entryName)(partitions)
    |> mergePartitionModules(triple)(resolveMachine)(payloadPaths)
