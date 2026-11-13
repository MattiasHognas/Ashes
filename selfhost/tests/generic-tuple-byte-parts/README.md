# Borrowed bytes in a generic tuple result

`Main.ash` exercises a generic tuple updater at Bool and Str replacement types, through a
known call and a generic closure application. It checks two aliases of a Bytes field, the source
used again after the call, a result escaping a locally produced source, fields borrowed from a
local owned tuple, and an unchanged Str field. Programs print `tuple byte parts passed`.

`render-mirror.sh` extracts the stage1 demotion guard helpers from `CoreLowering.ash` and joins
them with the small state adapter and assertions in `MirrorPrelude.ash` and
`MirrorAssertions.ash`. It accepts the exact borrowed demotion and rejects a fresh source,
multiple users, duplicate field stores, a scalar field, an intervening release, and a nonlocal
branch. It also checks the requested source mapping used for owner release. The returned IR and source mapping
are consumed on every round; the positive case must add the request guard. It prints
`tuple byte parts mirror passed`.

Run the focused plain or poisoned checks from any shell using Bash:

```sh
bash selfhost/tests/generic-tuple-byte-parts/validate.sh /absolute/path/to/ashes checkpoint
bash selfhost/tests/generic-tuple-byte-parts/validate.sh /absolute/path/to/stage0 checkpoint-poison poison
```

Poison is a stage0 compile-time switch. The runner uses the heavy-job wrapper and systemd
scopes (8G compile, 4G native, no swap), records elapsed time and peak RSS, requires the expected
output and exactly one live-cell counter, and compares counters at 1,000 and 10,000 rounds.
The stage1 mirror extraction is a focused helper check, not proof that a newly built stage1
compiler has integrated the change. Run the main regression again with that compiler.

## Ownership proof

A tuple with an unresolved sibling remains in the arena. Normally a known Bytes/Str child is
copied there because the arena tuple cannot own an unbalanced counted reference. The optimization
recognizes only the exact seven-instruction borrowed demotion, with one user, in a field stored
once in the sole returned arena tuple, outside a loop. Only non-releasing construction instructions and branches local to the interval
may occur before its tuple store. Fresh unowned values and unproved paths
retain their existing behavior.

When the caller requests tuple parts, the guarded demotion keeps the original value. The
existing part-store protocol retains each counted field once, including duplicate aliases. The
callee records the part in its mask; source owners that no other result field reaches can then
be released. Caller normalization retains the child for its own result, and mask settlement
releases the handed-over reference exactly once. Only this transformed pattern maps back to its
borrowed source in the requested ownership analysis.

Without a request, the original copy runs and no part-store retain runs. Fixed-copyout callers
continue withdrawing the request; their behavior is unchanged. The stage0 switch
`GRC_NO_TUPLESTRINGPARTS=1` disables both the expanded part selector and copy bypass.

## Focused measurements

Before integration, both plain and poisoned stage0 executions of `Main.ash` were flat at
1/1/1 live cells for 1/1,000/10,000 rounds. A separate fixed-copyout `(Int, Bytes, Bool)` caller
passed poisoned execution with one live cell.

An extraction of the unchanged production `ParserState`, `parserStateWithTokens`, and
`parserSetWithSuppressed` performed 10,000 two-setter updates over 65,536 input bytes. With the
rule disabled it took 1.26 seconds and peaked at 8,336 KiB; enabled it finished below the timer's
0.01-second resolution and peaked at 8,352 KiB. Both printed 65,536 and one live cell. These are
focused setter measurements, not an overall bootstrap speedup. Full bootstrap validation remains
necessary.
