// expect: outside(eet,Stray,Greet) abcde,abcde,abcde,abcde
type GraphError =
    | Conflict(Str, Str, Str)
    | Outside(Str, Str, Str)

let validateName dependency namespace moduleName =
    if moduleName == namespace
    then Ok(Unit)
    else
        namespace
        |> Outside(dependency)(moduleName)
        |> Error

let recursive validateAll dependency namespace paths =
    match paths with
        | [] -> Ok(Unit)
        | path :: rest ->
            match validateName(dependency)(namespace)(path) with
                | Error(error) -> Error(error)
                | Ok(Unit) -> validateAll(dependency)(namespace)(rest)

let describe (result: Result(GraphError, Unit)) =
    match result with
        | Error(Outside(a, b, c)) -> "outside(" + a + "," + b + "," + c + ")"
        | Error(Conflict(a, b, c)) -> "conflict(" + a + "," + b + "," + c + ")"
        | Ok(Unit) -> "ok"

let recursive churn (count: Int) (acc: List(Str)) =
    if count == 0
    then acc
    else churn(count - 1)(Ashes.Text.take("abcdefghij")(5) :: acc)

let run (dependency: Str) = validateAll(dependency)("Gr" + dependency)(["Greet", "Stray", "Other"])

match run("eet") with
    | raw ->
        match churn(4)([]) with
            | noise -> Ashes.IO.print(describe(raw) + " " + Ashes.Text.join(",")(noise))
