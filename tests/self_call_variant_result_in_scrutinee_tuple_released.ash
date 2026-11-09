// expect: 3
type Sem =
    | SemInt
    | SemNamed(Str, List(Sem))

let toSem (value: Int) =
    if value > 100
    then SemNamed("big")([])
    else SemInt

let semText (semanticType: Sem) =
    match semanticType with
        | SemInt -> Some("Int")
        | _ -> None

let recursive layout (values: List(Int)) (index: Int) =
    match values with
        | [] -> Some([])
        | value :: rest ->
            match (value
            |> toSem
            |> semText, layout(rest)(index + 1)) with
                | (Some(text), Some(found)) -> Some((index * 8, text) :: found)
                | _ -> None

let layoutSize (values: List(Int)) =
    match layout(values)(0) with
        | Some(found) -> Ashes.Collection.List.length(found)
        | None -> 0

let recursive loop (n: Int) (acc: Int) =
    if n == 0
    then acc
    else loop(n - 1)(acc + layoutSize([n]))
in
    0
    |> loop(3)
    |> Ashes.Text.fromInt
    |> Ashes.IO.print
