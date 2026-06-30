# Inline Modules — Status & Design

**Status:** Planned (design proposed; implementation not started).

Today a module in Ashes is **a whole file**. The only way to give a group of related bindings a
namespace is to put them in their own `.ash` file and let the project's module-name → file-path
mapping pick them up (see [PROJECT_SPEC.md](../PROJECT_SPEC.md) §4.2). **Inline modules** lift that
one-module-per-file restriction: a file may declare nested, named modules directly in its body, so
related `let` / `type` declarations can be grouped under a qualifier without spawning a new file.

This is a pure **compile-time namespacing** feature. There is no runtime representation, no
first-class modules, no functors — modules are erased during lowering exactly as today. Ground Rules
5 (purity) and 6 (no GC) are untouched: an inline module is just a naming scope.

## Why

The file-per-module rule is the right default for libraries, but it is friction for small,
cohesive groupings:

- **Local cohesion.** A 40-line file that wants two or three internal namespaces (e.g. `Parser`
  and `Lexer` helpers in one tool) currently must either flatten everything into one flat scope —
  losing the qualifier that documents intent — or fragment into files that only ever talk to each
  other.
- **Avoiding name collisions without prefixing.** Instead of `parseExpr` / `parseStmt` /
  `lexToken`, group them: `Parse.expr`, `Parse.stmt`, `Lex.token`. The qualifier carries the
  prefix.
- **Progressive extraction.** Prototype a namespace inline; when it grows, promote it to its own
  file with **zero changes at the call sites** — `Geometry.area` resolves identically whether
  `Geometry` is an inline module or `Geometry.ash`. The path is the contract, not the file layout.
- **Grouping a type with its operations.** Keep a `type` and the functions over it under one
  qualifier in one file (the closest Ashes gets to "a type and its methods"), while staying purely
  functional.

## What it is *not*

To keep the feature aligned with the language's no-runtime, no-typeclass design:

- **Not first-class.** A module is not a value. You cannot bind one to a `let`, pass it to a
  function, or return it. There are no functors / parameterized modules.
- **Not a new visibility system.** Exports follow the existing file-module rules (below). There is
  no `pub`/`private` keyword introduced here.
- **Not runtime state.** A module has no fields, no instance, no initialization order beyond the
  existing sequential (Model A) scoping. It is erased before IR.

## Design at a glance (proposed decisions)

| Decision | Choice | Rationale |
|---|---|---|
| Introducer | `module Name =` | Mirrors `let name =` / `type Name =`; `module` is a new contextual keyword |
| Block delimiter | **Layout (column-based)**, no `end` — *but see Open Questions* | Ashes has no block terminators anywhere (`match`, `let … in`, top-level items are all layout/column-delimited); an `end` keyword would be foreign. The body is the run of declarations indented past the `module` keyword. **Caveat:** the Strategy-A text splitter is markedly simpler with an explicit terminator, so confirm this before coding |
| Members | `let`, `let rec … and …`, `type`, nested `module` | The same declaration forms a file may contain |
| Trailing expression | **Not allowed** inside a module | File modules already *ignore* their trailing expression (LANGUAGE_SPEC §13.1); an inline module is declarations only |
| `extern` | **Not allowed** inside a module | `extern` is a file-level FFI concern and is never exported anyway |
| Naming | `UpperCamel`, like type and module names | Distinguishes a module qualifier from a value binding at a glance |
| Identity | An inline module is an **exported submodule** of its file | `File.Inner.member` is path-addressable from other files, so inline ↔ file promotion is transparent |
| Scoping | Sequential (Model A), same as top level | A member sees earlier file bindings and earlier members; nothing sees it before its declaration |
| Reserved | Cannot be named `Ashes` (or any `Ashes.*`) | Consistent with PROJECT_SPEC §4.7 |
| Runtime cost | **Zero** — erased in lowering | Pure namespacing; mangled into qualified symbol names, exactly as file modules are today |

## Surface syntax

An inline module is `module Name =` followed by a layout block of declarations indented past the
`module` keyword. The block ends at the first line dedented back to (or past) the `module` column —
the same column rule the parser already uses to find the next top-level item.

```ash
import Ashes.IO

module Geometry =
    let pi = 3.14159
    let area r = pi *. r *. r
    let circumference r = 2.0 *. pi *. r

// back at file column — Geometry's block is closed here
Ashes.IO.print (Geometry.area 2.0)
```

### Grouping a type with its operations

```ash
module Stack =
    type Stack a =
        | Empty
        | Push a (Stack a)

    let empty = Empty

    let push x s = Push x s

    let pop s =
        match s with
        | Empty -> None
        | Push x rest -> Some (x, rest)
```

