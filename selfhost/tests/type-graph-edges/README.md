# Lazy type graph edge resolution

Production traversal work remembers the immutable parent's ID, resource/unresolved
edge policy and child position. At dequeue it uses a previously resolved child
node or interns the complete type with the existing collision check. This avoids
repeating structural hash/equality work when another path or query revisits the
same edge. Enqueue performs no discovery. The two edge policies remain separate,
and no path-dependent answers enter the cache.

A missed edge is retained only if its canonical node already existed before that
lookup. This avoids constructing an extra map for every new cold child. Sharing
or a subsequent visit admits the edge. Work resolution returns the original
(node, index) tuple directly instead of wrapping it in another result tuple.

Run `bash selfhost/tests/type-graph-edges/render.sh` to prepare ignored projects
from the checked-in Ashes sources. Compile `work/type-graph-edges/check/ashes.json`
with leak counters, plain and poisoned, using the session's 8G scoped compiler.
Validate each binary with:

```bash
bash selfhost/tests/type-graph-edges/validate.sh /absolute/path/to/binary work/type-graph-edges/validation
```

The validator scopes programs to 4G, requires exact stdout and one live counter,
rejects signals/nonzero exits, and compares 1,000/10,000 fresh-index counts.

The relation checks cover nonregular growing recursion, first-positive
short-circuiting without discovering an unused sibling, resource/unresolved child
policy separation, exact fingerprint collisions, duplicate child positions,
immutable earlier snapshots, aliases and environment replacement. They distinguish
cold admission from warm edge retention. Nonregular drop queries independently
inspect a child's resource closure before cutting that child's recursive path;
the benchmark's `nonregular` mode checks the same discovery counts against the
unchanged implementation. Fresh modes construct and release both edge maps and
their retained canonical nodes on every iteration. Assertions accept a concrete
Bool, so the fixture does not require polymorphic constrained assertion helpers.

`Benchmark.ash` is identical for old and candidate builds. The baseline production
modules were frozen at 81c18753. Measured with the same stage0 compiler:

| Workload | Baseline | Candidate |
| --- | ---: | ---: |
| 1,000,000 fresh depth-8 graphs | 3.54s | 4.28s |
| 1,000,000 warm depth-8 traversals | 1.09s | 0.80s |
| 10,000 warm depth-128 traversals | 1.11s | 0.11s |

All benchmark programs finish at one live cell. Peak RSS is comparable: about
25 MiB for the million fresh graphs and 13 MiB for the warm cases. Cold small
queries retain a measured cost of about 0.74 microseconds per fresh graph. These
synthetic timings are not a bootstrap speedup claim. The broader shared-graph-query
frozen oracle separately checks all layout views and path-sensitive cases.

Final source passed the relation tests and fresh-index plateau under stage0 plain
and poisoned builds, and under the actual bootstrap32 stage1 compiler. All three
produced live counts 1→1 at 1,000/10,000 fresh iterations. Stage0 fixture builds took
19.70s plain and 19.12s poisoned; stage1 took 85.42s with peak 820,140 KiB. These are
focused fixture compilation times, not compiler bootstrap times.

The frozen shared-graph-query oracle and expected layout tests also passed. Its
fresh basic queries stayed 1→1 and full layout views stayed 6→6; the oracle build
took 22.08s. All final source hashes and detailed logs are retained locally under
`work/type-graph-edges/`. Full suites and the next combined bootstrap remain
separate integration gates.
