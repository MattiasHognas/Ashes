type Tree =
    | Leaf(Str)
    | Nested(Tree)
    | Many(List(Tree))

type Job =
    | value: Tree
    | path: List(Tree)

type WalkResult =
    | ok: Bool
    | known: List(Tree)

let resolve value environment =
    if environment == 0
    then value
    else Nested(value)

let recursive prepend values path pending =
    match values with
        | [] -> pending
        | value :: rest -> Job(value = value, path = path) :: prepend(rest)(path)(pending)

let recursive walk (pending: List(Job)) (visited: List(Tree)) (known: List(Tree)) (environment: Int) (flag: Bool) =
    match pending with
        | [] -> WalkResult(ok = true, known = known)
        | Job { value = value, path = path } :: rest ->
            let resolved = resolve(value)(environment)
            in
                match resolved with
                    | Leaf(_text) -> walk(rest)(visited)(known)(environment)(flag)
                    | Nested(child) -> walk(Job(value = child, path = path) :: rest)(visited)(known)(environment)(flag)
                    | Many(children) ->
                        walk(prepend(children)(path)(rest))(visited)(known)(environment)(flag)

let recursive run n =
    if n == 0
    then Ashes.IO.print("done")
    else
        let result =
            walk([Job(value = Many([n
            |> Ashes.Text.fromInt
            |> Leaf
            |> Nested]), path = [])])([])([])(0)(false)
        in
            if result.ok
            then run(n - 1)
            else Ashes.IO.panic("bad result")

// A completed job escapes through a sibling accumulator and is read after pending is gone.
// Its aggregate head and Tree field must have their own references before pending is released.
let recursive escapingWalk (pending: List(Job)) (saved: List(Job)) =
    match pending with
        | [] -> saved
        | (Job { value = value, path = path } as job) :: rest ->
            let resolved = resolve(value)(0)
            in
                match resolved with
                    | Leaf(_text) -> escapingWalk(rest)(job :: saved)
                    | Nested(child) -> escapingWalk(Job(value = child, path = path) :: rest)(saved)
                    | Many(children) ->
                        escapingWalk(prepend(children)(path)(rest))(saved)

let escapingCheck seed =
    (let text = "owned-head-" + Ashes.Text.fromInt(seed)
    in
        match escapingWalk([Job(value = Many([Nested(Leaf(text))]), path = [])])([]) with
            | Job { value = Leaf(actual), path = [] } :: [] -> actual == text
            | _ -> false)

let recursive runEscaping n =
    if n == 0
    then Ashes.IO.print("escaped head ok")
    else
        if escapingCheck(n)
        then runEscaping(n - 1)
        else Ashes.IO.panic("escaped head mismatch")

// A sibling keeps the borrowed children list from an owned result. Placement must preserve
// that sibling too; promoting pending alone must not permit dropping its borrowed backing value.
let recursive borrowedSiblingWalk (pending: List(Job)) (held: List(Tree)) =
    match pending with
        | [] -> held
        | Job { value = value, path = path } :: rest ->
            let resolved = resolve(value)(0)
            in
                match resolved with
                    | Leaf(_text) -> borrowedSiblingWalk(rest)(held)
                    | Nested(child) -> borrowedSiblingWalk(Job(value = child, path = path) :: rest)(held)
                    | Many(children) ->
                        borrowedSiblingWalk(prepend(children)(path)(rest))(children)

let borrowedSiblingCheck seed =
    (let text = "held-child-" + Ashes.Text.fromInt(seed)
    in
        match borrowedSiblingWalk([Job(value = Many([Nested(Leaf(text))]), path = [])])([]) with
            | Nested(Leaf(actual)) :: [] -> actual == text
            | _ -> false)

let recursive runBorrowedSibling n =
    if n == 0
    then Ashes.IO.print("borrowed sibling ok")
    else
        if borrowedSiblingCheck(n)
        then runBorrowedSibling(n - 1)
        else Ashes.IO.panic("borrowed sibling mismatch")

match Ashes.IO.args with
    | "borrowed1000" :: [] -> runBorrowedSibling(1000)
    | "borrowed10000" :: [] -> runBorrowedSibling(10000)
    | "escape1000" :: [] -> runEscaping(1000)
    | "escape10000" :: [] -> runEscaping(10000)
    | "1000" :: [] -> run(1000)
    | "10000" :: [] -> run(10000)
    | _ -> Ashes.IO.panic("mode")
