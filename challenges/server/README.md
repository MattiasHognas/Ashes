# challenges/server — TCP server benchmark

A load/latency benchmark for the Ashes TCP server, built to measure how fast the server responds and
how it holds up under pressure, and to be reused as an optimization baseline (like `challenges/1brc`).
Nothing here is discovered or run by CI, and the `.ash` is not format-checked by any gate.

## Pieces

- **`echo.ash`** — a minimal echo server on `127.0.0.1:18080` using `Ashes.Net.Tcp.Server.serve`. One
  `receive` + echo + `close` per connection, so the benchmark measures the server path
  (accept / receive / send / close + scheduling), not handler work.
- **`bench.py`** — the driver. Each request is one connection (connect → send → read echo → close).
  Reports, per stage:
  - **response speed** — round-trip latency percentiles (p50/p90/p99) at concurrency 1;
  - **load handling** — throughput (req/s) and latency percentiles at N concurrent connections,
    showing how tail latency degrades as concurrency climbs.

## Run

```bash
dotnet run --project src/Ashes.Cli -- compile challenges/server/echo.ash -o /tmp/echo
/tmp/echo &                                   # binds 127.0.0.1:18080
python3 challenges/server/bench.py            # sweeps concurrency 1, 8, 64
# options: --requests N  --concurrency C ...  --payload-bytes B  --host H --port P
kill %1
```

## Reading the results

`serve` currently handles connections **sequentially** (one at a time), so throughput falls and tail
latency grows as concurrency rises — that queuing is the honest baseline the multi-reactor milestone
(worker-per-core reactors) will be compared against. Example shape on a quiet box (numbers are
illustrative; a loaded box adds variance, so interleave A/B runs when comparing builds):

| stage | throughput | p50 | p99 |
|---|---|---|---|
| latency (concurrency 1) | ~66k req/s | ~0.01 ms | ~0.02 ms |
| load (concurrency 8)    | ~58k req/s | ~0.13 ms | ~0.27 ms |
| load (concurrency 64)   | ~39k req/s | ~1.5 ms  | ~2.7 ms  |
