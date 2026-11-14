# Type graph resolution identity

`bash selfhost/tests/type-graph-node-identity/render.sh` creates an ignored focused
project in `work/type-graph-node-identity`. Its `.ash` entry point calls the actual
registered `TypeGraphIndexTests` module, copied from the semantics suite. Production
index/query/classification modules are ordinary package dependencies. The renderer
records input hashes and embeds no Ashes test body in Bash.

Compile with `ASHES_RC_LEAKCHECK=1` under the session's 8G compiler scope, then repeat
with compile-time `ASHES_RC_POISON=1`. Validate each binary with:

```bash
bash selfhost/tests/type-graph-node-identity/validate.sh /absolute/path/to/binary work/type-graph-node-identity/validation
```

The validator scopes native runs to 4G, requires exact success output and one live
counter, rejects recorded signals, and compares 1,000/10,000 fresh-index counts.
Commands are invocable from fish.

`check` covers constructor/field order, generic instantiations, lazy nonregular
recursion, resource/unresolved fact separation, exact structural comparison and
intentional fingerprint collisions. The added alias test resolves one source key
to a string, repeats the resolved lookup without adding nodes, then queries the
same source in a fresh non-alias environment and requires its distinct nominal
answer. Ordinary cold/warm structure checks compare the stored equality fact to
an independent equality of the source and resolved types.

The fresh modes construct dynamic type names, environments and indices, publish a
fact and repeat a hit, then release the complete graph each iteration. This tests
ownership of dead caches, beyond a permanently retained warm cache.

Stage 0 focused validation: plain and poisoned identity checks passed; fresh indices
stay live 1→1. Compiler times were 21.83s plain and 21.32s poisoned, peak 938,968 KiB and
952,808 KiB. The separate `shared-graph-queries` oracle checks both changed consumers
against frozen classifiers and expected facts, including path-sensitive aliases;
its plain and poisoned expected/oracle checks passed, full-view fresh counts 6→6 and basic
resource/unresolved counts 1→1. These are focused fixtures, not a compiler census or
a completed stage 2 timing. A rebuilt stage 1 remains an integration gate.
