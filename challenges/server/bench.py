#!/usr/bin/env python3
"""Load/latency benchmark for the Ashes TCP echo server (challenges/server/echo.ash).

Each request is one connection: connect -> send payload -> read echo -> close. That is exactly what
the echo server does per connection, so this measures the server path (accept, receive, send, close,
and scheduling), not handler work.

Two things are reported, matching what we want to optimize:
  - response speed:  round-trip latency percentiles (p50/p90/p99) at concurrency 1
  - load handling:   throughput (requests/sec) and latency percentiles at N concurrent connections,
                     showing how tail latency degrades as concurrency climbs

The server is sequential today (one connection at a time), so concurrency mainly exposes queuing —
that is the honest baseline the multi-reactor milestone will be A/B'd against. Interleave runs when
comparing builds; a loaded box adds variance.

Usage:
  python3 bench.py [--host H] [--port P] [--requests N] [--concurrency C ...] [--payload-bytes B]
Start the server first:  ./echo &   (binds 127.0.0.1:18080)
"""
import argparse
import socket
import statistics
import sys
import threading
import time


def one_request(host, port, payload, timeout):
    """Perform one connection round-trip; return latency in seconds, or None on error."""
    t0 = time.perf_counter()
    try:
        with socket.create_connection((host, port), timeout=timeout) as s:
            s.settimeout(timeout)
            s.sendall(payload)
            need = len(payload)
            got = 0
            while got < need:
                chunk = s.recv(need - got)
                if not chunk:
                    break
                got += len(chunk)
        return time.perf_counter() - t0
    except OSError:
        return None


def percentiles(latencies_ms):
    if not latencies_ms:
        return {}
    xs = sorted(latencies_ms)
    def pct(p):
        if len(xs) == 1:
            return xs[0]
        i = min(len(xs) - 1, int(round((p / 100.0) * (len(xs) - 1))))
        return xs[i]
    return {
        "min": xs[0], "p50": pct(50), "p90": pct(90), "p99": pct(99),
        "max": xs[-1], "mean": statistics.fmean(xs),
    }


def run_load(host, port, total_requests, concurrency, payload, timeout):
    """Drive total_requests across `concurrency` worker threads; return (elapsed, latencies_ms, errors)."""
    per_worker = [total_requests // concurrency] * concurrency
    for i in range(total_requests % concurrency):
        per_worker[i] += 1

    latencies = []
    errors = [0]
    lock = threading.Lock()

    def worker(count):
        local = []
        err = 0
        for _ in range(count):
            dt = one_request(host, port, payload, timeout)
            if dt is None:
                err += 1
            else:
                local.append(dt * 1000.0)
        with lock:
            latencies.extend(local)
            errors[0] += err

    threads = [threading.Thread(target=worker, args=(c,)) for c in per_worker if c > 0]
    t0 = time.perf_counter()
    for t in threads:
        t.start()
    for t in threads:
        t.join()
    elapsed = time.perf_counter() - t0
    return elapsed, latencies, errors[0]


def report(label, elapsed, latencies, errors, total):
    ok = len(latencies)
    rps = ok / elapsed if elapsed > 0 else 0.0
    p = percentiles(latencies)
    print(f"[{label}]")
    print(f"  requests: {total}  ok: {ok}  errors: {errors}  elapsed: {elapsed:.3f}s")
    print(f"  throughput: {rps:,.0f} req/s")
    if p:
        print(f"  latency ms: min {p['min']:.2f}  p50 {p['p50']:.2f}  "
              f"p90 {p['p90']:.2f}  p99 {p['p99']:.2f}  max {p['max']:.2f}  mean {p['mean']:.2f}")
    print()


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--host", default="127.0.0.1")
    ap.add_argument("--port", type=int, default=18080)
    ap.add_argument("--requests", type=int, default=5000, help="total requests per stage")
    ap.add_argument("--concurrency", type=int, nargs="+", default=[1, 8, 64],
                    help="concurrency levels to sweep (first is also the latency stage)")
    ap.add_argument("--payload-bytes", type=int, default=32)
    ap.add_argument("--timeout", type=float, default=5.0)
    ap.add_argument("--warmup", type=int, default=200)
    args = ap.parse_args()

    payload = b"x" * args.payload_bytes

    # Wait for the server to accept, and warm up.
    deadline = time.time() + 10
    while time.time() < deadline:
        if one_request(args.host, args.port, payload, args.timeout) is not None:
            break
        time.sleep(0.1)
    else:
        print(f"could not reach server at {args.host}:{args.port} — start ./echo first", file=sys.stderr)
        return 1
    for _ in range(args.warmup):
        one_request(args.host, args.port, payload, args.timeout)

    print(f"target {args.host}:{args.port}  requests/stage={args.requests}  payload={args.payload_bytes}B\n")
    for c in args.concurrency:
        elapsed, latencies, errors = run_load(args.host, args.port, args.requests, c, payload, args.timeout)
        label = f"latency (concurrency 1)" if c == 1 else f"load (concurrency {c})"
        report(label, elapsed, latencies, errors, args.requests)
    return 0


if __name__ == "__main__":
    sys.exit(main())
