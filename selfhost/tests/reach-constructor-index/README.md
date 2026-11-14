# Result-reach constructor index

Render with `bash selfhost/tests/reach-constructor-index/render.sh`; the project is written to
`work/reach-constructor-index`. The frozen oracle is `ResultReachSummaries.ash` from commit
`61ff1e6a`, before the registry indexed its constructors by name, with its exported types and
values prefixed (`Ref`, `ref_`) so both modules can be imported side by side. `Main.ash` also keeps
the original list walk as `frozenLookupConstructor`.

`Main.ash check <first seed> <last seed>` generates one program per seed and compares, 14
comparisons per program:

- for twelve names, the frozen walk over the registry's constructor list with a lookup in the
  registry's index, which is all the indexed `lookupConstructor` does;
- the constructor list of the indexed registry with the frozen module's, field by field;
- the reach summaries of the whole program from both modules: function keys, reach counts, causes,
  poison flags and must-reach parameters.

A program declares up to three types drawn from plain variants of arity 0 to 2, three-variant
types, records, zero-cost wrappers and aliases, with constructor names from a pool of eight that
includes the builtin `Some`, `None`, `Unit` and `Ok` and one lower-case name. Constructors therefore
repeat across types and shadow builtins with a different arity, copy-typed fields, field names or
sole-nullary flag, so which declaration a lookup returns changes the summaries. Three functions
follow, each with a body for one of the places the reach walk asks for a constructor: a bare name,
an application to one or two arguments, a record literal, a record pattern, a bare pattern name, a
constructor pattern, a call into a sibling function, a tuple, and a match combining them.

```sh
bash selfhost/tests/reach-constructor-index/validate.sh /path/to/ashes s0-plain
```

The validator renders, compiles under an 8 GiB scope and runs every program under a 4 GiB scope.
Evidence is written under ignored `work/reach-constructor-index-<tag>/`.

Validated with stage 0 over seeds 1 to 20,000: 280,000 comparisons agree, over 168,573 registered
constructors; 32,438 of the 60,000 summaries are poisoned and the rest are not. An index that keeps
the last constructor of a name instead of the first fails at seed 1.

The lookup benchmark declares `types` two-constructor types and asks, per round, for one declared
constructor and three names that are none, the mix a walk over function bodies produces. The
summaries benchmark computes the reach summaries of a program of `types` types and `functions`
functions with both modules. Old and new print the same checksum. Stage 0, seconds:

| Shape | Frozen | Indexed |
| --- | ---: | ---: |
| 48,000 lookups, 3,000 constructors | 0.36 s | 0.01 s |
| 96,000 lookups, 6,000 constructors | 1.50 s | 0.06 s |
| summaries, 3,000 constructors, 3,000 functions | 0.21 s | 0.10 s |
| summaries, 6,000 constructors, 6,000 functions | 0.84 s | 0.37 s |

Peak resident memory is unchanged within noise (15.3 MiB for the lookups; 36.2 against 36.0 MiB
and 60.2 against 64.1 MiB for the summaries). These measurements concern the fixture, not a
complete compiler run.
