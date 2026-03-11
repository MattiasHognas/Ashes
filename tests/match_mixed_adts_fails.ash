// expect-compile-error: Constructor patterns from different ADTs
type MyMaybe =
    | None
    | Some(T)

type MyResult =
    | Ok(T)
    | Error(T)

match None with
    | None -> 0
    | Ok(v) -> v
