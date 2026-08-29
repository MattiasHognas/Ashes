// The first genuinely IR-driven slice of the self-hosted backend: walks a REAL `IrFunction`
// produced by `AshesCompiler.Semantics` (via `AshesCompiler.Frontend.Parser.parseProgram` +
// `AshesCompiler.Semantics.CoreLowering.lowerCoreProgramWithSource`, the same pipeline
// `selfhost/tests/ir-program-parity` already trusts against stage 0) and drives
// `AshesCompiler.Backend.Llvm` from its actual instructions — not a human hand-simulating what
// codegen should produce, which is all every earlier test in this arc ever did.
//
// Boundary:
// - Deliberately covers only the instruction kinds a scalar-arithmetic entry function needs with
//   no top-level `let` (so no arena bracketing — see `simple_arith`'s own lowered IR, which is
//   exactly `LoadConstInt` x3, `MulInt`, `AddInt`, `Return`, nothing else): `LoadConstInt`,
//   `MulInt`, `AddInt`, `Return`. Anything else panics with a clear "unsupported" message rather
//   than silently miscompiling. Locals (`StoreLocal`/`LoadLocal`), arena bookkeeping
//   (`SaveArenaState`/`RestoreArenaState`/`ReclaimArenaChunks`), and every other instruction kind
//   are follow-up slices, not attempted here.
// - Every IR value is a full-width `i64` word (architecture.md: "every value is an i64 word"), so
//   a temp environment is just `List((IrTemp, LLVMValueRef))` — no type-directed dispatch needed
//   for this instruction subset. `Ashes.Number.UInt.fromInt64` (added alongside this slice) is
//   what makes `LoadConstInt`'s dynamic `Int` payload usable with `constInt`'s `u64` parameter.
// - Builds a single `i64()` function (no parameters, no real process-entry ABI) representing the
//   entry function's computed value — proving the IR-to-LLVM composition, not yet the real linked
//   executable's actual entry contract.

import AshesCompiler.Semantics.Ir
import AshesCompiler.Semantics.IrInstructions
import AshesCompiler.Backend.Llvm
import Ashes.Number.UInt
export (
    value codegenEntryFunction,
)

let recursive lookupTemp temp env =
    match env with
        | [] -> Ashes.IO.panic("codegen: unknown temp t" + Ashes.Text.fromInt(temp))
        | (boundTemp, value) :: rest ->
            if boundTemp == temp
            then value
            else lookupTemp(temp)(rest)

let codegenInstructionKind builder i64 kind env =
    match kind with
        | LoadConstInt(target, value) -> (target, constInt(i64)(Ashes.Number.UInt.fromInt64(value))(true)) :: env
        | MulInt(target, left, right) -> (target, buildMul(builder)(lookupTemp(left)(env))(lookupTemp(right)(env))("t" + Ashes.Text.fromInt(target))) :: env
        | AddInt(target, left, right) -> (target, buildAdd(builder)(lookupTemp(left)(env))(lookupTemp(right)(env))("t" + Ashes.Text.fromInt(target))) :: env
        | Return(source) ->
            let _ = buildRet(builder)(lookupTemp(source)(env))
            in env
        | _ -> Ashes.IO.panic("codegen: unsupported IrInstructionKind for this minimal slice")

let recursive codegenInstructions builder i64 instructions env =
    match instructions with
        | [] -> env
        | instruction :: rest ->
            match instruction with
                | IrInstruction { instruction = kind } -> codegenInstructions(builder)(i64)(rest)(codegenInstructionKind(builder)(i64)(kind)(env))

// Builds `i64 <name>()` in a fresh module from `irFunction`'s real instructions and returns
// `(module_, builder)`, matching every other module builder's shape in `selfhost/tests/backend`
// so the same `emitModule` verification pipeline applies unchanged.
let codegenEntryFunction name context irFunction =
    (let module_ = createModule(name)(context)
    in
        let i64 = int64Type(context)
        in
            let functionValue = addFunction(module_)(name)(functionType(i64)([])(0u32)(false))
            in
                let entryBlock = appendBasicBlock(context)(functionValue)("entry")
                in
                    let builder = createBuilder(context)
                    in
                        let _ = positionBuilderAtEnd(builder)(entryBlock)
                        in
                            match irFunction with
                                | IrFunction { instructions = instructions } ->
                                    let _ = codegenInstructions(builder)(i64)(instructions)([])
                                    in (module_, builder))
