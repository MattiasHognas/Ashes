# challenges/server — TCP and HTTP server benchmarks

Load/latency benchmarks that compare the Ashes servers against equivalent .NET baselines on the same
functionality, reusable as optimization baselines (like `challenges/1brc`). Nothing here is discovered
or run by CI, and the `.ash` files are not format-checked by any gate.

There are **two benchmarks**, each isolating one server path:

- **TCP echo** — `tcp_echo.ash` vs `dotnet-tcp.cs` on `127.0.0.1:18080`.
- **HTTP 200** — `http_echo.ash` vs `dotnet-http.cs` on `127.0.0.1:18081`.

The key point: the **same fast .NET load generator drives both servers in each benchmark**, so the
comparison isolates the *server* (same functionality, same client). An earlier version drove the
servers with an Ashes client that was itself the bottleneck, which hid the difference; a fast common
client makes the server the bottleneck, so real differences show.

## Pieces

- **`tcp_echo.ash`** — the Ashes TCP echo server (`Ashes.Net.Tcp.Server.serve`): one `receive` + echo
  + `close` per connection — the smallest handler, so it measures the server path.
- **`http_echo.ash`** — the Ashes HTTP server (`Ashes.Http.Server.serve`): every request returns a
  fixed `200 "ok"`, so it measures the HTTP path (request parse + response render) over the TCP layer.
- **`dotnet-tcp.cs`** / **`dotnet-http.cs`** — single-file .NET baselines (concurrent async accept
  loops, the natural .NET idiom; the HTTP one is a raw socket, not Kestrel, to stay apples-to-apples).
- **`loadgen.cs`** — a single-file .NET load generator (the common driver) with a `tcp`/`http` mode.
  Each request is one connection; it does the concurrency and timing itself and reports throughput +
  latency percentiles.
- **`bench.sh`** — builds everything to plain executables once (the Ashes servers via the compiler,
  the .NET pieces via `dotnet publish`), then runs both benchmarks, each server labeled.

## Run

```bash
bash challenges/server/bench.sh                 # 20000 requests/stage, concurrency sweep 1 8 64
bash challenges/server/bench.sh 50000 1 16 128  # REQUESTS then CONCURRENCY levels
```

## Latest outcome

Rerun before every change to the server path and update the numbers below (20000 requests/stage,
concurrency 1/8/64, quiet loopback box, one connection per request). A loaded box adds variance, so
trust the `ashes`-vs-`dotnet` comparison within one run and interleave A/B runs across Ashes builds.

```
=== TCP echo   127.0.0.1:18080 ===
  ashes    conc 1     39,231 req/s   p50 0.021  p99 0.030  max 3.002 ms
  ashes    conc 8    123,898 req/s   p50 0.052  p99 0.133  max 3.616 ms
  ashes    conc 64   125,769 req/s   p50 0.481  p99 1.118  max 4.375 ms
  dotnet   conc 1     40,356 req/s   p50 0.019  p99 0.031  max 3.009 ms
  dotnet   conc 8    121,147 req/s   p50 0.058  p99 0.082  max 2.547 ms
  dotnet   conc 64   139,818 req/s   p50 0.391  p99 0.863  max 3.451 ms

=== HTTP 200   127.0.0.1:18081 ===
  ashes    conc 1     28,495 req/s   p50 0.029  p99 0.044  max 1.165 ms
  ashes    conc 8     48,396 req/s   p50 0.151  p99 0.317  max 1.828 ms
  ashes    conc 64    51,931 req/s   p50 1.211  p99 2.534  max 3.970 ms
  dotnet   conc 1     41,934 req/s   p50 0.018  p99 0.030  max 1.322 ms
  dotnet   conc 8    135,946 req/s   p50 0.048  p99 0.080  max 1.814 ms
  dotnet   conc 64   123,235 req/s   p50 0.511  p99 1.764  max 2.239 ms
```

## Reading the results

Both Ashes servers step handlers **cooperatively on one thread** today; the .NET baselines are
concurrent across the thread pool. On **TCP echo** Ashes is competitive (~90–100% of the .NET
throughput across the sweep) — the raw accept/receive/send/close path is tight. On **HTTP 200** the
gap widens with concurrency: at conc 1 Ashes is ~68% of .NET, but at conc 8/64 it trails more
(~36–42%) because the HTTP request parse is pure-Ashes string work and there is no cross-core
parallelism yet. Closing that gap is the headroom the **multi-reactor milestone** (worker-per-core
reactors) targets; faster in-loop parsing would help the single-thread number too.
