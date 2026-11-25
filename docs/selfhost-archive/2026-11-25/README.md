# Self-hosting plan and log archive

On 2026-11-25 the self-hosting plan (`docs/md/future/SELF_HOSTING.md`) was rewritten as one short
document, its item catalogue moved to `docs/md/future/SELF_HOSTING_CHECKLIST.md`, and the log
(`docs/md/future/SELF_HOSTING_LOG.md`) rewritten as a changelog. This directory keeps the text they
replaced, verbatim, taken from `selfhost/round11` at commit `f1b0c4ee`. It is a record, not a plan:
nothing here is a task to resume.

| File | What it is |
| --- | --- |
| `SELF_HOSTING.superseded.md` | `SELF_HOSTING.md` as it was, up to the start of its "Toolchain implementation checklist" (lines 1 to 1829): the introduction, "Known gaps deferred on the way to the fixpoint", the full dated "Type inference before lowering", "Start here" with every round note, the lettered "Work order" A to T, "How to work on this" and "Migration state (reference)". The checklist itself moved unchanged to `SELF_HOSTING_CHECKLIST.md`. |
| `SELF_HOSTING_LOG.before.md` | `SELF_HOSTING_LOG.md` as it was: completed checklist entries, the language and standard-library prerequisites audit, the plan as it read before the fixpoint, and the round-10 checkpoints. |
| `SELF_HOSTING_RESUME.md` | The continuation prompt written after round 10, deleted from `docs/md/future/`. |

The files are copied byte for byte, so their relative links resolve against `docs/md/future/`, not
this directory. Like the `2026-11-21` archive beside it, this directory is outside the
documentation site's source directory (`docs/md`) and is not part of the published site.
