export (
    value widthOf,
    value make,
)

type Cfg =
    | width: Int
    | label: Str

let make (w: Int) = Cfg(width = w, label = "l")

let widthOf (c: Cfg) =
    match c with
        | Cfg { width = w, label = _label } -> w
