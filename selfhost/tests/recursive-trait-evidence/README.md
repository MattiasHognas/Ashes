# Recursive evidence forwarding

`RecursiveTraitEvidence` rewrites the free recursive name to that same name applied
to its ordered dictionary parameters. Apply it once to the source value before
wrapping dictionary lambdas and before recursive call-shape/TCO analysis. Core
must then lower self references using the physical function type, without adding
evidence again.

The pure AST tests check computed self calls, ordered evidence, aliases, partial
and escaped references, spans and call layout, ordinary/recursive/result-let
scopes, sugar parameters, lambdas, eight pattern forms, guards, handlers,
aggregates, qualified names, and dictionary-name capture. Unknown expression or
pattern variants and actual evidence-name capture return `None`; Core must report
that failure rather than silently retaining an unelaborated self reference.

```bash
bash selfhost/tests/recursive-trait-evidence/validate.sh /absolute/path/to/ashes plain
bash selfhost/tests/recursive-trait-evidence/validate.sh /absolute/path/to/ashes poison
```

The runner checks native success and exact live-cell counts at 1, 1,000 and 10,000
rounds. The test module is also wired into the semantics suite.
`tests/constrained_recursive_dictionary_forwarding.ash` separately exercises a
computed recursive argument at 200,000 iterations for Int and Str; it must also
be run through the integrated Core/backend path. Pure rewriting success does not
establish runtime dictionary lifetime or native tail-call behavior.
