// A match arm rebuilds the list a call returned from two pattern-bound elements and the rest,
// inside a constructor it returns: the second cell's head must outlive the matched list.
// expect: name1,name2 name1,name2,name3
type Problem =
    | Mixed(List(Str))
    | Other(Str)

let recursive gather (n: Int) (acc: List(Str)) =
    if n == 0
    then acc
    else gather(n - 1)("name" + Ashes.Text.fromInt(n) :: acc)

let check (count: Int) =
    match gather(count)([]) with
        | first :: second :: more -> Some(Mixed(first :: second :: more))
        | _ -> None

let render (found: Maybe(Problem)) =
    match found with
        | Some(Mixed(names)) -> Ashes.Text.join(",")(names)
        | Some(Other(text)) -> text
        | None -> "none"

Ashes.IO.print(render(check(2)) + " " + render(check(3)))
