import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.TypeInference
import AshesCompiler.Semantics.TypeGraphIndex
import AshesCompiler.Semantics.TypeGraphQueries
let recursive nested depth value =
    if depth == 0
    then value
    else nested(depth - 1)(SemList(value))

let recursive repeat remaining root index =
    if remaining == 0
    then Ashes.IO.print("graph traversal passed")
    else
        match typeGraphDroppable(root)(index) with
            | (true, next) -> repeat(remaining - 1)(root)(next)
            | (false, _) -> Ashes.IO.panic("drop mismatch")

let run depth rounds =
    Unit
    |> emptyTypeEnvironment
    |> emptyTypeGraphIndex
    |> repeat(rounds)(nested(depth)(SemInt))

let recursive coldSmall remaining =
    if remaining == 0
    then Ashes.IO.print("graph traversal passed")
    else
        match Unit
        |> emptyTypeEnvironment
        |> emptyTypeGraphIndex
        |> typeGraphDroppable(nested(8)(SemInt)) with
            | (true, _) -> coldSmall(remaining - 1)
            | (false, _) -> Ashes.IO.panic("cold drop mismatch")

let growing argument = SemNamed(31)("Growing")([argument])

// This sequence also runs against the unchanged index/query implementation.
let nonregular unit =
    (let environment = emptyTypeEnvironment(Unit) with constructorFieldGroups = [ConstructorFieldGroup(groupSymbolId = 31, groupTypeName = "Growing", groupIsGeneric = true, groupFields = [], groupGenericFields = [("Grow", [growing(SemList(SemVariable(71)))], [SemVariable(71)])])]
    in
        match environment
        |> emptyTypeGraphIndex
        |> typeGraphResource(growing(SemInt)) with
            | (resource, next) ->
                match typeGraphDroppable(growing(SemInt))(next) with
                    | (droppable, final) ->
                        if !resource && droppable && next.nodeCount == 2 && final.nodeCount == 3
                        then Ashes.IO.print("nonregular discovery passed")
                        else Ashes.IO.panic("nonregular discovery mismatch"))

match Ashes.IO.args with
    | "small" :: [] -> run(8)(1000000)
    | "large" :: [] -> run(128)(10000)
    | "cold-small" :: [] -> coldSmall(1000000)
    | "nonregular" :: [] -> nonregular(Unit)
    | _ -> Ashes.IO.panic("mode")
