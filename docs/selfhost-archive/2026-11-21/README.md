# Self-hosting source archive and consolidation

The active development branch is `selfhost/round10`, with [draft PR #1160](https://github.com/MattiasHognas/Ashes/pull/1160). No development tags were created.
This archive preserves retired development states for reference; its patches are not accepted
compiler changes and must not be applied wholesale to the integration branch.

`source-snapshots.tar.gz` contains 85 remote branch snapshots and 12 older dirty-worktree
snapshots. `manifest.tsv` identifies each snapshot's original name, base commit, original HEAD
and patch filename. Each base is an ancestor of the integration branch. All 97 patches were
applied to temporary Git indexes and their resulting trees compared with the original branch
or working-tree contents. The sole excluded untracked file is `stage1-post-guards/shape2`, an
obsolete generated ELF executable. Original working-tree indexes and source files were not changed.

To inspect a snapshot, extract the archive under an ignored `work/` directory. To reconstruct it,
create a disposable worktree under `.claude/worktrees/` at the manifest's base commit, then apply
that row's patch there. Use the machine limits in `work/coord/rules.md`. Do not build or execute
old candidate code without first reviewing its status and preserving the current test inputs.
The patch reconstructs the file tree; it does not recreate the original commit history.

The complete pre-consolidation Git histories also have a verified machine-local bundle at
`step-f-round3/work/coord/consolidation-20261121/history-before.bundle`. The source snapshots
above are committed and pushed with integration, so unfinished source is not only local.
Existing diagnostic and artifact worktrees retain their exact revisions with detached HEADs.

## Review of the twelve older dirty worktrees

This was source and evidence review only; no new compiler execution or leak census was run.
None of these older changes was established as a fix for the current REST capability rejection
or recursive evidence-summary growth.

| Worktree | Finding and disposition |
| --- | --- |
| `stage1-leak-free` | Retain the original borrowed-child/literal-successor reproducer for the later leak phase. Its `ROUNDS` input is a template, not a directly runnable regression. |
| `stage1-post-guards` | Retain historical leak witnesses and watchpoint notes, including dispatch hand-over slots and CFG pending-list ownership. The untracked `shape2` file is just a generated ELF. Old counts need fresh reproduction. |
| `step-e-dispatch-analysis` | Retain the unfinished private dispatch tuple-field ownership proof, owned tuple-result adoption change, four regression programs and watchpoint evidence. These additions are absent from current source. The historical candidate reduced one case from 19/91 to 13/61 live cells; it did not eliminate its leaks. The stage-1 mirror is incomplete/unvalidated and this is not an accepted fix. |
| `step-f-constrained-specialization32` | The specialization implementation is superseded by current `CoreLowering.Expressions` and `CoreConstrainedBindings`. Retain `work/alias-ownership/DIAGNOSIS.md`, its original Main and watchpoint outputs: the three-dictionary alias's final closure/environment were never released in the old experiment. Current `ApplyTraitDictionaryCallChain` still initializes stage temporaries only when `dictionaries.Count == 1`; this is a relevant lead, not a freshly reproduced bug. |
| `step-f-graph-stage1-tests` | Retire the assertion-rewriting adapter, which bypassed a bootstrap32 compiler bug. Use original assertion programs for acceptance. Its separate validator improvement remains useful: require stderr to be exactly one numeric `ashes-rc-live` line, rather than merely counting a matching line among other output. Record this small future validation improvement without adopting the adapter. |
| `step-f-inference-fields` | Superseded: current inference includes record-field candidates and qualified-value resolution. The regression module and query programs are byte-identical; lockfile/current inference differences reflect later work. Preserve the historical lifetime evidence before removing the worktree. |
| `step-f-map-successor-tests` | Superseded: the fuzz template is byte-identical on main, its registry entry and test exist, and the two e2e programs differ only in canonical formatting. |
| `step-f-mixed-evidence32` | Superseded by current constrained-binding/evidence handling. The original constrained-binding case manifest and programs are already present; current inference adds the later strict eager-evidence policy. |
| `step-f-reach-constructor-index` | Superseded: current result-reach analysis indexes constructors while keeping the first declaration. The current fixture has a shell renderer and wider comparisons; retire the old Perl renderer without executing it. |
| `step-f-roadmap` | Obsolete sequencing notes and three generated executable-hash snapshot edits. The current handoff and user decisions supersede them. |
| `step-f-shared-graph-consumers` | Superseded: the current heap classifier includes the indexed consumer changes, plus an explicit inference import and cached source/resolved identity. |
| `step-f-shared-graph-queries` | Superseded: current queries include child-node caching, and current tests add a genuinely frozen oracle, full-view checks and expected assertions. |

The eight removed worktrees are `step-f-graph-stage1-tests`,
`step-f-inference-fields`, `step-f-map-successor-tests`, `step-f-mixed-evidence32`,
`step-f-reach-constructor-index`, `step-f-roadmap`, `step-f-shared-graph-consumers`, and
`step-f-shared-graph-queries`. Their existing `work/` directories are retained machine-locally at
`step-f-round3/work/coord/consolidation-20261121/retired-worktree-evidence/<name>/work/`.
The other four worktrees retain their original source and evidence in place.

## Validation required after consolidation

The completed indexing batch `d30df401` is now `fa0a1247`; the recorded source-copy type batch
`36d8dbe1` is now `a898abc2`. These were transferred without conflict. Prior formatting and source
checks are recorded in their worker progress files. Fresh integrated native validation is pending.

Run fresh split semantics/IR and normal/poison backend checks, the new evidence-key identity and
recorded-copy counter regressions, then matched measurement after recursive summary growth is
fixed. Fix the real REST capability errors before a new bootstrap acceptance attempt. The earlier
bootstrap55 stage 1 and all prior suite results belong to their recorded old source revisions.
Stage 2/3 completion, byte identity, typed-lowering acceptance, parallel LLVM/linking and all-leak
cleanup remain unfinished. The compiler goal is still paused at the user's quota threshold.
