# challenges/server — TCP server benchmark

A load/latency benchmark for the Ashes TCP server, with a single-file .NET echo server as a reference
point. Built to measure how fast the server responds and how it holds up under pressure, and to be
reused as an optimization baseline (like `challenges/1brc`). Nothing here is discovered or run by CI,
and the `.ash` files are not format-checked by any gate.

Client and server are both Ashes (dogfooding); orchestration is a shell script.

## Pieces

- **`echo.ash`** — a minimal echo server on `127.0.0.1:18080` using `Ashes.Net.Tcp.Server.serve`. One
  `receive` + echo + `close` per connection, so the benchmark measures the server path
  (accept / receive / send / close + scheduling), not handler work.
- **`load.ash`** — a load client: does `count` (its first argument) sequential
  connect/send/recv/close round-trips. Compiled, so it measures the server rather than driver overhead.
- **`dotnet-echo.cs`** — a single-file .NET echo server (`dotnet run dotnet-echo.cs [port]`,
  .NET 10 file-based app) used as a reference point. It is concurrent (async accept loop), the natural
  .NET idiom, so it shows the ceiling; the current Ashes `serve` is sequential, so the gap is the
  headroom the multi-reactor milestone targets.
- **`bench.sh`** — compiles the Ashes pieces, then runs the same concurrency sweep against the Ashes
  server and (if `dotnet` is present) the .NET baseline, printing throughput (req/s) and mean
  round-trip latency per stage, labeled `ashes` / `dotnet`.

## Run

```bash
bash challenges/server/bench.sh                 # 20000 requests, concurrency sweep 1 8 64
bash challenges/server/bench.sh 50000 1 16 128  # REQUESTS then CONCURRENCY levels
```

## Reading the results

`serve` handles connections **sequentially** today, yet it lands in the same ballpark as the
concurrent .NET baseline on this echo workload (the load client is itself heavy, so throughput is
partly client-bound). Run-to-run variance is high on a loaded box, so trust the `ashes`-vs-`dotnet`
comparison *within one invocation* (identical conditions) more than absolute numbers, and interleave
A/B runs when comparing Ashes builds. The multi-reactor milestone (worker-per-core reactors) will be
compared against this baseline. Example shape on a quiet box (illustrative):

```
== ashes (serve, sequential) ==
  ashes    concurrency 1    ~19k req/s   ~0.05 ms
  ashes    concurrency 8    ~92k req/s   ~0.09 ms
  ashes    concurrency 64  ~146k req/s   ~0.44 ms
== dotnet (concurrent baseline) ==
  dotnet   concurrency 1    ~17k req/s   ~0.06 ms
  dotnet   concurrency 8    ~92k req/s   ~0.09 ms
  dotnet   concurrency 64  ~138k req/s   ~0.46 ms
```
