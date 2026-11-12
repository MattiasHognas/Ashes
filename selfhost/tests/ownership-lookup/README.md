# Ownership lookup regression

Run `bash selfhost/tests/ownership-lookup/render.sh` from this worktree. It renders
an ignored project with the production module and an independent oracle copied
from commit e34238b6. Three oracle-local type names are renamed to avoid duplicate
nominal declarations; the ownership algorithm is unchanged.

Compile the emitted project with stage 0 plain/poisoned and stage 1 plain.
Run without arguments for eleven differential cases: unique and transitive handoffs,
two/three duplicate names, lexical and parameter shadowing, partial and excessive
application, unproductive cycles, retained parameters, and unknown callees.
Expected stdout: `passed`.

With `ASHES_RC_LEAKCHECK=1` at compilation, run `old100`, `old1000`,
`new100`, and `new1000`. Each call computes a fresh complete fixpoint over
one prebuilt immutable AST; output must be `done`. Compare live counts within
each implementation to detect retention across invocations. The historical oracle
is intentionally retained only in generated ignored files.

`MemoryCases.ash` is a standalone companion for the three compiler ownership
boundaries exposed by the index. Compile it with leak checking and run
`shared100`/`shared1000`, `binder100`/`binder1000`, and
`curry100`/`curry1000`. These create fresh nested optional map values, exercise
nullary constructor tests in the index builder, and retain a string across a
partial application and branch. Every mode prints `passed`; compare each pair's
live counts. Use the same source with stage 0 plain/poisoned and stage 1 plain.

`bash selfhost/tests/ownership-lookup/render.sh prebuilt` selects the equivalent
three-function AST fixture without invoking the parser. The default driver checks
that this fixture matches the parsed source structurally, ignoring source spans.
This isolates ownership inference from a separately reproduced stage-1 parser
returned-pair lifetime defect; it does not fix or excuse that defect.

With unchanged bootstrap17, the prebuilt driver has live counts 3/3 at 100/1000
fresh inferences for both the historical oracle and the index. Stage 0 with RC
poisoning has the same 3/3 counts. The exact span-wrapped fixture also has old/new counts 3/3 under unchanged
stage 1 and poisoned stage 0. This covers the original inference input and
justifies omitting the three stage-0 ownership-rule mirrors for this case. The parser-backed stage-0 driver is flat at 4/4.

`render.sh wrapped` selects the exact expression-wrapper and byte-span layout
emitted by stage 0 for the original source. The default driver checks both the
unwrapped semantic comparison and an exact recursive span/wrapper comparison.
The wrapped driver runs the same old/new paired inference measurements without
calling the stage-1 parser.
