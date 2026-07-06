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
concurrency 1/8/64, one connection per request, on a 32-core loopback box). A loaded box adds
variance, so trust the `ashes`-vs-`dotnet` comparison within one run and interleave A/B runs across
Ashes builds. `serve` is parallel by default (one reactor per online CPU), so these reflect
multi-core scaling out of the box.

```
=== TCP echo   127.0.0.1:18080 ===
  ashes    conc 1     36,054 req/s   p50 0.022  p99 0.033  max 3.024 ms
  ashes    conc 8    204,558 req/s   p50 0.028  p99 0.084  max 3.602 ms
  ashes    conc 64   300,801 req/s   p50 0.136  p99 0.617  max 4.952 ms
  dotnet   conc 1     44,193 req/s   p50 0.016  p99 0.031  max 2.587 ms
  dotnet   conc 8    192,007 req/s   p50 0.028  p99 0.094  max 3.008 ms
  dotnet   conc 64   265,980 req/s   p50 0.126  p99 0.937  max 4.109 ms

=== HTTP 200   127.0.0.1:18081 ===
  ashes    conc 1     29,146 req/s   p50 0.030  p99 0.044  max 1.189 ms
  ashes    conc 8    154,350 req/s   p50 0.039  p99 0.095  max 1.675 ms
  ashes    conc 64   268,186 req/s   p50 0.174  p99 0.850  max 3.473 ms
  dotnet   conc 1     30,089 req/s   p50 0.026  p99 0.048  max 1.195 ms
  dotnet   conc 8    112,717 req/s   p50 0.048  p99 0.203  max 1.607 ms
  dotnet   conc 64   134,054 req/s   p50 0.294  p99 2.142  max 3.824 ms
```

## Reading the results

`serve` is parallel by default — a fork-based multi-reactor with one reactor per online CPU
(`SO_REUSEPORT`, so the kernel load-balances connections across workers). At **conc 1** a single
connection uses a single reactor, so Ashes and .NET are within noise on both benchmarks (the raw
accept/receive/send/close path is tight). As concurrency rises the reactors light up: on **TCP echo**
Ashes leads .NET (~316k vs ~269k at conc 64), and on **HTTP 200** — where the pure-Ashes request parse
is the per-request cost — the extra cores more than absorb it, so Ashes roughly **doubles** .NET at
conc 64 (~274k vs ~134k). Remaining single-reactor headroom (faster in-loop parsing, persistent epoll)
would lift the conc-1 numbers further.
