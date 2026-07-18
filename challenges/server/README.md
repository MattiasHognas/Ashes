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
- **`http_echo.ash`** — the Ashes HTTP server (`Ashes.Net.Http.Server.serve`): every request returns a
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
  ashes    conc 1     39,626 req/s   p50 0.019  p99 0.030  max 2.885 ms
  ashes    conc 8    213,949 req/s   p50 0.027  p99 0.074  max 2.995 ms
  ashes    conc 64   314,488 req/s   p50 0.134  p99 0.590  max 4.491 ms
  dotnet   conc 1     40,250 req/s   p50 0.019  p99 0.031  max 2.875 ms
  dotnet   conc 8    137,001 req/s   p50 0.049  p99 0.080  max 3.298 ms
  dotnet   conc 64   134,462 req/s   p50 0.489  p99 0.806  max 3.942 ms

=== HTTP 200   127.0.0.1:18081 ===
  ashes    conc 1     25,169 req/s   p50 0.033  p99 0.049  max 1.111 ms
  ashes    conc 8    144,611 req/s   p50 0.042  p99 0.100  max 1.698 ms
  ashes    conc 64   249,445 req/s   p50 0.191  p99 0.847  max 3.971 ms
  dotnet   conc 1     40,571 req/s   p50 0.020  p99 0.032  max 1.403 ms
  dotnet   conc 8    131,726 req/s   p50 0.049  p99 0.078  max 1.491 ms
  dotnet   conc 64   123,764 req/s   p50 0.534  p99 1.393  max 2.045 ms
```

Measured on the run-queue scheduler with async tail-recursive loops, the per-iteration arena reset
(bounded keep-alive memory), incremental Content-Length and chunked request buffering, and the
graceful-shutdown drain — all in place, zero errors across every stage, no measurable throughput
change from earlier recordings.

## Reading the results

`serve` is parallel by default — a fork-based multi-reactor with one reactor per online CPU
(`SO_REUSEPORT`, so the kernel load-balances connections across workers). At **conc 1** a single
connection uses a single reactor, so Ashes and .NET are within noise on TCP (the raw
accept/receive/send/close path is tight); on HTTP the pure-Ashes request parse is the per-request cost,
so .NET leads single-connection. As concurrency rises the reactors light up: on **TCP echo** Ashes
leads .NET ~2.3x at conc 64 (~314k vs ~134k), and on **HTTP 200** the extra cores more than absorb the
parse cost, so Ashes roughly **doubles** .NET at conc 64 (~249k vs ~124k). Remaining single-reactor
headroom (faster in-loop parsing) would lift the conc-1 HTTP number further.
