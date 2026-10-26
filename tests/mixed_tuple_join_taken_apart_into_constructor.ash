// expect: 2000
type Expr =
    | ExprVar(Str)
    | ExprQualifiedVar(Str, Str)

let recursive reverseList (items: List(Str)) (acc: List(Str)) =
    match items with
        | [] -> acc
        | item :: rest -> reverseList(rest)(item :: acc)

let recursive join (separator: Str) (parts: List(Str)) =
    match parts with
        | [] -> ""
        | part :: [] -> part
        | part :: rest -> part + separator + join(separator)(rest)

let qualifiedName parts =
    match parts with
        | [] -> ("", "")
        | name :: moduleParts ->
            ([]
            |> reverseList(moduleParts)
            |> join("."), name)

let variable (reversedParts: List(Str)) =
    match qualifiedName(reversedParts) with
        | (moduleName, name) ->
            if moduleName == ""
            then ExprVar(name)
            else ExprQualifiedVar(moduleName)(name)

let size (expression: Expr) =
    match expression with
        | ExprVar(name) -> Ashes.Text.length(name)
        | ExprQualifiedVar(moduleName, name) -> Ashes.Text.length(moduleName) + Ashes.Text.length(name)

let parts (n: Int) (qualified: Bool) =
    if qualified
    then [Ashes.Text.fromInt(n - n / 10 * 10), "m" + Ashes.Text.fromInt(n - n / 10 * 10)]
    else [Ashes.Text.fromInt(n - n / 10 * 10)]

let recursive rounds (n: Int) (qualified: Bool) (total: Int) =
    if n == 0
    then total
    else
        rounds(n - 1)(!qualified)(total + size(qualified
        |> parts(n)
        |> variable))

0
|> rounds(1000)(false)
|> Ashes.IO.print
