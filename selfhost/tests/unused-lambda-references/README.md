# Unused-lambda reference coverage

`Tests.ash` contains the assertions. The small Bash generator inserts private
production helpers from `CoreLowering.ash` at its marker; it does not implement a
second reference collector. Run from the worktree root, including from fish:

```sh
bash selfhost/tests/unused-lambda-references/render.sh
```

Compile the generated `work/unused-lambda-references/ashes.json` with the stage-0
CLI at `--debug -O0`, through the agreed heavy-job wrapper and an 8G systemd scope
with `MemorySwapMax=0`. Canonically format `work/unused-lambda-references/Main.ash`
before compilation. Run the resulting binary in a 4G scope. Repeat compilation
with `env ASHES_RC_POISON=1`. Expected stdout and exit code are:

```text
unused lambda reference coverage passed
```

Exit code: `0`.

Coverage includes direct callees, call arguments, aliases, aggregate escapes,
closures, program results, qualified receivers, match guards, implementation
bodies, trait defaults, providers and explicit exports. Unknown expression forms
and module exports conservatively retain declarations. Eager initializers and
recursive declarations are retained. Separate assertions verify that the original
non-callee ownership-analysis mode still ignores direct callees, qualified
receivers and the additional top-level declaration bodies.

This validates the reference index and replay-item selection. Full discovery,
missing-evidence diagnostics and absence of provisional IR are covered separately
by `CoreOrderedInferenceTests.ash` against the actual Core lowerer.

Plain and poisoned native runs passed. Builds took 10.80s and 9.45s, with peak RSS
approximately 986 MiB and 1005 MiB; native checks took less than 0.01s. The Bash
extractor's canonically formatted output matched the previously compiled input
exactly. Evidence is under `work/unused-lambda-references/`.

## Reference-map lifetime

The optional native argument repeats production reference-map construction and
replay-item selection over a fixed AST containing both a retained and an omitted
lambda. Build two additional executables at
`work/unused-lambda-references/plateau-plain` and `plateau-poison`, using
`env ASHES_RC_LEAKCHECK=1` and
`env ASHES_RC_LEAKCHECK=1 ASHES_RC_POISON=1`, respectively. Use the same scoped
8G compilation described above. Then run, from fish or Bash:

```sh
bash selfhost/tests/unused-lambda-references/validate.sh
```

The validator scopes each native run to 4G, requires the assertion success output,
rejects missing or malformed counters, and compares live counts at 1000 and
10000 rounds. Both flavors passed with **10 → 10 live cells**. Runs took 0.01s
and 0.13s with approximately 22 MiB peak RSS. This establishes no per-round growth
for the extracted map lifetime; the fixed fixture count is not a claim that the
complete compiler is leak-free. Stage1-produced lifetime validation is separate.
