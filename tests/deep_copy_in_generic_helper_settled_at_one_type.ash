// expect: left-1|right-2|alpha.beta.gamma
import Ashes.Internal
import Ashes.Text
import Ashes.IO
// Two shapes whose copied value's type is still open where the copy is lowered. The first helper is
// generic and every use passes text. The second copies its parameter on the branch lowered first,
// before the other branch's `+` settles the parameter at text.
let copyOptional value =
    match value with
        | None -> None
        | Some(payload) ->
            payload
            |> Ashes.Internal.deepCopy
            |> Some

let appendSegment prefix segment =
    if prefix == ""
    then Ashes.Internal.deepCopy(segment)
    else prefix + "." + segment

let labelled name index = name + "-" + Ashes.Text.fromInt(index)

let show value =
    match value with
        | Some(text) -> text
        | None -> "none"
in
    Ashes.IO.print(show(1
    |> labelled("left")
    |> Some
    |> copyOptional) + "|" + show(2
    |> labelled("right")
    |> Some
    |> copyOptional) + "|" + appendSegment(appendSegment(appendSegment("")("alpha"))("beta"))("gamma"))
