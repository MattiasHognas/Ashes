# Indexed semantic stitching lookups

`FrozenStitching.ash` is `ModuleSemanticStitching.ash` from commit `61ff1e6a`, before its name
lookups were indexed. Its scope and project types and its exported functions are renamed so both
modules can be imported side by side, its pending collection's `reversed` field is renamed because
two copies of the type make an unannotated field access ambiguous, and the types both modules share
are imported from the indexed module. Nothing else differs.

`Main.ash check <first seed> <last seed>` builds one generated project per seed with both modules
and compares them:

- the build outcome: the same error, or scopes that agree field by field (imports, module aliases,
  plain whole-module imports, intrinsic selectors, definitions) and the same next definition id;
- for a project that stitches, every combination of 8 modules, 9 names, 6 name kinds and 8
  visibility boundaries through `resolveStitchedUnqualified`; 8 modules, 11 qualifiers, 9 names and
  6 kinds through `resolveStitchedQualified`; and the module alias, shipped type, intrinsic selector
  and plain-import queries: 8,405 comparisons per project.

A project is three dependency modules and an entry module. Names come from a pool of eight, so
modules export the same names, a name is declared as a value in one module and a type or
constructor in another, and a module redeclares a name now and then. Declarations cover every
top-level form the stitcher collects, exports are drawn per name and namespace, and imports mix
plain and aliased whole-module imports, value and type selectors, intrinsic selectors, a missing
module, repeated modules, dependencies out of order, inline-module source paths and shipped
(`Ashes.`) module names. One import shape puts a module under another module's full name before
that module is imported under an alias of its own; two modules then answer to one qualifier, the
only reachable state in which the order of bindings under a name decides a resolution.

The checker stops after a seed that ends in a missing module and prints the seed to resume at:
compiled by stage 0, the frozen module does not survive a further build in the same process after
reporting one (the indexed module does). `validate.sh` resumes in a new process and sums the
tallies.

```sh
bash selfhost/tests/stitching-lookup-index/validate.sh /path/to/ashes s0-plain
```

The validator compiles under an 8 GiB scope and runs every program under a 4 GiB scope. Evidence
is written under ignored `work/stitching-lookup-index-<tag>/`.

Validated with stage 0 over seeds 1 to 4,000: 7,089,325 comparisons agree. 845 projects stitched,
with 341,340 of their name resolutions finding a definition; the others ended in 93 duplicate
modules, 724 duplicate declarations, 220 missing modules, 888 missing exports, 218 conflicting
imports, 133 conflicting qualifiers and 879 compiler-name collisions, each reported identically.
Reversing the order of the modules listed under one qualifier makes the check fail at seed 17.

The benchmark modes stitch `modules` dependencies of `size` exported values each, ten of them under
names every dependency shares, into an entry module of `size * 4` definitions that imports every
dependency whole, every other one under an alias. `old` and `new` then resolve each exported name
unqualified, qualified, and as a type, and one local name, and print a checksum over the resolved
ids; the validator requires equal checksums. Stage 0, seconds and peak resident memory:

| Modules x definitions | Build, frozen | Build, indexed | Build and resolve, frozen | Build and resolve, indexed |
| --- | ---: | ---: | ---: | ---: |
| 24 x 150 | 1.56 s, 14.7 MiB | 0.01 s, 26.5 MiB | 2.01 s, 14.7 MiB | 0.02 s, 26.5 MiB |
| 48 x 150 | 6.34 s, 22.7 MiB | 0.04 s, 38.5 MiB | 7.65 s, 22.7 MiB | 0.03 s, 38.5 MiB |
| 24 x 600 | 21.95 s, 38.9 MiB | 0.05 s, 74.7 MiB | 27.50 s, 38.9 MiB | 0.06 s, 74.7 MiB |

The indexes cost memory: a map insert leaves the nodes of the path it rebuilt behind, and the
indexed build inserts once per definition by source name, once per definition by compiler name,
once per interface export and once per unqualified import binding. Qualified bindings are not
indexed at all; a qualifier lists the definition indexes of the modules bound under it.

Compiled with `ASHES_RC_LEAKCHECK=1`, the 8 x 100 build ends with 9,545 live cells against the
frozen module's 25,988 (16,745 against 29,588 after resolving). An entry module with no imports
ends with about six live cells per definition where the frozen module ends with one cell in all;
leaving out the compiler-name map or the scope's definition index does not change that count,
and its source is not identified.

These measurements concern the fixture, not a complete compiler run.
