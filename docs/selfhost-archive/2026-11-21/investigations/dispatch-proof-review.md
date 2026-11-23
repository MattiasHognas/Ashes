# Isolated dispatch scalar call-part proof review

Candidate, not accepted or integrated.

- Registration entry is called only by CreateDispatchTcoContext for mutual recursion dispatch.
- A fresh structural-walk state resolves immutable lexical parameter aliases; no private shapes
  are written back into FunctionOwnershipSummary or reused by unrelated placement rules.
- Every self-call edge at an admitted ordinal must be passthrough, cons onto that same ordinal,
  or a direct field of a tuple returned by a known full-arity call. At least one field edge required.
- Calls are recorded by reference identity. AST result provenance is not treated as representation.
- The actual LowerMatch result must have tuple type and IsRuntimeManagedResultTemp=true. These are
  the existing conditions supporting runtime match ownership. Every required source must be observed
  before the private ordinal can influence placement. Missing/unreachable evidence fails closed.
- Final layout must be scalar/no-owned-child (CanArenaReset element set); TMC, consumed-tail and
  invariant exclusions remain. A tuple-list parameter cannot qualify via this private rule.
- Placement uses existing post-body refresh, entry normalization, deferred back-edge and exit drops.
  No new release instruction is manufactured by the analysis, and no arena-copy workaround added.
- d2 cannot activate any private ordinal: it has no tuple-result-part edge. Its previous crash was
  caused by broad alias propagation activating unrelated walked/scalar/tuple-list placements.
- New dedicated dictionary/set state is cleared alongside move-analysis state, and private maps
  clone lexical scopes. Pattern and non-alias bindings remove shadowed names.

Outstanding before acceptance: ON/OFF and poison matrix; lexical-shadow/alias negatives; direct
reproducer flatness; actual probe class movement; stage1 diagnosis/mirror (a2 measured31/151);
required tests/docs and final combined gates/census per root scheduling. No commit authorized.

Follow-up after failed late-evidence candidate (2026-11-10):

- Candidate crashed on first a2 plain run. Reverted and archived; this review is not acceptance.
- Post-body representation evidence may arrive too late for earlier cons ownership decisions;
  needs symbolized crash confirmation before another code change.
- Existing compiled-result fact can potentially prove ownership before dispatch loop entry:
  TryGetCompiledFunctionResultRuntimeManaged walks _functionReturnedClosureLabels to the innermost
  compiled body and reads _bodyRuntimeManagedByLabel. Original group members are lowered first.
- Any such proof must also bind to exact lexical FuncKey. Resolving AST name through lowering scope
  alone could confuse a shadowing local function with an outer name. Require resolved label's
  _functionKeyByLabel identity to equal structural lexical scope's function key, exact argument count,
  and positive compiled result fact. Missing label/chain/fact fails closed. No AST eligibility fallback.
- Still must confirm emission/specialization uses the same result contract; do not infer ownership
  from a different closure or reused source name. No pre-entry candidate implemented yet.
