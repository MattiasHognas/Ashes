// expect: 2000
type Tree =
    | Leaf
    | Node(Tree, Int, Tree)

type Fs =
    | declSpan: Maybe(Int)
    | names: List(Str)

type St =
    | tree: Tree
    | stringLiterals: List(Str)
    | currentSpan: Maybe(Int)
    | functionState: Fs

type Item =
    | ItemAt(Int, Item)
    | ItemType
    | ItemLet(Str)

let withDeclSpan value (state: St) =
    (let group = state.functionState
    in state with functionState = (group with declSpan = value))

let lowerLet (name: Str) (state: St) = state with stringLiterals = Ashes.Collection.List.append(state.stringLiterals)([name])

let finish (trailing: Int) (state: St) = trailing + Ashes.Collection.List.length(state.stringLiterals)

let recursive go items trailing seen state =
    match items with
        | [] -> state |> finish(trailing)
        | ItemAt(span, inner) :: rest ->
            (state with currentSpan = Some(span))
            |> withDeclSpan(Some(span))
            |> go(inner :: rest)(trailing)(seen)
        | ItemType :: rest -> go(rest)(trailing)(seen)(state)
        | ItemLet(name) :: rest ->
            state
            |> lowerLet(name)
            |> go(rest)(trailing)(name :: seen)

let recursive rounds (n: Int) (total: Int) =
    if n == 0
    then total
    else rounds(n - 1)(total + go([ItemAt(1)(ItemLet("x" + Ashes.Text.fromInt(n))), ItemAt(2)(ItemType), ItemLet("y")])(0)([])(St(tree = Node(Leaf)(n)(Leaf), stringLiterals = ["a" + Ashes.Text.fromInt(n), "b"], currentSpan = None, functionState = Fs(declSpan = None, names = []))))

0
|> rounds(500)
|> Ashes.IO.print
