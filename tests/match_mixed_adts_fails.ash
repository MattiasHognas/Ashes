// expect-compile-error: Constructor patterns from different ADTs
type Option =
    | None
    | Some(T)

match None with
    | None -> 0
    | Ok(v) -> v
