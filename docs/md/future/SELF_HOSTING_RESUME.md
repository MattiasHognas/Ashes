# Resume Ashes self-hosting after the 2026-11-21 quota pause

The goal is **paused, not completed**. The user revised the stop threshold to 98% weekly usage (2% remaining). It was reached; every running job was stopped and all agents had handed back. Resume when the user requests it and usage is available. Check the new session's current usage before starting loads; the old snapshot is not current account state.

Copy this prompt into a new agent session:

```text
Resume the Ashes self-hosting goal autonomously. You have no prior memory; use the repository handoff as the source of truth.

Repository: /home/mattias/Documents/source/Ashes. Work only in its .claude/worktrees/ directory. Integration: .claude/worktrees/step-f-round3, branch selfhost/round10. Fetch origin and verify status before changes.

First read all of "Current round: round 10" in docs/md/future/SELF_HOSTING.md, then its orientation documents in order, root AGENTS.md/CLAUDE.md, and work/coord/rules.md. Also read docs/md/future/SELF_HOSTING_RESUME.md. Verify live files and process state; do not assume old sessions are still running. The previous coordinator stopped all jobs at the user’s revised 98%-used / 2%-remaining weekly limit. Check fresh usage and retain that threshold.

Full objective: replace stage 0 with stage 1; obtain error-free, byte-identical stage2-to-stage3 compilation and stage parity; finish typed lowering steps 7/8; parallelize LLVM/codegen/linking (CG-17/LNK-14); then fix ALL leaks. Fix any blocking leak immediately. Do not narrow this to passing focused tests.

Current state: bootstrap55 stage 1 built successfully from source 78325617. Stage 2 was cancelled for quota, not a compiler failure. No stage 3 or byte comparison exists. Pre-round normal/no-reuse backend and semantics checks pass all non-leak cases; 32/95 IR differences are unchanged audited output. Fresh stage-1 REST and mock checks then fail with ASH019/ASH020 for capability Store / Store.load. Recursive inference has a separate severe summary-growth problem. Start with these two implementation tasks.

The indexing patch d30df401 is integrated as fa0a1247, and typed-lowering batch 36d8dbe1 as a898abc2. Do not cherry-pick them again. Their combined native validation remains pending. The matched indexing diagnostic advances from pass 2 (3,138 uses) to pass 4 (310,375 uses), so lookup improvement alone is insufficient. Read r10-recursive-evidence-cost/work/PROGRESS.md and r10-typed-lowering-next/work/PROGRESS.md. Diagnostic patches and fixture candidates are NOT integration candidates.

The single development/PR branch is selfhost/round10. The user forbids development tags. Older branch names in handoffs are historical: artifact worktrees are kept at their exact revisions with detached HEADs. Read docs/selfhost-archive/2026-11-21/README.md for the verified remote-backed source snapshots, four retained leak/ownership investigations, and retired worktrees. Do not apply archived patches wholesale. Never confuse archival preservation with validation or a completed compiler fix.

Coordinate at most two implementation agents, each with its own fix/* worktree from integration HEAD and a self-contained brief, rules and budget. Agents implement and run only permitted quick checks; you run all builds, suites, sweeps and profiling. Enforce stack/core limits, systemd memory/swap caps, timeouts and heavy slots. Never edit a source worktree while a job reads it. Stage 3 needs swap allowed in parent and child scopes. No pgrep-f wait loops, Python or Perl. Format edited Ash and C#; fix the compiler, never rewrite witnesses to avoid a bug; pair stage-0 fixes with stage-1 ports or prove the counterpart already correct.

Keep every source branch and the handoff committed and pushed. Integrate between readers, validate meaningful batches, and resume the full bootstrap/behavior/counter/suite gate after the new blockers are fixed. The user authorized autonomous iteration, validated milestone PR merges and obsolete-branch cleanup; ask only for unresolved language/spec decisions. At 98% weekly usage, stop jobs and agents, update handoff/docs, commit/push, pause the goal and provide another self-contained continuation prompt.
```

## Restart notes

- All result paths, exact hashes, test counts and cancellations are in the current-round handoff. Source commits `fa0a1247` and `a898abc2` were integrated after the quota pause at the user's consolidation request. Earlier results do not validate this newer source. No compiler jobs or suites were resumed during consolidation.
- `artifacts/selfhost-dev/s1-f-bootstrap55-debug` is valid and retained. Its SHA-256 is in `work/r10pre/round10-bootstrap55/stage1.sha256`. Use it to reproduce the REST errors and as a baseline.
- Preserve `work/r10pre/round10-bootstrap55/` unchanged. Its guard files record the old source head and its stage2 exit is 143. The wrapper refuses existing results/binaries. Source fixes require a newly validated source and fresh attempt/output name; do not create development Git tags, overwrite the cancellation or copy the old gate as current acceptance.
- Coordinator scripts and detailed logs are machine-local under the worktrees named in the handoff. Use split native drivers; the combined driver exceeded 24 GiB twice. Keep all original cases and sources.
- Start the two workers on REST capability resolution and evidence summary growth. Have the latter examine the C# merge/demand-stage behavior before making semantic reductions. An iteration limit, dropped requirement, or rewritten witness is not a fix.
- The prior strict-evidence decision stands: evidence needed while evaluating a binding must be concrete or supplied by its enclosing function. A later use cannot choose an earlier effect’s instance. Returned functions retain legitimate polymorphism.
- No full stage-1/stage-2 behavior run, round-10 six-suite gate, current-source counter acceptance, zero-replay proof, or S2/S3 byte identity has been established. Typed optimization, multithreading and the final all-leak cleanup remain in scope.
