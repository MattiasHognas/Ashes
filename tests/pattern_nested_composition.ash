// expect: 2
type Bundle =
    | items: List(Int)
    | enabled: Bool

type Wrapped =
    | Wrapped(Bundle)

let value = Wrapped(Bundle(items = [2, 3], enabled = true))
in
    match value with
        | Wrapped(Bundle { items = head :: _, enabled = true | false }) -> Ashes.IO.print(head)
        | Wrapped(Bundle { items = [] }) -> Ashes.IO.print(0)
