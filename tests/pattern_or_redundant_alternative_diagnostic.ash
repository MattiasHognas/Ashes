// expect-compile-error: Unreachable match arm: integer literal 1 is already matched earlier.
match 1 with
    | 1 | 1 -> 1
    | _ -> 0
