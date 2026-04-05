# Debug Support Implementation Plan

> **Design decisions** (from user answers):
> 1. **Platforms:** Both Linux and Windows
> 2. **Debugger:** Assume user installs GDB or LLDB (configurable via VS Code extension setting)
> 3. **Multi-file:** Yes, from the start
> 4. **Variable inspection:** Full recursive
> 5. **Extension delivery:** Integrate into existing VS Code extension
> 6. **Source mapping granularity:** Per-expression

---

## Phase 0 — Source Location Propagation Through IR (Per-Expression)

**Problem:** `IrInst` records carry no source location. `Lowering.cs` already
accesses spans via `GetSpan(expr)` → `AstSpans.GetOrDefault(expr)` and records
`HoverTypeInfo` for the LSP, but once IR instructions are emitted via
`_inst.Add(new IrInst.XXX(...))` all location data is lost.

### 0a. Define `SourceLocation` Record

- [ ] **File:** `Ashes.Semantics/Ir.cs`
- Add `public readonly record struct SourceLocation(string FilePath, int Line, int Column);`
- Add a mutable `SourceLocation? Location { get; set; }` property on the
  abstract `IrInst` base record (line 25). A mutable property avoids rewriting
  all 63 subclass constructors.

### 0b. Line/Column Conversion Utility

- [ ] **File:** New `Ashes.Frontend/SourceTextUtils.cs`
- The LSP already has `LspTextUtils.ToLineCharacter`, but it lives in
  `Ashes.Lsp` which Semantics cannot depend on.
- Extract the line-starts computation into Frontend (which both Semantics and
  LSP can reference):
  - `GetLineStarts(string text) → int[]`
  - `ToLineColumn(int[] lineStarts, int textLength, int position) → (int Line, int Column)` (1-based for DWARF)

### 0c. Tag IR Instructions with Source Locations During Lowering

- [ ] **File:** `Ashes.Semantics/Lowering.cs`
- Add fields: `_currentFilePath`, `_lineStarts`, `_sourceLength`.
- Initialize in `Lower()` from the source text and file path already available
  at the call site.
- Add helper: `private void Emit(IrInst inst, Expr source)` that sets
  `inst.Location` from `GetSpan(source)` and appends to `_inst`.
- Update all `_inst.Add(...)` call sites to use `Emit(...)`.

### 0d. Multi-File Source Locations

- [ ] **File:** `Ashes.Semantics/ProjectSupport.cs`
- `CombinedCompilationLayout` already tracks `EntryOffset` and `BodyStart`.
- Add `IReadOnlyList<(string FilePath, int StartOffset, int EndOffset)> ModuleOffsets`
  so that given an absolute position in the combined source we can determine
  which file it belongs to and compute file-relative line/column.
- `Tag()` in Lowering uses this mapping to set the correct `FilePath`.

---

## Phase 1 — Debug vs. Release Build Mode

### 1a. Backend Options

- [ ] **File:** `Ashes.Backend` (options type, e.g. `BackendCompileOptions`)
- Add property: `bool EmitDebugInfo { get; init; }`
- When true, the backend emits DWARF (Linux) or CodeView (Windows) debug
  metadata.

### 1b. CLI Flag

- [ ] **File:** `Ashes.Cli/Program.cs`
- Add `--debug` / `-g` flag to `compile` and `run` commands.
- When set: `EmitDebugInfo = true`, optimisation capped at `-O1` (or forced
  `-O0` if no explicit `-O`), print `Debug: yes` in compilation summary.

### 1c. Documentation

- [ ] **File:** `docs/COMPILER_CLI_SPEC.md`
- Document `--debug` / `-g`, its interaction with optimisation levels, and the
  debug metadata it enables.

---

## Phase 2 — LLVM Debug Info Emission

### 2a. Extend LLVM Interop — Debug Info Bindings

- [ ] **File:** `Ashes.Backend/Llvm/Interop/LlvmApi.cs`

Add opaque handle types `LlvmDIBuilderHandle`, `LlvmMetadataHandle` and
P/Invoke bindings for ~20 LLVM-C functions:

