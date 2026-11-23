// Opt-in development instrumentation; ordinary compilation never walks the module or reads the clock.
// With `ASHES_DIAGNOSTIC_LLVM_STATS` set to a path, the generated LLVM module is walked once after
// verification and before the optimization passes, and one line per defined function is written
// there: `<blocks> <instructions> <condBr> <switch> <call> <alloca> <load> <store> <phi> <name>`,
// then a `total` line. Each compile phase boundary is also reported on standard error as
// `llvm-stats: <phase> <monotonic ms>`, so a resident-size sampler can be lined up with the phases.
import AshesCompiler.Backend.Llvm
export (
    value writeDiagnosticLlvmStats,
    value reportDiagnosticPhase,
)

type FunctionCounts =
    | blocks: Int
    | instructions: Int
    | condBranches: Int
    | switches: Int
    | calls: Int
    | allocas: Int
    | loads: Int
    | stores: Int
    | phis: Int

let emptyCounts = FunctionCounts(blocks = 0, instructions = 0, condBranches = 0, switches = 0, calls = 0, allocas = 0, loads = 0, stores = 0, phis = 0)

let oneIf (condition: Bool) =
    if condition
    then 1
    else 0

// `LLVMOpcode` values: LLVMSwitch 3, LLVMAlloca 26, LLVMLoad 27, LLVMStore 28, LLVMPHI 44,
// LLVMCall 45, LLVMCondBr 71.
let countInstruction (counts: FunctionCounts) (opcode: u32) =
    match counts with
        | FunctionCounts { blocks = blocks, instructions = instructions, condBranches = condBranches, switches = switches, calls = calls, allocas = allocas, loads = loads, stores = stores, phis = phis } ->
            FunctionCounts(
                blocks = blocks,
                instructions = instructions + 1,
                condBranches = condBranches + oneIf(opcode == 71u32),
                switches = switches + oneIf(opcode == 3u32),
                calls = calls + oneIf(opcode == 45u32),
                allocas = allocas + oneIf(opcode == 26u32),
                loads = loads + oneIf(opcode == 27u32),
                stores = stores + oneIf(opcode == 28u32),
                phis = phis + oneIf(opcode == 44u32)
            )

let countBlock (counts: FunctionCounts) =
    match counts with
        | FunctionCounts { blocks = blocks, instructions = instructions, condBranches = condBranches, switches = switches, calls = calls, allocas = allocas, loads = loads, stores = stores, phis = phis } -> FunctionCounts(blocks = blocks + 1, instructions = instructions, condBranches = condBranches, switches = switches, calls = calls, allocas = allocas, loads = loads, stores = stores, phis = phis)

let addCounts (left: FunctionCounts) (right: FunctionCounts) =
    match (left, right) with
        | (FunctionCounts { blocks = b1, instructions = i1, condBranches = c1, switches = s1, calls = k1, allocas = a1, loads = l1, stores = t1, phis = p1 }, FunctionCounts { blocks = b2, instructions = i2, condBranches = c2, switches = s2, calls = k2, allocas = a2, loads = l2, stores = t2, phis = p2 }) -> FunctionCounts(blocks = b1 + b2, instructions = i1 + i2, condBranches = c1 + c2, switches = s1 + s2, calls = k1 + k2, allocas = a1 + a2, loads = l1 + l2, stores = t1 + t2, phis = p1 + p2)

let recursive countInstructions (instruction: Int) (counts: FunctionCounts) =
    if instruction == 0
    then counts
    else
        instruction
        |> instructionOpcodeAt
        |> countInstruction(counts)
        |> countInstructions(previousInstructionAddress(instruction))

let recursive countBlocks (block: Int) (counts: FunctionCounts) =
    if block == 0
    then counts
    else
        counts
        |> countBlock
        |> countInstructions(lastInstructionAddress(block))
        |> countBlocks(nextBlockAddress(block))

let countsLine (counts: FunctionCounts) (name: Str) =
    match counts with
        | FunctionCounts { blocks = blocks, instructions = instructions, condBranches = condBranches, switches = switches, calls = calls, allocas = allocas, loads = loads, stores = stores, phis = phis } -> Ashes.Text.fromInt(blocks) + " " + Ashes.Text.fromInt(instructions) + " " + Ashes.Text.fromInt(condBranches) + " " + Ashes.Text.fromInt(switches) + " " + Ashes.Text.fromInt(calls) + " " + Ashes.Text.fromInt(allocas) + " " + Ashes.Text.fromInt(loads) + " " + Ashes.Text.fromInt(stores) + " " + Ashes.Text.fromInt(phis) + " " + name

let functionName (function_: Int) =
    match valueNameAt(function_) with
        | Ok(name) -> name
        | Error(_message) -> "?"

// The lines of every defined function, last function first, and the module total.
let recursive functionLines (function_: Int) (lines: List(Str)) (total: FunctionCounts) =
    if function_ == 0
    then (lines, total)
    else
        let counts =
            countBlocks(firstBlockAddress(function_))(emptyCounts)
        in
            if counts.blocks == 0
            then
                functionLines(nextFunctionAddress(function_))(lines)(total)
            else
                counts
                |> addCounts(total)
                |> functionLines(nextFunctionAddress(function_))(countsLine(counts)(functionName(function_)) :: lines)

let reportDiagnosticPhase (phase: Str) =
    match Ashes.IO.Environment.get("ASHES_DIAGNOSTIC_LLVM_STATS") with
        | Ok(Some(path)) when path != "" ->
            Ashes.IO.writeErrorLine("llvm-stats: " + phase + " " + Ashes.Text.fromInt(Ashes.IO.Console.monotonicMillis(Unit)))
        | _ -> Unit

let writeDiagnosticLlvmStats module_ =
    match Ashes.IO.Environment.get("ASHES_DIAGNOSTIC_LLVM_STATS") with
        | Ok(Some(path)) when path != "" ->
            match functionLines(firstFunctionAddress(module_))([])(emptyCounts) with
                | (lines, total) ->
                    match Ashes.IO.File.writeText(path)(Ashes.Text.join("\n")(lines) + "\n" + countsLine(total)("total") + "\n") with
                        | Ok(_) -> reportDiagnosticPhase("stats-written")
                        | Error(message) -> Ashes.IO.writeErrorLine("Could not write LLVM statistics " + path + ": " + message)
        | _ -> Unit
