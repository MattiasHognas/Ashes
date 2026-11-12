# Type resolution indexes

This fixture compares indexed name, provenance, zero-cost representation and external-destructor queries against the original ordered-list predicates over every sequence of up to four declarations selected from seven declaration variants (2,801 contexts). The variants deliberately reuse names and IDs across nominal, zero-cost, alias and external declarations.

Additional checks cover parameter shadowing, generic zero-cost substitution, arity diagnostics, unknown names and monotonically increasing symbol allocation. Fresh contexts use runtime-derived names and IDs at 1,000 and 10,000 rounds; their maps must be released with the context.

Compile with `ASHES_RC_LEAKCHECK=1` and run with argument `1000` or `10000`. Repeat with stage0 plain and `ASHES_RC_POISON=1`, then stage1 plain. Stage1 does not implement poison instrumentation. All compiler/program invocations must use the scoped memory budgets documented for Step F.

Expected stdout: `resolution index ok`. Live-cell counts must be identical at both round counts.

Validation for the index change: stage0 plain and poisoned both report `1→1`; stage1 reports `1201→1201` with the exhaustive branching checks. The identical fixture against the original list-based TypeResolution module also reports `1201→1201` under stage1. Without the one-time exhaustive checks, the fresh-context fixture reports `1→1` in all three modes. Thus the fixed stage1 retention in the exhaustive checks predates this change; the fresh-context slope remains zero.
