import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.TypeGraphSearch.typeGraphAny
let unchanged semanticType = semanticType

let node depth = SemNamed(depth)("Node" + Ashes.Text.fromInt(depth))([])

let fields semanticType =
    match semanticType with
        | SemNamed(0, _name, []) -> [SemInt]
        | SemNamed(depth, _name, []) -> [node(depth - 1), node(depth - 1)]
        | _ -> []

let recursive run remaining =
    if remaining == 0
    then Ashes.IO.print("done")
    else
        let expected = remaining % 2 == 0
        in
            let target =
                if expected
                then SemInt
                else SemString
            in
                if typeGraphAny(unchanged)(given (value) -> value == target)(fields)(node(remaining % 8 + 1)) == Some(expected)
                then run(remaining - 1)
                else Ashes.IO.panic("unexpected classification")

match Ashes.IO.args with
    | "1000" :: [] -> run(1000)
    | "10000" :: [] -> run(10000)
    | _ -> Ashes.IO.panic("usage: type-graph-search-plateau 1000|10000")
