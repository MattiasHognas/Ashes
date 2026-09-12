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
| n-body 50,000,000 | 1.80s | 1.924s | **1.186s** | 2.461s | 1.800s |
| fannkuch-redux 11 | 25.294s | 2.045s | 1.853s | **1.687s** | 2.023s |

Reading these honestly:

- **spectral-norm** is a dead heat with Rust. A float-heavy O(N^2) kernel over immutable lists
  matching `rustc -O` over arrays is a real result.
- **binary-trees** Ashes wins outright, but the Rust entry here uses a `Box` per node, so that
  column measures `malloc`, not Rust — the Benchmarks Game Rust entry uses a typed arena and is far
  faster. The fair reading is that the Ashes arena beats .NET's gen0 bump allocator and Go's
  allocator, and beats naive per-node `malloc` by 6x.
- **n-body** went from last to competitive by dropping the `List(Body)` for a fixed five-body
  record and evaluating each pair once (14.3 s to 1.8 s); see that challenge's README. Read its
  row with the caveat below: the Ashes program writes its ten pair interactions out, while these
  ports loop, and that is worth about 10%. Measured against an OCaml port unrolled the same way
  (1.61 s), Ashes is 9% slower rather than 3% faster -- third behind Rust and OCaml, not second.
  Still a good result for a program allocating a fresh record per step against one mutating in
  place.
- **fannkuch-redux** is the remaining gap, and the cause is measured, not guessed: at N=10 Ashes executes 46.9
  billion instructions against Rust's 1.25 billion, with cache misses negligible in both, and
  `--explain reuse` shows in-place reuse firing nowhere in the program. Growth per N is identical
  to Rust and Go (12.5x vs 12.3x vs 12.1x from N=10 to N=11), so it is a constant factor rather
  than an asymptotic difference.

## The immutable-to-immutable comparison

The table above measures each language in its idiomatic form, which for .NET, Rust, Go and OCaml
means mutable arrays. That answers "how does Ashes compare", but it does not isolate what Ashes'
constraints cost, because those ports are doing something Ashes forbids.

`ml-immutable/` holds a second set of OCaml ports written inside Ashes' rules: lists and records,
recursion and `match`, **no arrays, no `ref`, no loops, no mutation**. Each mirrors its `.ash`
program operation for operation, and every output is verified identical.

| Benchmark | Ashes | OCaml immutable | | OCaml mutable |
|---|---|---|---|---|
| spectral-norm 5,500 | **0.870s** | 2.577s | **2.96x faster** | 1.110s |
| binary-trees 21 | **1.216s** | 2.453s | **2.02x faster** | 2.481s |
| n-body 50,000,000 | **1.769s** | 1.895s | **1.07x faster** | 1.810s |
| fannkuch-redux 11 | 24.996s | **3.733s** | 6.70x slower | 2.023s |

**Ashes beats immutable OCaml on three of the four.** Immutability costs OCaml 2.3x on
spectral-norm and 1.4x on binary-trees; Ashes pays neither.

fannkuch-redux is the outlier and the shape is diagnostic. It does nothing but rewrite a small
list at high rate -- allocate and immediately discard, with almost nothing surviving. That is the
best case for a generational collector, where allocation is a pointer bump and a minor collection
copies almost nothing, and the worst case for reference counting, which pays an increment and a
decrement per cell however briefly it lives. The other three either allocate in bulk the arena
reclaims at once (binary-trees) or build a list once and then read it (spectral-norm's folds).

So the honest summary is not "immutable code is slower here". It is that Ashes' arena and
reference-counted model is competitive-to-better on ordinary immutable workloads, and loses
specifically to a generational collector on very high-rate small-object churn.

These ports are not part of the default `bench.sh` run. Build one with:

```sh
ocamlopt -unsafe -inline 100 -o /tmp/fannkuch challenges/xlang/ml-immutable/fannkuch.ml
```

## Caveats

- The distro `ocamlopt` is frequently built without flambda, which makes `-O3` unavailable and caps
  cross-function inlining. Check `ocamlopt -config | grep flambda` before reading the OCaml column
  as representative.
- The .NET numbers include JIT warmup, which is negligible at these workloads except on
  spectral-norm.
- Every port is single-threaded, as are the Ashes programs. Any parallel result belongs in a
  separate column, not this table.
- **Loop shape is not matched across the table.** The Ashes n-body writes out its ten pairwise
  interactions while every port loops over a fixed array. Unrolling is worth ~10% here (an
  unrolled OCaml n-body runs 1.61 s against 1.78 s looped), so that row flatters Ashes by roughly
  that much. The other three benchmarks use the same loop structure on both sides. That
  unrolled port is checked in as `ml/nbody_unrolled.ml` so the figure can be reproduced; it is not
  part of the default run.
