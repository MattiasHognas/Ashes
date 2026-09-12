# challenges/xlang/

Ports of four compute-bound Benchmarks Game challenges to .NET, Rust, Go, and OCaml, so the
Ashes implementations in `challenges/` can be measured against the same program in other
languages. Like the rest of `challenges/`, nothing here is part of the test or example suites and
none of it is built or checked by CI.

```sh
challenges/xlang/bench.sh                # every benchmark at its standard workload
challenges/xlang/bench.sh nbody 1000     # one benchmark at a chosen workload
RUNS=5 challenges/xlang/bench.sh fannkuch
```

The harness builds each port, checks its output against the Ashes program's output, and reports
hyperfine wall-clock means. A toolchain that is not installed is reported as skipped. Output
comparison normalizes trailing blank lines, because the Ashes programs print a string that already
ends in a newline and `io.print` adds one more; everything before that must match byte for byte.

## What the ports are, and are not

They are **idiomatic for their language**, not transliterations of the Ashes code: mutable arrays
and in-place updates, matching the reference implementations. That is the honest basis for "how
does Ashes compare", but it means a gap is not automatically a codegen gap — part of it is the cost
of the immutable rebuild the Ashes program performs and the port does not.

Constants and workloads are taken from the Ashes sources, so the two are solving the same problem
at the same size. Every port's output is verified against the Ashes output on every run rather than
assumed.

## Results

2026-09-12, 32-thread AMD Ryzen 9 9950X3D, Linux x64, single-threaded, hyperfine means of 3 runs
after 1 warmup. Ashes `-O2`; .NET 10.0.301 Release (JIT, workstation GC); `rustc -O` 1.95;
Go 1.27.1; `ocamlopt -unsafe -inline 100` 5.5.0. All outputs matched.

| Benchmark | Ashes | .NET 10 | Rust | Go | OCaml |
|---|---|---|---|---|---|
| spectral-norm 5,500 | **0.874s** | 0.902s | **0.870s** | 0.893s | 1.110s |
| binary-trees 21 | **1.219s** | 2.310s | 7.698s | 2.925s | 2.481s |
| n-body 50,000,000 | 13.370s | 1.924s | **1.186s** | 2.461s | 1.800s |
| fannkuch-redux 11 | 25.294s | 2.045s | 1.853s | **1.687s** | 2.023s |

Reading these honestly:

- **spectral-norm** is a dead heat with Rust. A float-heavy O(N^2) kernel over immutable lists
  matching `rustc -O` over arrays is a real result.
- **binary-trees** Ashes wins outright, but the Rust entry here uses a `Box` per node, so that
  column measures `malloc`, not Rust — the Benchmarks Game Rust entry uses a typed arena and is far
  faster. The fair reading is that the Ashes arena beats .NET's gen0 bump allocator and Go's
  allocator, and beats naive per-node `malloc` by 6x.
- **n-body** and **fannkuch-redux** are the two programs that repeatedly *update* a structure, and
  both lose by 11-15x. The cause is measured, not guessed: at fannkuch N=10 Ashes executes 46.9
  billion instructions against Rust's 1.25 billion, with cache misses negligible in both, and
  `--explain reuse` shows in-place reuse firing nowhere in the program. Growth per N is identical
  to Rust and Go (12.5x vs 12.3x vs 12.1x from N=10 to N=11), so it is a constant factor rather
  than an asymptotic difference.

## Caveats

- The distro `ocamlopt` is frequently built without flambda, which makes `-O3` unavailable and caps
  cross-function inlining. Check `ocamlopt -config | grep flambda` before reading the OCaml column
  as representative.
- The .NET numbers include JIT warmup, which is negligible at these workloads except on
  spectral-norm.
- Every port is single-threaded, as are the Ashes programs. Any parallel result belongs in a
  separate column, not this table.
