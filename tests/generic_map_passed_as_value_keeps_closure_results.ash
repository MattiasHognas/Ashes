// expect: 6039789
type Sem =
    | SemLeaf(Int)
    | SemNamed(Int, List(Sem))

type Box =
    | label: Str
    | items: List(Int)

let renumber (shift: Int) (value: Sem) =
    match value with
        | SemNamed(id, arguments) -> SemNamed(id + shift)(arguments)
        | other -> other

let viaMapper mapper f xs = mapper(f)(xs)

let recursive measure (values: List(Sem)) (total: Int) =
    match values with
        | [] -> total
        | SemLeaf(v) :: rest -> measure(rest)(total + v)
        | SemNamed(id, arguments) :: rest -> measure(rest)(total + id + measure(arguments)(0))

let recursive boxes (values: List(Box)) (total: Int) =
    match values with
        | [] -> total
        | box :: rest -> boxes(rest)(total + Ashes.Text.byteLength(box.label) + Ashes.Collection.List.length(box.items))

let recursive rounds (n: Int) (total: Int) =
    if n == 0
    then total
    else
        let kept = SemNamed(n)([SemLeaf(2)])
        in
            let xs = [SemLeaf(n), kept, SemNamed(n)([SemLeaf(1)])]
            in
                let identity =
                    measure(viaMapper(Ashes.Collection.List.map)(given (v) -> v)(xs))(0)
                in
                    let captured =
                        measure(viaMapper(Ashes.Collection.List.map)(given (_v) -> kept)(xs))(0)
                    in
                        let nested =
                            measure(xs
                            |> viaMapper(Ashes.Collection.List.map)(renumber(2))
                            |> viaMapper(Ashes.Collection.List.map)(renumber(1)))(0)
                        in
                            let generic =
                                xs
                                |> viaMapper(Ashes.Collection.List.map)(renumber(1))
                                |> Ashes.Collection.List.length
                            in
                                let made =
                                    boxes(viaMapper(Ashes.Collection.List.map)(given (k) -> Box(label = Ashes.Text.fromInt(k), items = [k, k]))([n, n + 1]))(0)
                                in
                                    let again = measure(xs)(0)
                                    in rounds(n - 1)(total + identity + captured + nested + generic + made + again)

0
|> rounds(1000)
|> Ashes.IO.print
