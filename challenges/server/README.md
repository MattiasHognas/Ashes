# challenges/server — TCP server benchmark

A load/latency benchmark for the Ashes TCP server, built to measure how fast the server responds and
how it holds up under pressure, and to be reused as an optimization baseline (like `challenges/1brc`).
Nothing here is discovered or run by CI, and the `.ash` files are not format-checked by any gate.

Client and server are both Ashes (dogfooding); orchestration is a shell script.

## Pieces

- **`echo.ash`** — a minimal echo server on `127.0.0.1:18080` using `Ashes.Net.Tcp.Server.serve`. One
  `receive` + echo + `close` per connection, so the benchmark measures the server path
  (accept / receive / send / close + scheduling), not handler work.
- **`load.ash`** — a load client: does `count` (its first argument) sequential
  connect/send/recv/close round-trips to the server. Compiled, so it measures the server rather than
  driver overhead.
- **`bench.sh`** — compiles both, starts the server, and for each concurrency level runs that many
  `load` clients in parallel (each doing `requests/concurrency` round-trips), timing the batch with
  the shell clock to report **throughput (req/s)** and **mean round-trip latency** per stage.

## Run

```bash
bash challenges/server/bench.sh                 # 20000 requests, concurrency sweep 1 8 64
bash challenges/server/bench.sh 50000 1 16 128  # REQUESTS then CONCURRENCY levels
```

## Reading the results

`serve` currently handles connections **sequentially** (one at a time). Concurrency 1 is client-bound
(a single client can't keep the server busy); a handful of parallel clients saturate it; high
concurrency then degrades as connections queue and per-connection overhead adds up. That queuing is
the honest baseline the multi-reactor milestone (worker-per-core reactors) will be A/B'd against.
Example shape on a quiet box (illustrative; a loaded box adds variance, so interleave A/B runs):

| stage | throughput | mean round-trip |
|---|---|---|
| latency (concurrency 1) | ~19k req/s | ~0.05 ms |
| load (concurrency 8)    | ~150k req/s | ~0.05 ms |
| load (concurrency 64)   | ~24k req/s | ~2.7 ms |
