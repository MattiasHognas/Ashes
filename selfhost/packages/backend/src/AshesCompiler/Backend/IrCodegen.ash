// The first genuinely IR-driven slice of the self-hosted backend: walks a REAL `IrFunction`
// produced by `AshesCompiler.Semantics` (via `AshesCompiler.Frontend.Parser.parseProgram` +
// `AshesCompiler.Semantics.CoreLowering.lowerCoreProgramWithSource`, the same pipeline
// `selfhost/tests/ir-program-parity` already trusts against stage 0) and drives
// `AshesCompiler.Backend.Llvm` from its actual instructions — not a human hand-simulating what
// codegen should produce, which is all every earlier test in this arc ever did.
//
// Boundary:
// - Covers `LoadConstInt`, `MulInt`, `AddInt`, `StoreLocal`, `LoadLocal`, and `Return` — enough
//   for `simple_arith` and `let_bindings`, `selfhost/tests/ir-program-parity`'s own two trusted
//   scalar fixtures. `SaveArenaState`/`RestoreArenaState`/`ReclaimArenaChunks` (emitted around
//   every top-level `let` scope, even a provably-scalar one — see `let_bindings`'s own lowered IR)
//   are treated as genuine no-ops: real scoped-arena codegen is a separate, much bigger slice this
//   one deliberately does not attempt, so these are explicitly ignored rather than silently
//   producing wrong code for a case they can't yet handle. Anything else panics with a clear
//   "unsupported" message. Closures, ADTs, strings, RC, and every other instruction kind remain
//   unstarted follow-up slices.
// - Every IR value is a full-width `i64` word (architecture.md: "every value is an i64 word"), so
//   a temp environment is just `List((IrTemp, LLVMValueRef))` — no type-directed dispatch needed
//   for this instruction subset. `Ashes.Number.UInt.fromInt64` (added alongside the first version
//   of this slice) is what makes `LoadConstInt`'s dynamic `Int` payload usable with `constInt`'s
//   `u64` parameter.
// - Locals get one `buildAlloca`'d `i64` slot each, allocated up front from `IrFunction`'s own
//   `localCount` — "every temp and local is an entry-block slot" (architecture.md) — looked up by
//   index the same way temps are, in a separate, fixed (never-appended-to) environment: unlike the
//   temp environment, which local index maps to which alloca pointer never changes once the
//   function's slots are built, only the value stored at that pointer does.
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

let recursive lookupIndexed key env =
    match env with
        | [] -> Ashes.IO.panic("codegen: unknown index " + Ashes.Text.fromInt(key))
        | (boundKey, value) :: rest ->
            if boundKey == key
            then value
            else lookupIndexed(key)(rest)

// Allocates one `i64` slot per local, `0..count-1`, and returns the fixed `IrLocal -> LLVMValueRef`
// mapping every `StoreLocal`/`LoadLocal` in the function looks up by index.
let recursive allocateLocalSlots builder i64 count index =
    if index >= count
    then []
    else (index, buildAlloca(builder)(i64)("local" + Ashes.Text.fromInt(index))) :: allocateLocalSlots(builder)(i64)(count)(index + 1)

let codegenInstructionKind builder i64 localSlots kind tempEnv =
    match kind with
        | LoadConstInt(target, value) -> (target, constInt(i64)(Ashes.Number.UInt.fromInt64(value))(true)) :: tempEnv
        | MulInt(target, left, right) -> (target, buildMul(builder)(lookupIndexed(left)(tempEnv))(lookupIndexed(right)(tempEnv))("t" + Ashes.Text.fromInt(target))) :: tempEnv
        | AddInt(target, left, right) -> (target, buildAdd(builder)(lookupIndexed(left)(tempEnv))(lookupIndexed(right)(tempEnv))("t" + Ashes.Text.fromInt(target))) :: tempEnv
        | StoreLocal(slot, source) ->
            let _ = buildStore(builder)(lookupIndexed(source)(tempEnv))(lookupIndexed(slot)(localSlots))
            in tempEnv
        | LoadLocal(target, slot) -> (target, buildLoad(builder)(i64)(lookupIndexed(slot)(localSlots))("t" + Ashes.Text.fromInt(target))) :: tempEnv
        | SaveArenaState(_, _, _) -> tempEnv
        | RestoreArenaState(_, _, _, _) -> tempEnv
        | ReclaimArenaChunks(_, _, _) -> tempEnv
        | Return(source) ->
            let _ = buildRet(builder)(lookupIndexed(source)(tempEnv))
            in tempEnv
        | _ -> Ashes.IO.panic("codegen: unsupported IrInstructionKind for this minimal slice")

let recursive codegenInstructions builder i64 localSlots instructions tempEnv =
    match instructions with
        | [] -> tempEnv
        | instruction :: rest ->
            match instruction with
                | IrInstruction { instruction = kind } -> codegenInstructions(builder)(i64)(localSlots)(rest)(codegenInstructionKind(builder)(i64)(localSlots)(kind)(tempEnv))

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
                                | IrFunction { instructions = instructions, localCount = localCount } ->
                                    let localSlots = allocateLocalSlots(builder)(i64)(localCount)(0)
                                    in
                                        let _ = codegenInstructions(builder)(i64)(localSlots)(instructions)([])
                                        in (module_, builder))
