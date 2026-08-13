// expect-compile-error: Every alternative of an or-pattern must bind exactly the same names.
match 1 with
    | _ | value -> 0