| Function | Purpose |
|----------|---------|
| `LLVMCreateDIBuilder` | Create DIBuilder |
| `LLVMDisposeDIBuilder` | Dispose |
| `LLVMDIBuilderFinalize` | Finalize after all functions emitted |
| `LLVMDIBuilderCreateCompileUnit` | Compile unit (file, language, producer) |
| `LLVMDIBuilderCreateFile` | Source file reference |
| `LLVMDIBuilderCreateFunction` | Function debug info |
| `LLVMDIBuilderCreateSubroutineType` | Function type |
| `LLVMDIBuilderCreateBasicType` | Primitive: Int, Float, Bool |
| `LLVMDIBuilderCreatePointerType` | Pointer type (closures, ADTs, strings) |
| `LLVMDIBuilderCreateStructType` | Composite type (ADT layout) |
| `LLVMDIBuilderCreateMemberType` | Struct member (tag, fields) |
| `LLVMDIBuilderCreateAutoVariable` | Local `let` binding |
| `LLVMDIBuilderInsertDeclareAtEnd` | Associate variable with storage |
| `LLVMDIBuilderCreateExpression` | Variable location expression |
| `LLVMSetSubprogram` | Attach debug subprogram to LLVM function |
| `LLVMSetCurrentDebugLocation2` | Set debug loc for IR builder |
| `LLVMCreateDebugLocation` | Create debug location |
| `LLVMAddModuleFlag` | Module metadata flags |
| `LLVMMetadataAsValue` | Convert metadata to value |

### 2b. Debug Info Emission in Code Generation

- [ ] **Files:** `LlvmCodegen.cs` and partial files

When `EmitDebugInfo == true`:

**Module-level setup (in `EmitProgramModule()`):**

1. Create `DIBuilder`.
2. Add module flags: `"Dwarf Version" = 5` (Linux) or `"CodeView" = 1`
   (Windows), `"Debug Info Version" = 3`.
3. Create `DICompileUnit` — language `DW_LANG_C99` (stand-in), producer
   `"Ashes Compiler"`, not optimised.
4. Create `DIFile` entries per source file (multi-file from day one via
   `ModuleOffsets`).

**Per-function setup:**

1. For each `IrFunction` create `DISubprogram` with name, linkage name, file +
   line from first instruction's `SourceLocation`, and a `DISubroutineType`.
2. Attach with `LLVMSetSubprogram`.

**Per-instruction debug locations:**

1. For each `IrInst` being lowered to LLVM IR, check `inst.Location`.
2. If present → `LLVMSetCurrentDebugLocation2(builder, LLVMCreateDebugLocation(...))`.
3. If absent → clear debug location to avoid stale mappings.

**Local variable debug info (`let` bindings):**

1. On `StoreLocal` corresponding to a `let`:
   - `DIAutoVariable` with binding name, type, and scope.
   - `LLVMDIBuilderInsertDeclareAtEnd` pointing to alloca for that slot.
2. Enables the debugger to show `x = 42` in the Variables pane.

**Type mapping for variable inspection:**

| Ashes Type | DWARF Type | Size |
|------------|-----------|------|
| `Int` | `DW_ATE_signed`, 64-bit | 8 bytes |
| `Float` | `DW_ATE_float`, 64-bit | 8 bytes |
| `Bool` | `DW_ATE_boolean`, 8-bit | 1 byte |
| `String` | Pointer to struct `{i64 length, i8[] data}` | 8 bytes (ptr) |
| `List<T>` | Pointer to struct `{i64 tag, i64 head, i64 tail}` | ADT tag 0=Nil 1=Cons |
| `Maybe<T>` | Pointer to struct `{i64 tag, i64 value}` | ADT tag 0=None 1=Some |
| `Result<T>` | Pointer to struct `{i64 tag, i64 value}` | ADT tag 0=Ok 1=Err |
| Closures | Pointer to struct `{i64 funcPtr, i64 envPtr}` | 16 bytes |
| Tuples | Pointer to struct `{i64 tag, i64 elem0, …}` | ADT encoding |
| User ADTs | Pointer to struct `{i64 tag, i64 field0, …}` | Per-constructor |

For full recursive inspection create `DICompositeType` (struct) with
`DIMemberType` entries; for recursive types (List) use forward declarations
completed after all types are defined.

**Finalization:** `LLVMDIBuilderFinalize` before `VerifyModule` / `EmitObjectCode`.

### 2c. Extend Linkers for Debug Sections

#### ELF (`LlvmImageLinkerElf.cs`)

- [ ] Currently extracts only `SHF_ALLOC` sections (`.text`, `.data`, …).
- Debug sections (`.debug_info`, `.debug_abbrev`, `.debug_line`, `.debug_str`,
  `.debug_ranges`, `.debug_line_str`, `.debug_addr`) have `SHF_ALLOC = 0`.
- **Changes:**
  1. When `EmitDebugInfo`, also collect non-ALLOC sections named `.debug_*`.
  2. Write them after loadable segments (no program headers needed).
  3. Add a section header table with entries for each debug section.
  4. Set `e_shoff`, `e_shnum`, `e_shstrndx`.
  5. Include `.shstrtab` for section name strings.
  6. Handle `.rela.debug_*` relocations (absolute refs into `.text`).

