// expect-compile-error: Constructor patterns from different ADTs
type Option =
    | None
    | Some(T)

type Result =
    | Ok(T)
    | Error(T)

match None with
    | None -> 0
    | Ok(v) -> v
