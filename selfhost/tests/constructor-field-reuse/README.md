# Closed constructor field reuse

Run `bash selfhost/tests/constructor-field-reuse/render.sh` from any shell. This
extracts the actual constructor index, field queries and their dependencies into
`work/constructor-field-reuse`. The checked-in test body is `Tests.ash`; Bash only
extracts production declarations and writes project metadata. The fixture's small
Core state contains exactly the fields read by these helpers. Whole Core lifecycle
validation remains a separate gate.

Compile the generated project with `ASHES_RC_LEAKCHECK=1` under the normal scoped
limits, and repeat with compile-time `ASHES_RC_POISON=1`. Then run:

```bash
bash selfhost/tests/constructor-field-reuse/validate.sh /absolute/path/to/binary work/constructor-field-reuse/validation
```

The validator scopes each native run to4G, requires exact output and exactly one
live-cell counter, rejects a recorded signal, and requires equal fresh-index counts
at1,000 and10,000 repetitions. Both commands can be invoked from fish.

`check` compares 1,000 three-layout sequences at three fresh-variable supplies and
substitutions. Each state checks both original and cached declared fields and five
instantiated argument lists, including open argument types. Layouts cover closed
recursive types, generic schemes, unquantified open bodies, rigid parameters,
quantified schemes with closed bodies, non-named results, two symbol IDs sharing
one result name, function/capability fields, and nullary constructors. Dedicated
checks preserve field order, distinguish missing from nullary, and rebuild changed
views. Fresh supplies stay above declaration and substitution IDs, as required by
scheme instantiation.

The closed view is admitted only when every constructor in a result-name bucket
has no quantified parameters or variable/rigid nodes anywhere in its body.
Instantiation and substitution are identities for this subset. Any rejected layout
disables the entire bucket; it does not leave a partially cached answer.

`old`/`new` repeat100,000 warm queries and both print400000. `1000`/`10000` create a
fresh index per iteration and query its retained fields after its builder returns,
printing4000/40000. The latter checks ownership, not only lookup equivalence.

Focused stage0 evidence: plain and poisoned checks passed; every mode reported one
live cell, with fresh-index1→1 at1,000/10,000. Plain warm queries took0.08s versus
below0.01s, on this tiny fixture; this is not a predicted bootstrap improvement.
Poisoned fixture compilation took4.21s, peak296,556KiB. The existing constructor
lookup fixture also passed1,296 cases and fresh-index1→1 after adding the fourth map.
Actual-Core integration and stage1-built lifetime checks are separate requirements.
