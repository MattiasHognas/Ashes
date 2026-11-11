# Focused validation

The lookup index is a Step F performance change. The compiler changes repair
three independently identified ownership errors exposed by its fresh-inference
plateau. Each has a stage-0 disable switch; empty and unset switches enable it.

- `GRC_NO_SHAREDMATCHFIELD`: an owned call result can alias a shared constructor.
  Watchpoints showed the list field released once by the independent pattern
  binding and again by the still-owning wrapper. The fix records initial wrapper
  uniqueness once: transfer/exclude for unique, retain/full wrapper drop for shared.
  The latter remains correct if another wrapper owner disappears before cleanup.
- `GRC_NO_RESOLVEDMATCHBINDERS`: the old syntactic binder collector registered
  nullary constructor `None` as a variable, falsely tying a new `None` to an old
  loop-owned match result. Actual resolved bindings preserve real `as` aliases.
- `GRC_NO_ARENACURRYBORROW`: the reuse fallback's first map curry stage only
  captures the string into an arena environment. A watchpoint identified a
  redundant Perceus argument retain. Final-body recognition records a placement
  borrow without altering parameter ownership summaries or result facts.

The Release CLI build, four currying-shape guards, nine Perceus lifetime tests,
and three new leak-template plateau/poison tests pass. The direct recursive
handoff fixture alternates unique and shared wrappers through the same flag local
and loop edge, then returns escaping values; plain and poisoned execution print
`passed`, live 1. Lowered IR contains the saved flag at field adoption and cleanup,
with excluded-field drop on transfer and full structural drop on retain.

The original parser-backed stage-0 differential driver passes eleven cases and
has live 4 at both 100 and 1000 fresh inferences. The equivalent prebuilt driver
has live 3 at both counts, for the historical oracle and the index. The exact
span-wrapped fixture is checked recursively against stage-0 parser output,
including every wrapper, byte span, call flag, argument layout and leaf value.
Its stage-0 poisoned old/new measurements are also 3/3.

Unchanged stage 1 also has exact wrapped old/new counts 3/3 at 100/1000,
justifying the scoped mirror exemptions. Independent stage-0 switch controls reproduce the original failures on this
exact wrapped input: shared-field OFF crashes with exit139; resolved-binders
OFF grows403 to4003; curry-borrow OFF grows1203 to12003. All enabled stays flat 3 to 3; explicit empty switches also pass the original
parser-backed and unwrapped controls. The strengthened shared/binder/curry
MemoryCases each stay at 1/1 for 100/1000 iterations under stage-0 plain/poison
and unchanged stage-1 plain execution. Full repository suites and authoritative clean-tree census
are deferred to the coordinated milestone gate.

The original parser-backed stage-1 driver remains a separate failing reproducer.
A parser-only project reproduces its crash without importing ownership inference.
Watchpoints show a returned parser pair and its state being freed before the
caller uses them. The direct AST driver isolates inference; it does not resolve
that parser defect. A separate ordinary arena-copy failure in a list-payload
constructor-alias control was also preserved; the general-RC map-payload alias
control passes baseline and candidate plain/poison checks.
