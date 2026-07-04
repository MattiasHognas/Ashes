# Parallel Worker Overrides

## Goal

Implement scoped worker-count overrides for `Ashes.Parallel`.

Today, `--parallel-workers` is a compiler option that affects the generated executable’s parallel runtime. Despite being passed at compile time, it is not really about making the compiler itself run differently. It defines the executable’s default maximum parallelism for runtime worker spawning.

This feature should make that distinction clearer by allowing Ashes programs to request a lower local worker limit from inside the language/stdlib.

The intended model is:

```text
--parallel-workers = compiled default / hard maximum
Parallel.withWorkers = scoped runtime override
```

So, if a program is compiled with:

```bash
ashes compile --parallel-workers 8
```

then the executable has a maximum worker ceiling of 8. Code may locally request fewer workers:

```ashes
Parallel.withWorkers(4)(
    given () ->
        Parallel.reduce(items)(map)(combine)
)
```

but it should not be able to exceed the compiled maximum.

## Desired Semantics

The effective worker count should be:

```text
effectiveWorkers = min(localOverride, compiledMax)
```

If no local override exists, the existing behavior remains unchanged.

If `--parallel-workers` is not supplied, the compiled max remains the existing default behavior: runtime detection of available CPU count.

Nested overrides should be scoped dynamically:

```ashes
Parallel.withWorkers(8)(
    given () ->
        Parallel.withWorkers(2)(
            given () ->
                Parallel.map(f)(items)
        )
)
```

Inside the inner block, the effective local limit is 2.

After the inner block returns, the outer limit is restored.

## Public API

Add a primitive/scoped function:

```ashes
Parallel.withWorkers(count)(action)
```

Suggested type shape:

```ashes
Int -> (() -> a) -> a
```

Then add convenience wrappers for all currently worker-count-sensitive functions:

```ashes
Parallel.bothWithWorkers(count)(left)(right)

Parallel.mapWithWorkers(count)(f)(items)
Parallel.mapGrainedWithWorkers(count)(grain)(f)(items)

Parallel.reduceWithWorkers(count)(combine)(identity)(f)(items)
Parallel.reduceGrainedWithWorkers(count)(grain)(combine)(identity)(f)(items)
```

These should be stdlib-level convenience functions where possible, implemented in terms of `withWorkers`.

Example:

```ashes
let reduceWithWorkers =
    given(count) ->
    given(combine) ->
    given(identity) ->
    given(f) ->
    given(items) ->
        Parallel.withWorkers(count)(
            given () ->
                Parallel.reduce(combine)(identity)(f)(items)
        )
```

## Affected Existing Functions

The current functions affected by worker count are:

```text
Ashes.Parallel.both
Ashes.Parallel.map
Ashes.Parallel.mapGrained
Ashes.Parallel.reduce
Ashes.Parallel.reduceGrained
```

These all eventually route through either the `both` worker gate or the queued reduce runtime.

## Implementation Notes

Currently, `--parallel-workers` flows into backend codegen as `ParallelWorkerCap`. The generated runtime either compares against a fixed constant or calls the runtime CPU-count detector.

This feature should **not** be treated as a compiler performance option. It is a generated-executable runtime policy.

The implementation likely needs a runtime-visible current worker cap, rather than only a baked constant comparison at every fork site.

One possible approach:

```text
compiledMaxWorkers:
    fixed value from --parallel-workers, or runtime-detected CPU count

currentWorkerOverride:
    dynamically scoped value, default unset

effectiveCap:
    min(compiledMaxWorkers, currentWorkerOverride if set else compiledMaxWorkers)
```

`withWorkers(count)(action)` would:

1. Save the previous override.
2. Set the current override to `min(count, compiledMaxWorkers)`.
3. Run `action`.
4. Restore the previous override before returning.

For now, Ashes may not have exceptions/finally semantics, so restoration can initially follow normal-return semantics. If the language later gains exceptions/panics/unwinding, this should become cleanup-safe.

## Validation Rules

`count` should probably be required to be positive.

Suggested behavior:

```text
count <= 0 => panic or compile/runtime diagnostic
```

Prefer matching existing Ashes style for invalid stdlib/builtin arguments.

## Expected Behavior

Given:

```bash
ashes compile --parallel-workers 8
```

This:

```ashes
Parallel.withWorkers(4)(
    given () ->
        Parallel.reduce(combine)(identity)(f)(items)
)
```

should use at most 4 workers for that scoped computation.

This:

```ashes
Parallel.withWorkers(32)(
    given () ->
        Parallel.reduce(combine)(identity)(f)(items)
)
```

should still use at most 8 workers.

Given no `--parallel-workers`, the compiled max is the existing runtime-detected worker count, and scoped overrides can only reduce that effective limit.

## Tests to Add

Add tests for:

1. Existing parallel functions still work unchanged without `withWorkers`.
2. `withWorkers(1)` forces effectively sequential worker spawning.
3. `withWorkers(n)` restores the previous value after returning.
4. Nested `withWorkers` scopes restore correctly.
5. `mapWithWorkers` behaves the same as `withWorkers + map`.
6. `reduceWithWorkers` behaves the same as `withWorkers + reduce`.
7. `bothWithWorkers` behaves the same as `withWorkers + both`.
8. A local override cannot exceed the compiled `--parallel-workers` ceiling.
9. Invalid worker counts are rejected consistently.

## Design Rationale

`--parallel-workers` should remain useful as a deployment/runtime ceiling. It answers:

> What is the maximum amount of parallelism this executable is allowed to use?

The language-level API answers:

> How much parallelism does this specific computation want?

Keeping both gives Ashes a clean split:

```text
compiler option = global default / hard maximum
stdlib API = local scheduling policy
```

This avoids baking worker-count variants into the compiler for every parallel operation. The compiler/runtime only needs one primitive mechanism, while the stdlib exposes ergonomic helpers such as `mapWithWorkers`, `reduceWithWorkers`, `bothWithWorkers`, etc.
