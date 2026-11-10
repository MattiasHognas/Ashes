# Heap constructor lookup index

`check` compares indexed and list-backed heap classification for 125 constructor
sequences, including identical names with distinct IDs, identical IDs with distinct
names, mixed generic constructors, first-constructor record detection, misses,
and incremental batches that repeat keys.

`old1000`, `old10000`, `new1000`, and `new10000` create and release fresh dynamic
names, definitions, environments and lookup results. Compile with
`ASHES_RC_LEAKCHECK=1` and compare final live cells at the two round counts.
Run stage 0 plain and poisoned, then stage 1 plain (stage 1 does not implement
poison instrumentation).

Boundary checks also cover effectful constructor signatures and invalidation after
`addConstructorBinding`, followed by re-extension. Fresh rounds consume full layout
classification as well as the constructor lookup result.
