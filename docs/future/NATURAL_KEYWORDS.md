# Natural-Language Keywords — Status & Plan

**Status:** Planned (design converged; implementation not started).

Ashes is drifting toward a surface that reads as English: `let ... in`, `match ... with`,
`if/then/else`, and the planned effects keywords `effect` / `perform` / `handle` / `resume` /
`uses` (the `uses` row notation was chosen over `!` precisely because it "reads in English").
Against that bar, the keyword set contains exactly three abbreviations — `fun`, `rec`, `extern` —
and they are the only misfits. This document renames all three and states the principles that stop
the question from reopening.

## Principles

1. **Words for meaning, symbols for plumbing.** Keywords carry semantics and must be full English
   words. Structural tokens — `->`, `=`, `|`, `(...)`, `{...}`, `::` — are plumbing and stay
   symbolic. No arrow is ever replaced with a word (`yields`, `returns`, `to` are all rejected on
   this principle).
2. **No abbreviations, ever.** A keyword is written out in full: `recursive`, not `rec`;
   `external`, not `extern`. This also constrains future keywords (`effect`, never `eff`).

## The renames

| Today | Becomes | Reading |
|---|---|---|
| `fun x -> e` | `given x -> e` | "given x, e" |
| `let rec f = ... and g = ...` | `let recursive f = ... and g = ...` | "let recursive f ... and g" |
| `extern` / `extern type` | `external` / `external type` | — |

Everything else already passes and is **kept unchanged**: `let`, `in`, `and`, `if`, `then`,
`else`, `match`, `with`, `when`, `type`, `await`, `true`, `false`, `import ... as`, and the
planned `effect`, `perform`, `handle`, `resume`, `uses`, and `module` (inline modules) — all
full English words.

### Why `given` for `fun`

A lambda in natural English is "given x, produce x + 1", and that is how it reads at every
remaining use site:

```
let priceOf = given item -> Prices.lookup(item)
Ashes.List.map(given n -> n * 2)(xs)
```

Because parameter sugar (`let abs n = ...`) already absorbs named function definitions, `fun`
survives almost exclusively in anonymous inline lambdas (three occurrences in the whole standard
library at the time of writing) — exactly the position where "given a thing, do this" is the
natural phrase. The word is unused as an identifier anywhere in the stdlib, tests, or examples.

Alternatives considered and rejected:

- `takes` — reads well but begs a paired `gives`/`returns` that does not exist; the sentence
  feels unfinished.
- `function` — literal but bureaucratic; a noun labeling the construct rather than a word
  participating in the sentence.
