// expect: cycle at A after 20
let recursive nameExists n xs =
    match xs with
        | [] -> false
        | h :: t ->
            if h + "" == n
            then true
            else nameExists(n)(t)

let recursive lookupDecl name decls =
    match decls with
        | [] -> None
        | (k, supers) :: tail ->
            if k + "" == name
            then Some(supers)
            else lookupDecl(name)(tail)

let recursive findInReqs reqs decls path =
    match reqs with
        | [] -> None
        | name :: tail ->
            match findCycle(name)(decls)(path) with
                | Some(c) -> Some(c)
                | None -> findInReqs(tail)(decls)(path)
and findCycle name decls path =
    if nameExists(name)(path)
    then Some(name)
    else
        match lookupDecl(name)(decls) with
            | None -> None
            | Some(supers) -> findInReqs(supers)(decls)(name :: path)

let recursive ping n acc =
    if n == 0
    then acc
    else pong(n - 1)(acc + 1)
and pong n acc =
    (let probe = ping(0)(acc)
    in
        if n == 0
        then probe
        else ping(n - 1)(probe + 1))

let decls = [("A", ["B"]), ("B", ["C"]), ("C", ["A"])]

let cycle =
    match findCycle("A")(decls)([]) with
        | Some(c) -> "cycle at " + c
        | None -> "no cycle"

Ashes.IO.print(cycle + " after " + Ashes.Text.fromInt(ping(20)(0)))
