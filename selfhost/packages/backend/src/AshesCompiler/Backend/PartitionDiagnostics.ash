// Opt-in timing of partitioned code generation, under the same switch as the CLI's LLVM
// statistics: with `ASHES_DIAGNOSTIC_LLVM_STATS` set, each partition step is reported on standard
// error as `llvm-stats: <phase> <monotonic ms>`, from the parent and from each child process (a
// child's lines are single unbuffered writes, so they interleave whole). Without the switch nothing
// but the environment lookup happens.
import AshesCompiler.Backend.Llvm
export (
    value partitionStatsRequested,
    value reportPartitionPhase,
    value partitionModuleSizeText,
)

let partitionStatsRequested unit =
    match Ashes.IO.Environment.get("ASHES_DIAGNOSTIC_LLVM_STATS") with
        | Ok(Some(path)) when path != "" -> true
        | _ -> false

let reportPartitionPhase (phase: Str) =
    if partitionStatsRequested(Unit)
    then
        Ashes.IO.writeErrorLine("llvm-stats: " + phase + " " + Ashes.Text.fromInt(Ashes.IO.Console.monotonicMillis(Unit)))
    else Unit

let recursive countModuleInstructions (instruction: Int) (count: Int) =
    if instruction == 0
    then count
    else
        countModuleInstructions(previousInstructionAddress(instruction))(count + 1)

let recursive countModuleBlocks (block: Int) (count: Int) =
    if block == 0
    then count
    else
        count
        |> countModuleInstructions(lastInstructionAddress(block))
        |> countModuleBlocks(nextBlockAddress(block))

// `(defined functions, instructions)` of the module.
let recursive countModuleFunctions (function_: Int) (functions: Int) (instructions: Int) =
    if function_ == 0
    then (functions, instructions)
    else
        let first = firstBlockAddress(function_)
        in
            if first == 0
            then
                countModuleFunctions(nextFunctionAddress(function_))(functions)(instructions)
            else
                instructions
                |> countModuleBlocks(first)
                |> countModuleFunctions(nextFunctionAddress(function_))(functions + 1)

// `<defined functions> functions <instructions> instructions`, walking the module once; only
// called with the switch on.
let partitionModuleSizeText module_ =
    match countModuleFunctions(firstFunctionAddress(module_))(0)(0) with
        | (functions, instructions) -> Ashes.Text.fromInt(functions) + " functions " + Ashes.Text.fromInt(instructions) + " instructions"
