// expect-compile-error: An as-pattern must bind a lower-case name other than '_'.
// fmt-skip: the invalid upper-case alias is the syntax under test.
match [1] with
    | _ as Whole -> 1
