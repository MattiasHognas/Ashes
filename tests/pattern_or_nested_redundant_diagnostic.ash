// expect-compile-error: Unreachable match arm: pattern Some(1) is already matched earlier.
type MaybeInt =
    | None
    | Some(Int)

match Some(1) with
    | Some(1) | Some(1) -> 1
    | Some(_) | None -> 0