- `with`, `where`, `when` — collide with existing meanings (match/handle arms, guards).
- `fn`, `\` — abbreviations/symbols; wrong direction under Principle 2.

### Why `recursive` is worth its length

`rec` cannot be dropped or inferred: under sequential (Model A) top-level scoping it is the
semantic opt-in for self-visibility, so the keyword is load-bearing. It is also the most-typed
keyword in the language (~80 occurrences in the stdlib alone; recursion is the only loop), which
is an argument in **both** directions — it costs the most keystrokes, and it buys the most
naturalness because readers see it most. `let recursive fold = ... and step = ...` keeps the
`and` pairing intact and reads as a sentence.

### `external`

Near-zero cost (rare keyword), same principle. Both `extern name : Type = "symbol"` and
`extern type Name` forms rename.

## Old spellings are permanently reserved

After the cutover, `fun`, `rec`, and `extern` do **not** revert to ordinary identifiers. If they
did, pre-rename code would silently reparse — `let rec f = ...` would become a let-binding named
`rec` with parameter `f` — and change meaning instead of erroring. Instead the three old spellings
stay reserved forever and produce a dedicated diagnostic:

- **`ASH021` — renamed keyword.** Message names the replacement, e.g.
  `'fun' was renamed to 'given'` / `'rec' was renamed to 'recursive'` /
  `'extern' was renamed to 'external'`. Next free code at the time of writing; confirm against
  `docs/DIAGNOSTICS.md` when implementing.

This makes every pre-rename program fail loudly with a fix-it-grade message, at the cost of three
identifier names nobody was using.

## Migration mechanics — two stages

Formatting is canonical in Ashes, which makes the corpus migration nearly free.

**Stage 1 — dual spelling, canonical output.** The lexer accepts both spellings for each keyword
(mapped to the same token kind); the formatter emits only the new spellings. Run `fmt -w` across
the entire `.ash` corpus — this auto-migrates every file. All docs and specs switch to the new
spellings in this stage.

**Stage 2 — cutover.** The old spellings stop being accepted and start producing `ASH021`. This
can land in the same release as Stage 1 (the repo is pre-1.0 and milestone-driven; the dual window
only needs to be long enough to reformat the world, which is one commit) — but Stage 1 must be a
separate commit so the corpus rewrite is mechanically reviewable apart from the behavior change.

## What changes, file by file

Spec first (Ground Rule 1), then Frontend outward. No Semantics, Backend, or IR changes — this is
purely lexical surface; the AST shapes are untouched and only token spellings move.

1. **`docs/LANGUAGE_SPEC.md`** — rename the keywords in the grammar, the keyword list, and every
   code sample. State the two principles above and the permanent reservation of the old spellings.
2. **`docs/DIAGNOSTICS.md`** — add the `ASH021` row (renamed keyword, one code for all three, the
   message carries the specific old/new pair).
3. **Other docs** — `docs/FORMATTER_SPEC.md` (canonical forms), `docs/STANDARD_LIBRARY.md`,
   `docs/TESTING.md`, `docs/PROJECT_SPEC.md`, `README.md`, and the code samples in the planned-work
   docs (the effects worked examples in `docs/LANGUAGE_SPEC.md` section 20 use `fun`;
   `docs/future/INLINE_MODULES.md` likewise if it shows lambdas).
4. **`src/Ashes.Frontend/Tokens.cs`** — rename the token kinds to match the surface:
   `TokenKind.Fun` → `TokenKind.Given`, `TokenKind.Rec` → `TokenKind.Recursive`,
   `TokenKind.Extern` → `TokenKind.External`.
5. **`src/Ashes.Frontend/Lexer.cs`** — in `GetIdentifierTokenKind`: Stage 1 maps both spellings to
   the renamed token kinds (`"given" or "fun" => TokenKind.Given`, ...); Stage 2 maps the old
   spellings to a form the parser rejects with `ASH021` (a dedicated `RenamedKeyword` token kind
   carrying the replacement text is the simplest route — the lexer stays diagnostic-free and the
   parser owns the error, matching the existing split).
6. **`src/Ashes.Frontend/Parser.cs`** — mechanical: follows the token-kind renames. No error-message
   text mentions the old spellings today (verified by grep), but re-verify, and add the `ASH021`
   emission for the reserved-spelling token. Check `Diagnostics.cs` for the new code constant.
7. **`src/Ashes.Formatter/Formatter.cs`** — the emission sites: the single `"fun ("` lambda
   rendering, the two `"let rec "` sites (top-level `RecGroup` and nested let-rec), the `"and "`
   continuation is unchanged, and the `"extern "` / `"extern type "` pair. Grep for the literal
   strings; these are the only places the formatter writes keywords from this set.
8. **`src/Ashes.Lsp/`** — nothing expected: completions are symbol-derived, not keyword-list-based
   (verified). Re-grep for the literal spellings to confirm hover/semantic-token paths are clean.
9. **`.ash` corpus** — `fmt -w` over `lib/`, `tests/`, `examples/`, and `challenges/` during
   Stage 1; refresh the shipped per-target copies under `dist/`. Check `tests/*.ash`
   `// expect-compile-error:` substrings for mentions of the old keywords.
10. **Tests** — lexer unit tests for the new spellings and the reserved-spelling path; `ASH021`
    end-to-end tests (one per renamed keyword, `// expect-compile-error:` on the rename message);
    formatter round-trip tests updated to the canonical new spellings.

## Conformance gates

- After Stage 1: `dotnet run --project src/Ashes.Cli -- fmt <path>` (check mode, no `-w`) over the
  whole corpus reports no pending changes, and the formatted output contains only new spellings.
- After Stage 2: a grep for `\bfun\b`, `\brec\b`, `\bextern\b` over all `.ash` files in the repo
  matches only the `ASH021` error fixtures; the three fixtures fail with the rename message;
  `scripts/verify.sh` is green.

## Open questions

- Whether `ASH021` should carry a machine-applicable fix (the LSP could offer the rewrite as a
  code action) or the message alone is enough given `fmt -w` handles migration in bulk.
- Whether the effects surface should land before or after this rename — if before, the effects
  worked examples are written with `fun` and migrate with the corpus; if after, they are written
  with `given` from the start. No technical coupling either way.
