# Captured environment generalization

This focused native fixture invokes the self-hosted inference APIs. It also runs as
`CapturedEnvironmentInferenceTests` in the semantics suite.

The 25 assertions cover ordinary let, let-rec, top-level binding inference, and a
two-member recursive group. Each boundary rejects mixed Int/Str calls when the
binding captures a monomorphic outer function, both with and without an Eq goal.
Same-type calls remain valid. Independent identity bindings remain polymorphic at
Int and Str, also with and without Eq. A direct scheme case covers substitution
chains, free outer constraint and capability-row variables, and quantified IDs
that must be excluded before following the substitution.

Run from the worktree with a built stage0 compiler:

```bash
bash selfhost/tests/inference-environment/validate.sh /absolute/path/to/ashes plain
bash selfhost/tests/inference-environment/validate.sh /absolute/path/to/ashes poison
```

The runner applies the required memory scopes, writes evidence under ignored
`work/inference-environment/`, and checks the exact success marker and one final
`ashes-rc-live 1` counter. Poison is a compile-time stage0 option; these commands
do not establish that a self-hosted compiler honors the same environment switch.
Full actual-Core and bootstrap validation are separate gates.