`Stack.Stack`, `Stack.empty`, `Stack.push`, and `Stack.pop` are all reachable by qualified access.
The constructors `Empty` / `Push` belong to `Stack.Stack` and are reached as `Stack.Empty` /
`Stack.Push`, mirroring how a `type` exported from a file behaves.

### Importing an inline module's names unqualified

The existing `import` machinery applies unchanged — an inline module is just another module path:

```ash
module Geometry =
    let pi = 3.14159
    let area r = pi *. r *. r

import Geometry            // names available unqualified below this point
import Geometry.area as a  // or select one binding

let _ = area 1.0           // == Geometry.area 1.0
let _ = a 2.0
```

Aliasing (`import Geometry as G` → `G.area`) and selector imports
(`import Geometry.area`, `import Geometry.Stack as S`) work exactly as for file modules
(LANGUAGE_SPEC §13.1).

### Nesting

Modules may nest; the path simply grows another segment:

```ash
module Json =
    module Parse =
        let value s = …
        let array s = …

    module Render =
        let value v = …

Json.Parse.value input
Json.Render.value out
```

### Cross-file: inline modules are real submodules

Because an inline module is an exported submodule of its file, another file reaches it through the
file's path. Given `Geom.ash`:

```ash
module Vec =
    let add (ax, ay) (bx, by) = (ax +. bx, ay +. by)
```

a sibling file imports it as a nested path:

```ash
import Geom.Vec
let _ = Vec.add (1.0, 2.0) (3.0, 4.0)
```

This is what makes inline → file promotion transparent: moving `module Vec` out into `Geom/Vec.ash`
leaves `import Geom.Vec` and every `Vec.add` call site unchanged.

### Selecting a single function out of a (nested) module

Because resolution is identical to a shipped module, a **selector import** can reach a binding that
lives inside an inline module — including a nested one — and alias it to a bare local name, exactly
like `import Ashes.String.substring as x`:

```ash
import Geom.Vec.add as addition   // module path Geom.Vec, binding add, alias addition

let _ = addition (1.0, 2.0) (3.0, 4.0)   // addition is now an ordinary function value
```

The only difference from `import Ashes.String.substring as x` is the depth of the path: the
compiler resolves the longest leading run of segments (`Geom.Vec`) as the module and treats the
final segment (`add`) as the selected member. Whether `Vec` is an inline module in `Geom.ash` or a
separate `Geom/Vec.ash` file does not change the import or the call site.

## Semantics

- **Scoping (Model A).** Inside a `module` block the same sequential rule as the top level holds: a
  declaration is visible to later declarations in the block and to nested blocks, never to earlier
  ones. Self-recursion needs `let rec`; mutual recursion needs `let rec … and …`. The module's
  members are visible *after* the block to the rest of the file via qualified access or `import`.
- **A module sees its enclosing scope.** Bindings declared in the file *before* the `module` block
  are in scope inside it (still Model A). Bindings after the block are not.
- **Exports.** Identical to file modules (LANGUAGE_SPEC §13.1 “Module Exports”): all top-level
  `let` bindings, all `let rec … and …` groups, all `type` declarations (with their constructors),
  and all nested `module`s are exported. There is no implicit re-export — what a module imports is
  not re-exported.
- **Resolution and collisions.** A module path resolves to **exactly one** module. If a path is
  satisfied by both an inline submodule and a file (e.g. `module Vec` inside `Geom.ash` *and* a file
  `Geom/Vec.ash`), that is a compile-time ambiguity error — same spirit as the existing
  multiple-root ambiguity rule (PROJECT_SPEC §4.2). The existing same-leaf-qualifier and
  same-unqualified-name import collision errors apply unchanged.
- **Reserved namespace.** A user inline module may not be named `Ashes`, and no inline path may
  shadow `Ashes.*` (PROJECT_SPEC §4.7).
- **Erasure.** Inline modules carry no runtime representation. During lowering they are flattened
  into the existing per-module symbol-mangling scheme, so codegen and linking are unaffected. There
  is no initialization order, no module value, no per-module storage.

## Diagnostics

New codes (next free range after `ASH016`):

| Code | When |
|------|------|
| `ASH017` | `module` block contains a disallowed form (trailing expression or `extern`) |
| `ASH018` | Inline module path collides with a file module of the same path (ambiguous resolution) |
| `ASH019` | Inline module named `Ashes` or shadowing a reserved `Ashes.*` path |
| `ASH020` | Duplicate inline module name in the same scope |

Existing import/export diagnostics (`ASH013`–`ASH016`) are reused as-is for unknown-member, unknown
selector, and import-collision cases — inline modules go through the **same** resolution path, so
they get the same messages for free.

