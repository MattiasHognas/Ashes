# Returned closure label index

Run `bash scripts/selfhost-dev/render-returned-closure-index.sh` to extract the production curry-chain walker and import the actual call-ownership walker. The comparison preserves first-match/newest-binding semantics, missing labels, cycles, bounded hops and persistent snapshots. Run `check`, `1000` and `10000` under the scoped limits, with leak checking; fresh runs must remain flat in both stages and poisoned stage0.

The map updates at the two production registration points, while outer-frame restoration carries the completed map back from nested lowering. It replaces the ordered list entirely because no consumer enumerates it. No index is rebuilt at lookup time.
