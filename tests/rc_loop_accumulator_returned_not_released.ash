// expect: x=b y=c

type Own =
    | Borrowed
    | Consumed

let recursive pick (name: Str) (table: List((Str, List((Str, Own))))) (found: Maybe(List((Str, Own)))) =
    match table with
        | [] -> found
        | (candidate, values) :: rest ->
            if candidate != name
            then pick(name)(rest)(found)
            else
                match found with
                    | Some(_) -> None
                    | None -> pick(name)(rest)(Some(values))

let recursive showAll (values: List((Str, Own))) (acc: Str) =
    match values with
        | [] -> acc
        | (label, own) :: rest ->
            let tag =
                match own with
                    | Borrowed -> "b"
                    | Consumed -> "c"
            in showAll(rest)(acc + label + "=" + tag + " ")

let table = [("a", [("x", Borrowed), ("y", Consumed)])]

Ashes.IO.print(
    match pick("a")(table)(None) with
        | None -> "none"
        | Some(values) -> showAll(values)("")
)
