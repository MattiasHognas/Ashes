// expect: 11

type State =
    | names: List(Str)
    | count: Int
    | bindings: List(Str)

type Group =
    | state: State
    | members: List(Str)
    | error: Maybe(Str)

type Lowered =
    | state: State
    | temp: Int
    | error: Maybe(Str)

type Item =
    | ItemPlain(Str)
    | ItemWrapped(Item)
    | ItemMany(List(Str))

let recursive prepare (names: List(Str)) (state: State) (reversed: List(Str)) =
    match names with
        | [] -> Group(state = state, members = Ashes.Collection.List.reverse(reversed), error = None)
        | name :: rest ->
            match state with
                | State { count = count } -> prepare(rest)((state with names = [name], count = count + 1))(name :: reversed)

let relabel (id: Int) (prepared: Group) =
    match prepared with
        | Group { state = state, members = member :: [], error = None } -> Group(state = state, members = [member + Ashes.Text.fromInt(id)], error = None)
        | _ -> prepared

let openGroup (outer: List(Str)) (prepared: Group) =
    match prepared with
        | Group { state = failed, error = Some(error) } -> Lowered(state = failed, temp = 0, error = Some(error))
        | Group { state = state, members = members, error = None } -> Lowered(state = (state with bindings = members), temp = Ashes.Collection.List.length(outer), error = None)

let recursive loop (items: List(Item)) wrap seen (state: State) =
    match items with
        | [] -> state.count + Ashes.Collection.List.length(state.names)
        | ItemWrapped(inner) :: rest -> loop(inner :: rest)(wrap)(seen)(state)
        | ItemMany(_names) :: rest -> loop(rest)(wrap)(seen)(state)
        | ItemPlain(item) :: rest ->
            match state with
                | State { bindings = outer, count = id } ->
                    match []
                    |> prepare([item])(state)
                    |> relabel(id)
                    |> openGroup(outer) with
                        | Lowered { state = failed, error = Some(_error) } -> failed.count
                        | Lowered { state = opened, error = None } -> loop(rest)(wrap)(wrap(item) :: seen)(opened)

let recursive items (n: Int) (acc: List(Item)) =
    if n <= 0
    then acc
    else items(n - 1)(ItemWrapped(ItemPlain("walk")) :: acc)

State(names = ["x"], count = 0, bindings = [])
|> loop(items(10)([]))(given (s: Str) -> [s])([])
|> Ashes.IO.print
