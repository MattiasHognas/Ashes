# Returned constructor field ownership

The recursive map lookup returns `Some(label)` with a retained reference-counted string parameter. Preserve this source shape: the compiler must account for the arena constructor's field retain.

Compile with leak checking and run `1000` and `10000`. Both print `done`. Stage0 with `GRC_NO_LOOPRESULTCTORFIELDRETAIN` unset reports `1→1`; disabling it restores `1001→10001`. Stage1 before its mirror also reports `1001→10001`. Validate the rebuilt stage1 and plain/poisoned stage0 before accepting the fix.
