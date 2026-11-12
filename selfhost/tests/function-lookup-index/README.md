# Function lookup index checks

Run `bash scripts/selfhost-dev/render-function-lookup.sh` to extract the actual Core lookup helpers and state setters into `work/function-lookup-index`. The generated fixture uses a minimal state record containing the relevant production fields; it does not compile all of CoreLowering.

Checks compare candidate/value/arity answers with the previous list lookup, including duplicate names, missing names, and qualified names. State checks preserve the ordered candidate list, clear old index entries on replacement, register newest function references, and retain older immutable snapshots. Fresh dynamic names exercise preparation, registration, lookup, and disposal on every round.

Compile with `ASHES_RC_LEAKCHECK=1`, then run with `1000` and `10000`; both must print `function indexes ok` with equal live-cell counts. Validate stage0 plain, stage0 with compile-time `ASHES_RC_POISON=1`, and stage1 plain. Use scoped 8G compilation and 4G native execution limits with swap disabled, recording elapsed time and peak RSS separately from leak output.

Frame restoration and ordinary local-binding precedence are preserved by the production integration: neither control path changes. The focused fixture verifies the new lookup/state primitives; full lowering behavior remains covered by the Step F gate and specialization/shadowing scenarios.
