// The partition plan of partitioned code generation, stage 0's `ResolveParallelObjectPartitions`
// and `AssignParallelObjectPartitions`: how many modules a program's lifted functions are split
// into, and which module defines each one. Both depend on the program alone, so the image a
// partitioned build produces is the same on every machine.
import AshesCompiler.Semantics.Ir
import AshesCompiler.Semantics.IrInstructions
import Ashes.Collection.Map
export (
    value objectPartitionFunctionsPerPartition,
    value objectPartitionMaximum,
    value clampObjectPartitions,
    value sizedObjectPartitions,
    value requestedObjectPartitions,
    value defaultPartitionProcesses,
    value partitionProcessesFor,
    value assignObjectPartitions,
    value partitionOfLabel,
)

let objectPartitionFunctionsPerPartition = 1024

let objectPartitionMaximum = 16

let clampObjectPartitions (count: Int) =
    if count < 1
    then 1
    else
        if count > objectPartitionMaximum
        then objectPartitionMaximum
        else count

let recursive countFunctions (functions: List(IrFunction)) (count: Int) =
    match functions with
        | [] -> count
        | _ :: rest -> countFunctions(rest)(count + 1)

// One partition per `functionsPerPartition` lifted functions, at least one and at most the maximum.
let sizedObjectPartitions (functionsPerPartition: Int) (program: IrProgram) =
    match program with
        | IrProgram { functions = functions } -> clampObjectPartitions(countFunctions(functions)(0) / functionsPerPartition)

// The partition count a build asks for with stage 0's `ASHES_LLVM_JOBS`, the same rule as stage 0's
// `ResolveParallelObjectPartitions`: a number is that many partitions, clamped, so `1` is the
// single-module build; unset, empty, `auto` or anything else sizes the plan from the program, one
// partition per `objectPartitionFunctionsPerPartition` lifted functions. A program below twice that
// is one partition and takes the single-module path, which is every test-sized program. The answer
// depends on the setting and the program alone, never on the machine.
let requestedObjectPartitions (setting: Maybe(Str)) (program: IrProgram) =
    match setting with
        | Some(text) ->
            match Ashes.Text.parseInt(text) with
                | Ok(count) -> clampObjectPartitions(count)
                | Error(_) -> sizedObjectPartitions(objectPartitionFunctionsPerPartition)(program)
        | None -> sizedObjectPartitions(objectPartitionFunctionsPerPartition)(program)

// How many partitions are optimized and emitted at once with partitions on, in child processes.
// Unset, it is `defaultPartitionProcesses`, at most the number of online processors; a number of
// one or more is that many; anything else is 1. Either way it is at most the partition count, and 1
// builds the partitions one after another in the compiling process. A child shares the parent's
// memory copy-on-write and makes only its own module and LLVM working set private, about 0.5-1 GiB
// for a sixteenth of the compiler, so 6 keeps the self-compile near 23 GiB at worst. Only the
// concurrency depends on the machine: the objects, and so the image, do not.
let defaultPartitionProcesses = 6

let atMost (limit: Int) (value: Int) =
    if value > limit
    then limit
    else value

let partitionProcessesFor (setting: Maybe(Str)) (onlineProcessors: Int) (partitions: Int) =
    (let requested =
        match setting with
            | Some(text) ->
                match Ashes.Text.parseInt(text) with
                    | Ok(count) when count >= 1 -> count
                    | _ -> 1
            | None ->
                if onlineProcessors < 1
                then 1
                else atMost(onlineProcessors)(defaultPartitionProcesses)
    in atMost(partitions)(requested))

let recursive instructionCount (instructions: List(IrInstruction)) (count: Int) =
    match instructions with
        | [] -> count
        | _ :: rest -> instructionCount(rest)(count + 1)

let functionInstructionCount (function_: IrFunction) =
    match function_ with
        | IrFunction { instructions = instructions } -> instructionCount(instructions)(0)

let recursive totalInstructions (functions: List(IrFunction)) (total: Int) =
    match functions with
        | [] -> total
        | function_ :: rest -> totalInstructions(rest)(total + functionInstructionCount(function_))

// Contiguous ranges balanced on instruction count, the entry's count seeding the running total, so
// the functions lowered together (one source module's) stay together and keep their cross-calls
// inside one module.
let recursive assignRanges (functions: List(IrFunction)) (partitions: Int) (target: Int) (accumulated: Int) (partition: Int) (assignment: MapTree(Str, Int)) =
    match functions with
        | [] -> assignment
        | function_ :: rest ->
            match function_ with
                | IrFunction { label = label } ->
                    let current =
                        if accumulated >= target * (partition + 1) && partition < partitions - 1
                        then partition + 1
                        else partition
                    in
                        assignment
                        |> Ashes.Collection.Map.setStr(label)(current)
                        |> assignRanges(rest)(partitions)(target)(accumulated + functionInstructionCount(function_))(current)

// Every lifted function's label mapped to the partition that defines it.
let assignObjectPartitions (program: IrProgram) (partitions: Int) =
    match program with
        | IrProgram { entryFunction = entryFunction, functions = functions } ->
            let entryCount = functionInstructionCount(entryFunction)
            in
                let total = totalInstructions(functions)(entryCount)
                in assignRanges(functions)(partitions)((total + partitions - 1) / partitions)(entryCount)(0)(Ashes.Collection.Map.empty)

// The partition defining `label`, or -1 for a label the plan does not cover.
let partitionOfLabel (assignment: MapTree(Str, Int)) (label: Str) =
    match Ashes.Collection.Map.getStr(label)(assignment) with
        | Some(partition) -> partition
        | None -> -1
