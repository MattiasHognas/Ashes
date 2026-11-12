# Curried parser result survives recursive return cleanup

Compile `ashes.json` with stage 0 plain/poisoned and a rebuilt stage 1. The native
program must print `parsed`. Its source is preserved from the original parser-only
failure, with no parser implementation changes or ownership workarounds.

Bootstrap 17 and bootstrap 20 both emitted a premature free. In bootstrap 20's
lowered `parserParseSugarParameters` closure helper, recursive `CallClosure` t32
was parked in owned slot22, passed through nested joins to t281, and treated as a
freshly owned function result. The return epilogue kept the pointer unchanged,
then released slot22 and returned the freed pair. Native watchpoints established
both pair and ParserState freelist insertion before the dangling return, followed
by reuse that put a pointer in the state's Bool field and null in its Bytes field.

The compiler fix derives non-reaching arms from emitted backedge dummy temps,
not syntactic self-call name/arity. It also removes that syntactic shortcut from
the retained-arm ownership fact. Genuine emitted backedges and all-neutral nested
joins keep their prior ownership-neutral meaning. Focused metadata checks live
in `../join-backedge-provenance`.

The baseline stage-0 program prints `parsed` with two live cells. Bootstrap20
crashes with signal 11. Rebuilt bootstrap21 (source `ac493aa2`, binary SHA256
`0b55e671d848f6aea53ab25814c55afe60d1b63fb13f372e542b791aa2287199`)
passes both plain and poisoned: each prints `parsed` and leaves two live cells.
Fixture compile times were 92.00s and 91.08s; peak RSS was 1282904KiB and
1283020KiB, respectively. These are single-run residual counts, not a plateau
measurement of the whole compiler.

The candidate's lowered IR retains the reaching recursive result before the outer
join: owned slot22 → t33 → inner join t37 → `RcDup t283=t37` on the RC path.
The join therefore holds its own reference when slot22 is released at function
exit. This is the existing mixed-join normalization, enabled by correct arm
provenance; no parser source rewrite or unconditional child retain was introduced.
Raw plain/poison IR and run evidence are preserved under
`work/bootstrap21-parser/result.MubOQF` in the development worktree.