#### PE (`LlvmImageLinkerPe.cs`)

- [ ] **Recommendation:** Use DWARF on both platforms for consistency.
  Set LLVM target triple to `x86_64-pc-windows-gnu` (MinGW) instead of MSVC;
  this produces DWARF which GDB/LLDB on Windows understand.
- Alternatively, handle `.debug$S` / `.debug$T` COFF sections and add a PE
  debug directory entry. Full PDB generation is complex — defer to a later
  phase.

---

## Phase 3 — Debug Adapter Protocol (DAP) Server

### 3a. New Project: `Ashes.Dap`

- [ ] **Location:** `src/Ashes.Dap/`
- **Dependencies:** `Ashes.Backend`, `Ashes.Semantics`, system process
  management.

```
VS Code  ←─JSON/stdio─→  Ashes.Dap  ←─GDB-MI / LLDB─→  GDB/LLDB  ←─ptrace─→  Binary
```

### 3b. DAP Protocol Layer

- [ ] **Transport:** JSON messages over stdin/stdout.

**Requests to implement:**

| Request | Description |
|---------|-------------|
| `initialize` | Advertise capabilities |
| `launch` | Compile `.ash` with `--debug`, launch under debugger |
| `setBreakpoints` | Set breakpoints by file + line |
| `configurationDone` | Signal configuration complete |
| `threads` | Single-threaded for now |
| `stackTrace` | Call stack with source locations |
| `scopes` | Locals, Closure Env |
| `variables` | Variable names, types, values |
| `continue` | Resume |
| `next` | Step over |
| `stepIn` | Step into |
| `stepOut` | Step out |
| `evaluate` | Expression evaluation (future) |
| `disconnect` | Clean up |

**Events:**
`stopped`, `terminated`, `output`, `initialized`.

### 3c. Debugger Backend Abstraction

- [ ] Interface `IDebuggerBackend` with:
  - `GdbMiBackend` — talks GDB Machine Interface protocol.
  - `LldbBackend` — talks `lldb-mi` or LLDB command interface.
- Selection via launch configuration parameter `"debugger": "gdb" | "lldb"`.

**GDB-MI essentials:**

| Action | Command |
|--------|---------|
| Launch | `gdb -i=mi --args ./program` |
| Breakpoint | `-break-insert file.ash:42` |
| Continue | `-exec-continue` |
| Step | `-exec-next` / `-exec-step` / `-exec-finish` |
| Stack | `-stack-list-frames` |
| Variables | `-stack-list-variables --all-values` |

### 3d. Variable Inspection — Full Recursive

- [ ] **Primitives** (`Int`, `Float`, `Bool`): read directly.
- **Strings:** dereference pointer → read `length` at offset 0, bytes at
  offset 8. Display as `"hello"`.
- **Lists:** read ADT pointer → tag 0 (Nil) → `[]`; tag 1 (Cons) → read
  head offset 8, tail offset 16, recurse. Display as `[1, 2, 3]`.
- **ADTs** (Maybe, Result, user): tag → constructor name from type metadata →
  read fields recursively.
- **Closures:** display as `<closure>` with function label + env pointer.
- **Tuples:** read fields sequentially → `(a, b, c)`.

The DAP server maintains a **type metadata map** from a sidecar file emitted
during debug compilation (see 3e).

### 3e. Source Map / Metadata Sidecar

- [ ] During debug compilation emit a `.ash-debug` JSON file alongside the
  binary:

```json
{
  "version": 1,
  "files": ["main.ash", "utils.ash"],
  "functions": [
    { "label": "_start_main", "name": "main", "file": 0, "line": 1 },
    { "label": "lambda_0", "name": "map", "file": 1, "line": 5 }
  ],
  "types": {
    "List": { "constructors": [
      { "name": "Nil", "tag": 0, "fields": [] },
      { "name": "Cons", "tag": 1, "fields": ["head", "tail"] }
    ]}
  },
  "locals": [
    { "function": "_start_main", "name": "x", "slot": 0, "type": "Int", "line": 3 }
  ]
}
```

---

## Phase 4 — VS Code Extension Integration

### 4a. Debugger Contribution

- [ ] **File:** `vscode-extension/package.json`

Add to `contributes`:

