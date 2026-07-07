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
  ashes    conc 1     41,746 req/s   p50 0.018  p99 0.030  max 2.714 ms
  ashes    conc 8    212,601 req/s   p50 0.027  p99 0.075  max 3.060 ms
  ashes    conc 64   319,799 req/s   p50 0.131  p99 0.558  max 4.547 ms
  dotnet   conc 1     41,428 req/s   p50 0.018  p99 0.033  max 3.037 ms
  dotnet   conc 8    130,651 req/s   p50 0.056  p99 0.085  max 3.154 ms
  dotnet   conc 64   117,640 req/s   p50 0.452  p99 1.893  max 7.201 ms

=== HTTP 200   127.0.0.1:18081 ===
  ashes    conc 1     25,597 req/s   p50 0.033  p99 0.049  max 1.149 ms
  ashes    conc 8    145,184 req/s   p50 0.042  p99 0.097  max 1.766 ms
  ashes    conc 64   238,701 req/s   p50 0.200  p99 1.002  max 3.373 ms
  dotnet   conc 1     40,173 req/s   p50 0.020  p99 0.030  max 1.511 ms
  dotnet   conc 8    122,498 req/s   p50 0.058  p99 0.082  max 1.416 ms
  dotnet   conc 64   124,524 req/s   p50 0.506  p99 1.650  max 3.043 ms
```

Measured on the run-queue scheduler with async tail-recursive loops and the per-iteration arena reset
(bounded keep-alive memory) — the reset costs no measurable throughput against the previous recording.

## Reading the results

`serve` is parallel by default — a fork-based multi-reactor with one reactor per online CPU
(`SO_REUSEPORT`, so the kernel load-balances connections across workers). At **conc 1** a single
connection uses a single reactor, so Ashes and .NET are within noise on TCP (the raw
accept/receive/send/close path is tight); on HTTP the pure-Ashes request parse is the per-request cost,
so .NET leads single-connection. As concurrency rises the reactors light up: on **TCP echo** Ashes
leads .NET ~2.7x at conc 64 (~320k vs ~118k), and on **HTTP 200** the extra cores more than absorb the
parse cost, so Ashes roughly **doubles** .NET at conc 64 (~239k vs ~125k). Remaining single-reactor
headroom (faster in-loop parsing) would lift the conc-1 HTTP number further.
