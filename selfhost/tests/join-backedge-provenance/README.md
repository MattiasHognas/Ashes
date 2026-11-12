# Actual backedge ownership provenance

Run `bash scripts/selfhost-dev/render-join-backedge-provenance.sh`, then compile
`work/join-backedge-provenance/ashes.json` with stage 0 plain/poisoned and stage 1.
Run each binary with `1000` and `10000`; both must print `ok` and have identical
live-cell counts. The fixture extracts the production arm builder and neutrality
helpers unchanged, with narrow adapters for unrelated layout and pattern facts.

The parked call-result temp models the original parser's curried `CallClosure`:
it reaches the join and owns no separate retained reference. It must prevent a
fresh sibling from proving the whole result freshly owned. Actual backedge dummy
temps remain neutral. All-neutral nested joins propagate the fact; mixed and
empty joins do not. This checks the exact metadata rules, not whole Core lowering.
The full runtime regression is `../parser-curried-result` and must also be run
with a compiler rebuilt from the changed Core source.

Validation (bootstrap20 stage 1, before rebuilding the complete compiler): all
three extracted-helper builds passed and stayed at 1 live cell for 1,000 and
10,000 checks. Compile elapsed/RSS: stage 0 plain 1.36s/218096KiB, poisoned
1.34s/235368KiB, stage 1 1.95s/233492KiB. These establish helper semantics and
lifetime only; they do not claim the rebuilt parser regression has passed.