```jsonc
"debuggers": [{
  "type": "ashes",
  "label": "Ashes Debug",
  "program": "./server/${rid}/Ashes.Dap",
  "languages": ["ashes"],
  "configurationAttributes": {
    "launch": {
      "required": ["program"],
      "properties": {
        "program":      { "type": "string", "description": "Path to .ash file or project" },
        "args":         { "type": "array",  "items": { "type": "string" } },
        "debugger":     { "type": "string", "enum": ["gdb", "lldb"], "default": "gdb" },
        "debuggerPath": { "type": "string", "description": "Path to gdb/lldb binary" }
      }
    }
  },
  "configurationSnippets": [{ "label": "Ashes: Launch", "body": {
    "type": "ashes", "request": "launch",
    "name": "Debug Ashes Program",
    "program": "${file}", "debugger": "gdb"
  }}],
  "initialConfigurations": [{ "type": "ashes", "request": "launch",
    "name": "Debug Ashes Program", "program": "${file}", "debugger": "gdb"
  }]
}],
"breakpoints": [{ "language": "ashes" }]
```

### 4b. Extension Activation

- [ ] **File:** `vscode-extension/src/extension.ts`
- Add DAP server acquisition (same pattern as LSP — download from releases or
  use bundled binary).
- Register `DebugAdapterDescriptorFactory` → `ExecutableDebugAdapterDescriptor`
  pointing to the platform DAP binary.

### 4c. Debug Configuration Provider

- [ ] **File:** New `vscode-extension/src/debugProvider.ts`
- `DebugConfigurationProvider`:
  - Auto-detect project vs. single-file.
  - Resolve `program` relative to workspace.
  - Validate chosen debugger is installed; show helpful install error.

### 4d. Keybindings

- [ ] **File:** `vscode-extension/package.json`
- **F5** → start debug session (`workbench.action.debug.start`).
- **Ctrl+F5** → run without debugging (`ashes.run`).
- Existing `ashes.run` remains for non-debug execution.

### 4e. Build Script

- [ ] **File:** `vscode-extension/package.json` scripts
- Add `build-dap-server` that publishes `Ashes.Dap` for `win-x64` and
  `linux-x64` (mirrors existing `build-server` for the LSP).

---

## Phase 5 — Future Enhancements

### 5a. Expression Evaluation at Breakpoints

- [ ] Compile Ashes expressions in the context of captured locals (similar to
  REPL). Return result via DAP `evaluate`.

### 5b. Conditional Breakpoints

- [ ] Pass `condition` through to GDB (`-break-insert -c "cond" file:line`).
  For Ashes-level conditions, use expression evaluator.

### 5c. GDB Pretty-Printers

- [ ] Python pretty-printers for Ashes types (lists, ADTs, strings) so raw GDB
  also shows human-readable values. Install alongside debug binary.

### 5d. Hot Reload

- [ ] Recompile changed function, patch in running process. Requires
  cooperative runtime support — very complex, long-term.

### 5e. CodeView / PDB for Windows

- [ ] Switch from DWARF-on-Windows to native CodeView + PDB for Visual Studio
  compatibility. Requires PDB stream writers or `llvm-pdbutil`.

---

## Dependency Graph

```
Phase 0 (source locations in IR)
    │
    ▼
Phase 1 (--debug flag)
    │
    ▼
Phase 2a (LLVM debug bindings) ──► 2b (debug emission) ──► 2c (linker debug sections)
                                                                │
                                                                ▼
                                                          Phase 3 (DAP server)
                                                                │
                                                                ▼
                                                          Phase 4 (VS Code extension)
                                                                │
                                                                ▼
                                                          Phase 5 (enhancements)
```

---

## New / Modified Files Summary

| File | Action | Phase |
|------|--------|-------|
| `src/Ashes.Frontend/SourceTextUtils.cs` | **New** | 0b |
| `src/Ashes.Semantics/Ir.cs` | Edit | 0a |
| `src/Ashes.Semantics/Lowering.cs` | Edit | 0c |
| `src/Ashes.Semantics/ProjectSupport.cs` | Edit | 0d |
| `src/Ashes.Backend` compile options | Edit | 1a |
| `src/Ashes.Cli/Program.cs` | Edit | 1b |
| `src/Ashes.Backend/Llvm/Interop/LlvmApi.cs` | Edit | 2a |
| `src/Ashes.Backend/Llvm/LlvmCodegen.cs` + partials | Edit | 2b |
| `src/Ashes.Backend/Llvm/LlvmImageLinkerElf.cs` | Edit | 2c |
| `src/Ashes.Backend/Llvm/LlvmImageLinkerPe.cs` | Edit | 2c |
| `src/Ashes.Dap/` (entire project) | **New** | 3 |
| `vscode-extension/package.json` | Edit | 4a, 4d, 4e |
| `vscode-extension/src/extension.ts` | Edit | 4b |
| `vscode-extension/src/debugProvider.ts` | **New** | 4c |
| `docs/COMPILER_CLI_SPEC.md` | Edit | 1c |
| `docs/ARCHITECTURE.md` | Edit | 2b |
| `docs/DEBUG_PLAN.md` | **This file** | — |
