# Type graph search plateau

Compile `ashes.json` with `ASHES_RC_LEAKCHECK=1`, then run the same executable with
`1000` and `10000`. Both runs must print `done` and the same live-cell count.
The runtime argument controls graph depth and alternates an early successful search
with an exhaustive unsuccessful search of a shared type graph.

Repeat with stage 0 plain and `ASHES_RC_POISON=1`, and with stage 1. Stage 1 does not
currently implement the poison switch, so its run is plain coverage only.
