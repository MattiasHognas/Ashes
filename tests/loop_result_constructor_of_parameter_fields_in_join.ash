// expect: 20
// retarget's result joins its parameter with a constructor rebuilt from the parameter's fields
// (`Fn(label = fn.label, calls = target :: fn.calls, tree = fn.tree)`) inside a match arm. The arm's
// normalization copies the arena cell, retaining the fields itself, so the cell's own retains of
// the parameter reads are withdrawn instead of leaking once per function.
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
            then Fn(label = fn.label, calls = target :: fn.calls, tree = fn.tree)
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
|> build(10)
|> run)
|> Ashes.IO.print
