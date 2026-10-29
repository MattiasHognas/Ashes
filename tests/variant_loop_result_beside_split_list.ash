// expect: 1800
type Sem =
    | SemInt
    | SemVariable(Int)
    | SemFunction(Sem, Sem, Maybe(Sem))
    | SemNamed(Int, Str, List(Sem))

type Scheme =
    | quantified: List((Int, Str))
    | body: Sem

type Instantiation =
    | semanticType: Sem
    | supply: Int

let recursive substitute (shift: Int) (semanticType: Sem) =
    match semanticType with
        | SemVariable(id) -> SemVariable(id + shift)
        | SemFunction(argument, result, row) ->
            let a = substitute(shift)(argument)
            in
                let r = substitute(shift)(result)
                in SemFunction(a)(r)(row)
        | SemNamed(symbolId, name, arguments) -> SemNamed(symbolId)(name)(arguments)
        | other -> other

let instantiate (scheme: Scheme) (supply: Int) =
    match scheme with
        | Scheme { body = body } -> Instantiation(semanticType = substitute(supply)(body), supply = supply + 1)

let recursive splitConstructorType semanticType reversed =
    match semanticType with
        | SemFunction(parameterType, resultType, _row) -> splitConstructorType(resultType)(parameterType :: reversed)
        | resultType -> (Ashes.Collection.List.reverse(reversed), resultType)

let layoutFieldTypes (scheme: Scheme) (supply: Int) =
    match instantiate(scheme)(supply) with
        | Instantiation { semanticType = semanticType } -> splitConstructorType(semanticType)([])

let nameLength (semanticType: Sem) =
    match semanticType with
        | SemNamed(_id, name, _arguments) -> Ashes.Text.byteLength(name)
        | _ -> 0

let measure (scheme: Scheme) (supply: Int) =
    match layoutFieldTypes(scheme)(supply) with
        | (fieldTypes, resultType) -> Ashes.Collection.List.length(fieldTypes) + nameLength(resultType)

let recursive rounds (n: Int) (scheme: Scheme) (total: Int) =
    if n == 0
    then total
    else rounds(n - 1)(scheme)(total + measure(scheme)(n))

let scheme =
    Scheme(quantified = [(1, "a")], body = SemFunction(SemVariable(1))(SemNamed(0)("Maybe" + "")([SemVariable(1)]))(None))

0
|> rounds(300)(scheme)
|> Ashes.IO.print
