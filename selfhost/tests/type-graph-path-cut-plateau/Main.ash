import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.TypeGraphSearch
let identity (value: SemanticType) = value

let target (value: SemanticType) = value == SemOpaque("Hit")

let node (n: Int) = SemNamed(n)("N" + Ashes.Text.fromInt(n))([])

let root = SemNamed(90)("Root")([SemInt])

let changedRoot = SemNamed(90)("Root")([SemList(SemInt)])

let recursive edges (n: Int) =
    if n == 0
    then [(node(0), [])]
    else (node(n), [node(n - 1), node(n - 1)]) :: edges(n - 1)

let fixture = (root, [changedRoot, node(4)]) :: (changedRoot, [SemOpaque("Hit")]) :: edges(4)

let recursive children (wanted: SemanticType) (entries: List((SemanticType, List(SemanticType)))) =
    match entries with
        | [] -> []
        | (key, values) :: rest ->
            if key == wanted
            then values
            else children(wanted)(rest)

let recursive run (count: Int) (regular: Bool) =
    if count == 0
    then Ashes.IO.print("done")
    else
        let answer =
            if regular
            then
                typeGraphAny(identity)(target)(given (value) -> children(value)(fixture))(root) == None
            else
                !typeGraphAnyWithPathCuts(identity)(target)(given (value) -> children(value)(fixture))(root)
        in
            if answer
            then run(count - 1)(regular)
            else Ashes.IO.panic("unexpected result")

match Ashes.IO.args with
    | "new1000" :: [] -> run(1000)(false)
    | "new10000" :: [] -> run(10000)(false)
    | "old1000" :: [] -> run(1000)(true)
    | "old10000" :: [] -> run(10000)(true)
    | _ -> Ashes.IO.panic("mode required")
