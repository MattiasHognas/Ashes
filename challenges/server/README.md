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
  ashes    conc 1     37,553 req/s   p50 0.021  p99 0.032  max 3.011 ms
  ashes    conc 8    205,642 req/s   p50 0.028  p99 0.074  max 3.338 ms
  ashes    conc 64   314,213 req/s   p50 0.137  p99 0.604  max 4.082 ms
  dotnet   conc 1     44,829 req/s   p50 0.015  p99 0.030  max 2.994 ms
  dotnet   conc 8    197,676 req/s   p50 0.028  p99 0.091  max 3.376 ms
  dotnet   conc 64   267,027 req/s   p50 0.129  p99 0.905  max 4.747 ms

=== HTTP 200   127.0.0.1:18081 ===
  ashes    conc 1     27,846 req/s   p50 0.029  p99 0.046  max 1.163 ms
  ashes    conc 8    149,628 req/s   p50 0.040  p99 0.100  max 1.618 ms
  ashes    conc 64   262,759 req/s   p50 0.175  p99 0.960  max 3.501 ms
  dotnet   conc 1     30,677 req/s   p50 0.026  p99 0.043  max 0.867 ms
  dotnet   conc 8    111,897 req/s   p50 0.049  p99 0.203  max 1.655 ms
  dotnet   conc 64   133,091 req/s   p50 0.262  p99 2.143  max 3.142 ms
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
