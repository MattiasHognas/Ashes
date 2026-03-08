// expect-compile-error: Non-exhaustive match expression. Missing case: Some([])
type Option =
    | None
    | Some(T)

let x = Some([])
in 
    match x with
        | None -> 0
        | Some(_ :: rest) -> 1
