// expect: ok
type Option =
    | None
    | Some(T)

let _x = Some(42)
in Ashes.IO.print("ok")
