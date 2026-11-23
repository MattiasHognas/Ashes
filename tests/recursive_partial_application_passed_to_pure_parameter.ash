// A partial application of a recursive function passed where a parameter's written function type
// is pure: the application's own capability row is closed by that type before it joins the
// caller's, so the caller may still perform a capability in a later argument.
// expect: 7
let recursive lookup (entries: List((Str, Int))) (name: Str) =
    match entries with
        | [] -> None
        | (candidate, value) :: rest ->
            if candidate == name
            then Some(value)
            else lookup(rest)(name)

let parsed (source: Str) =
    match source with
        | "seven" -> 7
        | _ -> Ashes.IO.panic("unknown " + source)

let choose (find: Str -> Maybe(Int)) (fallback: Int) (name: Str) =
    match find(name) with
        | Some(value) -> value
        | None -> fallback

let resolve source =
    choose(lookup([("one", 1)]))(parsed(source))("two")

"seven"
|> resolve
|> Ashes.IO.print
