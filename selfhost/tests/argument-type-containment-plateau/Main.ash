import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.ArgumentTypeContainment.mayContainArgumentType
let unchanged (semanticType: SemanticType) = semanticType

let node depth = SemNamed(depth)("Node" + Ashes.Text.fromInt(depth))([])

let fields (semanticType: SemanticType) =
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
                if mayContainArgumentType(unchanged)(fields)([])(node(remaining % 8 + 1))(target) == expected
                then run(remaining - 1)
                else Ashes.IO.panic("unexpected containment")

match Ashes.IO.args with
    | "1" :: [] -> run(1)
    | "2" :: [] -> run(2)
    | "1000" :: [] -> run(1000)
    | "10000" :: [] -> run(10000)
    | _ -> Ashes.IO.panic("usage: containment-plateau 1|2|1000|10000")
