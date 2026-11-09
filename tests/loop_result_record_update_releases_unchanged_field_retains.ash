// expect: 80
// A loop over a table matched out of a call result, partially applied and mapped over records, rebuilds
// its record parameter with `fn with calls = target :: fn.calls` as its result. The arena record keeps
// retained copies of the unchanged fields, which the return's copy retains again; the arena record's
// retains are released once the result holds its own.
type Tree =
    | Leaf(Str)
    | Node(Tree, Tree)

type Fn =
    | label: Str
    | calls: List(Str)
    | tree: Tree

let recursive retarget (known: List((Str, Str))) (fn: Fn) =
    match known with
        | [] -> fn
        | (label, target) :: rest ->
            if label == fn.label
            then fn with calls = target :: fn.calls
            else retarget(rest)(fn)

let recursive collect (functions: List(Fn)) (known: List((Str, Str))) =
    match functions with
        | [] -> known
        | fn :: rest -> collect(rest)((fn.label, fn.label + "_r") :: known)

let run (functions: List(Fn)) =
    match collect(functions)([]) with
        | [] -> functions
        | known ->
            Ashes.Collection.List.map(retarget(known))(functions)

let recursive sumCalls (functions: List(Fn)) (acc: Int) =
    match functions with
        | [] -> acc
        | fn :: rest -> sumCalls(rest)(acc + Ashes.Collection.List.length(fn.calls))

let recursive build (n: Int) (acc: List(Fn)) =
    if n == 0
    then acc
    else build(n - 1)(Fn(label = "lambda_" + Ashes.Text.fromInt(n), calls = ["f"], tree = Leaf("t")) :: acc)

0
|> sumCalls([]
|> build(40)
|> run)
|> Ashes.IO.print