## Implementation reality — how the module system actually works

Before listing tasks, the load-bearing fact that dictates the whole approach: **Ashes' module
system is not an AST or type-checker feature. It is a text-stitching layer that runs *before*
parsing.** Verified in the source:

- **`import` lines never reach the parser.** They are stripped by a regex
  (`ProjectSupport.ImportModulePattern` / the compiled `ImportLine`) in
  `ProjectSupport.ParseImportHeader`, on *every* path — single-file (`BuildStandaloneCompilationLayout`,
  called from `Ashes.Cli/Program.cs:149` and `Ashes.TestRunner/Runner.cs:753`) and project mode
  (`BuildCompilationLayout`). `Ashes.Frontend/Parser.cs:58` says so explicitly: *"imports are
  stripped upstream."* The AST has **no** `Import` node.
- **Modules are combined as source text.** `ProjectSupport.cs` (~2200 lines) shapes each module's
  text into binding fragments (`ShapeModuleSource` → `TryShapeFlatModule` / `ExtractTopLevelBindings`),
  mangles qualified names into flat symbols (`SanitizeModuleBindingName`, which is literally
  `moduleName.Replace('.', '_')`, plus `BuildModuleBindingPrefix`), rewrites qualified references,
  and concatenates everything into one combined source (`BuildCompilationLayoutCore` /
  `BuildEntryExpression`) that is then parsed and lowered as a single program.
- **The AST top level is flat.** `Ashes.Frontend/Ast.cs` has `TopLevelItem.{Type, Extern, LetDecl,
  RecGroup}` and a trailing `Program.Body`. There is no module node. The parser delimits top-level
  items by source column (`_topLevelDeclColumn`, `StartsNextTopLevelItem` in `Parser.cs`).

Consequence: `Geom.Vec.add` already becomes the flat symbol `Geom_Vec_add` once `Geom.Vec` is a
module name. So **the cheapest correct implementation does not touch the parser, AST, type checker,
or backend at all** — it teaches `ProjectSupport` to manufacture `Geom.Vec` as a module out of an
inline block.

### Two strategies

**Strategy A — lift inline blocks into synthetic modules in `ProjectSupport` (recommended first).**
Before shaping/combination, scan each file's source for `module Name =` blocks, cut each block out,
and register it as an additional `ProjectModule` whose `ModuleName` is `<EnclosingModule>.<Name>`
(recursively for nesting). Everything downstream is unchanged: mangling, qualified-reference
rewriting, whole-module/alias/selector imports, and the `import Geom.Vec.add as addition` path all
work because `Geom.Vec` is now just another module name. **No Frontend, AST, type-checker, or
Backend changes.** Cost is concentrated in `ProjectSupport.cs` plus a block splitter.

- *Risk:* the splitter operates on text. Layout/column delimiting (no `end`) is doable but fiddly at
  the text level — it must find the run of lines indented past the `module` keyword. This is the one
  place where the "no `end` keyword" surface decision has a real implementation cost: an explicit
  terminator would make the splitter trivial and robust. **Decision to confirm before coding**
  (see Open Questions). A middle path: split using the *real* lexer to find the column-delimited
  block extent, rather than a regex.

**Strategy B — a first-class AST/parser construct.** Add `TopLevelItem.Module` to `Ast.cs`, parse
the layout block in `Parser.cs` (reusing the column machinery), format it in `Formatter.cs`, and
make `ProjectSupport` consume module items structurally instead of by text. Cleaner long-term and
gives the formatter/LSP real structure, but it touches Frontend + Formatter + the shaping code and
risks divergence between the structured path and the still-text-based combination path.

**Recommendation:** ship Strategy A first (smallest, lowest-risk, reuses all mangling), then
consider promoting to Strategy B if nesting/formatting fidelity demands it.

## What has to change — implementation tasks (Strategy A)

### 1. Spec (do first — Ground Rule 1)
- [ ] Add an "Inline Modules" subsection to `LANGUAGE_SPEC.md` §13: the `module Name =` form, block
      delimiting (per the confirmed Open Question), members allowed, Model A scoping, purely
      compile-time.
- [ ] `PROJECT_SPEC.md` §4: a module path may now be satisfied by an inline submodule; define the
      inline-vs-file collision rule (`ASH018`).

### 2. Semantics — `ProjectSupport.cs` (the bulk of the work)
- [ ] **Block splitter:** add a helper that, given a file's source, extracts `module Name =` blocks
      (recursively) and returns the outer source minus those blocks plus a list of
      `(ComposedModuleName, BlockSource)`. Prefer driving it off the lexer's column information over
      a bare regex, so it matches the parser's `StartsNextTopLevelItem` boundary rule exactly.
