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

// The partition count a build asks for with stage 0's `ASHES_LLVM_JOBS`: unset, empty or not a
// number is the single-module build (1); `auto` sizes the plan from the program; a number is that
// many partitions, clamped. Unlike stage 0, which partitions a large program by default, partitioned
// code generation is opt-in here.
let requestedObjectPartitions (setting: Maybe(Str)) (program: IrProgram) =
    match setting with
        | None -> 1
        | Some("auto") -> sizedObjectPartitions(objectPartitionFunctionsPerPartition)(program)
        | Some(text) ->
            match Ashes.Text.parseInt(text) with
                | Ok(count) -> clampObjectPartitions(count)
                | Error(_) -> 1

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
