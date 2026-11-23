// expect: prefixx x
type Expr =
    | Var(Str)
    | Wrap(Expr)
    | Record(List((Str, Expr)))
    | Let(Str, Expr, Expr)

let recursive hide names name =
    match names with
        | [] -> []
        | x :: rest ->
            if x == name
            then rest
            else x :: hide(rest)(name)

let recursive rewrite stage names expression =
    if names == []
    then expression
    else
        let child = rewrite(stage)(names)
        in
            match expression with
                | Var(name) -> Var(stage + name)
                | Wrap(inner) ->
                    inner
                    |> child
                    |> Wrap
                | Let(name, rhs, body) ->
                    body
                    |> rewrite(stage)(hide(names)(name))
                    |> Let(name)(child(rhs))
                | Record(fields) ->
                    fields
                    |> rewriteFields(child)
                    |> Record
and rewriteFields child fields =
    Ashes.Collection.List.map(given (field) ->
        match field with
            | (name, value) -> (name, child(value)))(fields)

let tree = Record([("field", Var("x"))])

let field names =
    match rewrite("prefix")(names)(tree) with
        | Record((_, Var(result)) :: []) -> result
        | _ -> "failed"

Ashes.IO.print(field(["a"]) + " " + field([]))