- [ ] **Register synthetic modules:** in `BuildCompilationPlan` / the standalone path, feed each
      lifted block in as a `ProjectModule { ModuleName = "<File>.<Name>", ... }` so the existing
      ordering, `ShapeModuleSource`, `SanitizeModuleBindingName`, `BuildModuleBindingPrefix`, and
      `BuildEntryExpression` pick them up unchanged.
- [ ] **Same-file references:** ensure a file's own trailing code can reference its inline module
      (`Geom.Vec.add`) and that `import Geom.Vec[...]` works *within the same file* — single-file
      mode strips imports via `ParseImportHeader`, so confirm a same-file inline module is on the
      module list before selector resolution runs.
- [ ] **Reserved / collision / duplicate checks:** reject `module Ashes` (`ASH019`),
      inline-vs-file path collisions (`ASH018`), duplicate inline names in one scope (`ASH020`), and
      a disallowed `extern`/trailing-expression inside a block (`ASH017`).
- [ ] **Exports:** a lifted module is exported under its composed path; verify no implicit
      re-export (already the case — `GetExportNames` is per-module).

### 3. Frontend (`Ashes.Frontend`)
- [ ] If the confirmed syntax needs the lexer to recognize `module` (e.g. to keep the splitter
      honest or for Strategy B), add it as a **contextual** keyword in `Lexer.cs`/`Tokens.cs` — must
      not break existing `module`-named identifiers outside declaration position. **Skip entirely if
      Strategy A's splitter is purely column-driven and needs no token.**

### 4. Backend (`Ashes.Backend`)
- [ ] **None.** Modules are erased into flat mangled symbols before IR; confirm with a test that the
      emitted symbol for `Geom.Vec.add` matches what a `Geom/Vec.ash` file produces.

### 5. Formatter (`Ashes.Formatter`)
- [ ] Strategy A leaves `module` blocks as ordinary source the formatter doesn't model — verify
      `fmt` is **idempotent** on a file containing an inline module (it formats the inner decls but
      preserves the `module Name =` line and indentation). If it mangles them (cf. the known
      selector-import `fmt` gap), either fix it or add a `// fmt-skip:` path and file a follow-up.
      Full fidelity is a Strategy B item.

### 6. Diagnostics
- [ ] Add `ASH017`–`ASH020` to `docs/DIAGNOSTICS.md` with messages and examples. Reuse
      `ASH013`–`ASH016` for unknown-member / unknown-selector / import-collision — lifted modules go
      through the identical selector resolution, so those come for free.

### 7. Tests & examples
- [ ] `tests/*.ash`: qualified access, unqualified `import`, alias, selector import
      (`import Geom.Vec.add as addition`), nesting, a type-with-operations module, cross-file
      `import File.Inner`, and Model A scoping (a member can't see a later one).
- [ ] Negative `// expect-compile-error:` tests for `ASH017`–`ASH020`.
- [ ] An `examples/` program (no test directives) — e.g. a small calculator with inline `Lex` /
      `Parse` modules — and the promotion test: move one inline module to its own file and assert
      identical output.
- [ ] `fmt … -w` every new `.ash`; verify no diffs (`scripts/verify.sh`).

### 8. Tooling (`Ashes.Lsp`)
- [ ] Hover/completion/go-to-definition over `Geom.Vec.add`. Because the LSP consumes the same
      `ProjectSupport` combination, qualified access should resolve once synthetic modules are
      registered; add coverage in `Ashes.Lsp.Tests`. (Precise spans inside a lifted block are a
      Strategy B concern.)

## Open questions

- **Block delimiter (decide before coding).** Layout/column (idiomatic, no new keyword) vs. an
  explicit terminator. Layout keeps the language consistent; an explicit terminator makes the
  Strategy-A text splitter trivial and unambiguous. Leaning toward driving the column-based splitter
  off the real lexer so we get layout *without* a regex — but if that proves fragile, an explicit
  terminator is the fallback.
- **Re-export sugar.** Should a file be able to *flatten* an inline module into its own exports
  (e.g. `export module Foo`)? Deferred — start with no re-export, matching file modules.
- **Same-file shadowing of a file module.** If `Geom.ash` declares `module Vec` and `Geom/Vec.ash`
  also exists, this design makes it an error (`ASH018`). An alternative is "inline wins"; the error
  is safer and can be relaxed later.
- **Private members.** No visibility modifiers in this pass; everything top-level in the block is
  exported, as with files. A future `pub`/`private` pass (or the Effects-era capability work) could
  add it uniformly across file and inline modules.
