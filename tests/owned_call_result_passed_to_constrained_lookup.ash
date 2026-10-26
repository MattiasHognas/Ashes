// expect: 2000
type Fn =
    | label: Str
    | size: Int

let functionLabel (fn: Fn) = fn.label

let recursive lookupAssociation key entries =
    match entries with
        | [] -> None
        | (k, v) :: tail ->
            if k == key
            then Some(v)
            else lookupAssociation(key)(tail)

let recursive addKnown (functions: List(Fn)) (known: List((Str, Str))) (changed: Bool) =
    match functions with
        | [] -> (known, changed)
        | fn :: rest ->
            match lookupAssociation(functionLabel(fn))(known) with
                | Some(_) -> addKnown(rest)(known)(changed)
                | None ->
                    if fn.size > 2
                    then addKnown(rest)((functionLabel(fn), "closure") :: known)(true)
                    else addKnown(rest)(known)(changed)

let recursive count (known: List((Str, Str))) (n: Int) =
    match known with
        | [] -> n
        | _ :: rest -> count(rest)(n + 1)

let run (functions: List(Fn)) =
    match addKnown(functions)([("lambda_" + Ashes.Text.fromInt(1), "x")])(false) with
        | (known, true) -> count(known)(0)
        | (known, false) -> count(known)(100)

let recursive rounds (functions: List(Fn)) (n: Int) (total: Int) =
    if n == 0
    then total
    else rounds(functions)(n - 1)(total + run(functions))

0
|> rounds([Fn(label = "lambda_" + Ashes.Text.fromInt(1), size = 3), Fn(label = "lambda_" + Ashes.Text.fromInt(2), size = 3), Fn(label = "lambda_" + Ashes.Text.fromInt(3), size = 1)])(1000)
|> Ashes.IO.print
