# challenges/server — TCP server benchmark

A load/latency benchmark that compares the Ashes TCP echo server against a .NET echo server on the
same functionality, and is reusable as an optimization baseline (like `challenges/1brc`). Nothing
here is discovered or run by CI, and the `.ash` is not format-checked by any gate.

The key point: the **same fast .NET load generator drives both servers**, so the comparison isolates
the *server* (same functionality — an echo server — same client). An earlier version drove the
servers with an Ashes client that was itself the bottleneck, which hid the difference; using a fast
common client makes the server the bottleneck, so real differences show.

## Pieces

- **`echo.ash`** — the Ashes echo server on `127.0.0.1:18080` (`Ashes.Net.Tcp.Server.serve`). One
  `receive` + echo + `close` per connection.
- **`dotnet-echo.cs`** — a single-file .NET echo server (concurrent async accept loop, the natural
  .NET idiom), as the reference point.
- **`loadgen.cs`** — a single-file .NET load generator (the common driver). Does the concurrency and
  timing itself; each request is one connection (connect → send → read echo → close) and it reports
  throughput + latency percentiles.
- **`bench.sh`** — builds all three to plain executables once (the Ashes server via the compiler, the
  .NET pieces via `dotnet publish`), then runs the same load sweep against each server, labeled.

## Run

```bash
bash challenges/server/bench.sh                 # 20000 requests, concurrency sweep 1 8 64
bash challenges/server/bench.sh 50000 1 16 128  # REQUESTS then CONCURRENCY levels
```

## Reading the results

`serve` handles connections **sequentially** today; the .NET baseline is concurrent. So Ashes tends to
win at low/moderate concurrency (less per-connection overhead than thread-pool dispatch), while the
concurrent .NET server holds tail latency better at high concurrency — which is the headroom the
multi-reactor milestone (worker-per-core reactors) targets. A loaded box adds variance, so trust the
`ashes`-vs-`dotnet` comparison within one run and interleave A/B runs across Ashes builds. Example
shape on a quiet box (illustrative):

```
== ashes server (serve, sequential) ==
  ashes  conc 1     48k req/s   p50 0.013  p99 0.027 ms
  ashes  conc 8    190k req/s   p50 0.032  p99 0.075 ms
  ashes  conc 64   127k req/s   p50 0.312  p99 1.884 ms
== dotnet server (concurrent) ==
  dotnet conc 1     37k req/s   p50 0.021  p99 0.033 ms
  dotnet conc 8    138k req/s   p50 0.048  p99 0.081 ms
  dotnet conc 64   143k req/s   p50 0.372  p99 0.827 ms
```
