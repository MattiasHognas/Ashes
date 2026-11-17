# Indexed proper path-ancestor pairs

`bash selfhost/tests/reach-ancestor-index/render.sh` generates the ignored project
`work/reach-ancestor-index/ashes.json`. Format its `.ash` files before compiling.
The full old result-reach implementation is frozen at ac493aa2, with only local
types/constructors and exported names prefixed to avoid importer collisions.
The original snapshot is retained byte-for-byte as an ignored raw-source dump. The direct Boolean
helper is extracted from both actual old and candidate production source.

`check` compares every pair of strings over `a`, `/`, `*` up to length three,
plus explicit Unicode and longer paths. It checks unequal ancestor/descendant
lists, duplicates and counts, exact `reachSum` counts/causes/poison output, and real
single-function summaries constructed as ASTs (identity, duplicated parameter,
distinct parameters, and list destructuring with parent plus child returned).
It explicitly distinguishes `a/` (no proper suffix) from `a//` (nonempty suffix)
under ancestor `a`. `tokenShared` and its less strict ancestor predicate remain
unchanged.

Compile with `ASHES_RC_LEAKCHECK=1`. `old100`/`old1000` and `new100`/`new1000`
allocate fresh names and query state each iteration; compare each implementation's
paired counts. `old-sparse`/`new-sparse` query 512 unrelated paths 100 times;
`old-early`/`new-early` add an immediately discoverable pair. The latter quantifies
the eager index-construction tradeoff. Run stage0 plain/poison and stage1 plain.
The full compiler build/profile remains a separate integration gate.

Byte-sliced text is also covered: a suffix made only of UTF-8 continuation bytes
has zero length under the existing Text.length rule. The indexed predicate checks
for a codepoint-start byte after a matching slash, preserving that behavior rather
than assuming every internal string is well-formed UTF-8.

## Focused results

Stage0 plain, stage0 poison and unchanged bootstrap20 stage1 all report
`equivalent` for the complete comparison fixture. Each old/new fresh-query pair
at 100/1000 ends with live counts 1/1, including stage1. One-shot comparisons and
benchmarks also end with live 1.

For 512 unrelated paths queried 100 times, stage0 old/new elapsed times are
0.79/0.00–0.01 seconds; stage1 is 0.68/<0.01 seconds. Both early-positive variants
finish below the 0.01-second timer resolution at this iteration count. This does
not prove that eager index construction is free on early matches; it measures
the intended sparse negative case and records the tradeoff honestly.

Stage1 fixture compilation took 3:34.10 wall, 213.18 CPU seconds, peak RSS
1,878,328 KiB, exit 0. Production/generated `.ash` formatting and focused checks
passed. The permanent selfhost test covers proper-suffix boundaries, Unicode,
sibling prefixes and duplicate-name sharing. The full compiler build/profile and
full repository suites remain integration gates, not completed claims here.
