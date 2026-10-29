// expect: 3300
type Sem =
    | SemInt
    | SemVariable(Int)
    | SemFunction(Sem, Sem, Maybe(Sem))
    | SemNamed(Int, Str, List(Sem))

let recursive substitute (shift: Int) (semanticType: Sem) =
    match semanticType with
        | SemVariable(id) -> SemVariable(id + shift)
        | SemFunction(argument, result, row) ->
            SemFunction(substitute(shift)(argument))(substitute(shift)(result))(row)
        | other -> other

let recursive splitConstructorType semanticType reversed =
    match semanticType with
        | SemFunction(parameterType, resultType, _row) -> splitConstructorType(resultType)(parameterType :: reversed)
        | resultType -> (Ashes.Collection.List.reverse(reversed), resultType)

let nameLength (semanticType: Sem) =
    match semanticType with
        | SemNamed(_id, name, _arguments) -> Ashes.Text.byteLength(name)
        | _ -> 0

let measure (body: Sem) (supply: Int) =
    match splitConstructorType(substitute(supply)(body))([]) with
        | (fieldTypes, resultType) -> Ashes.Collection.List.length(fieldTypes) + nameLength(resultType)

let recursive arrows (count: Int) (result: Sem) =
    if count == 0
    then result
    else
        None
        |> SemFunction(SemVariable(count))(result)
        |> arrows(count - 1)

let recursive rounds (n: Int) (body: Sem) (total: Int) =
    if n == 0
    then total
    else rounds(n - 1)(body)(total + measure(body)(n))

0
|> rounds(300)([SemVariable(1)]
|> SemNamed(0)("Maybe" + "")
|> arrows(6))
|> Ashes.IO.print
