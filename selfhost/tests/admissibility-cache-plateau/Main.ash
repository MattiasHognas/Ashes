import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.AdmissibilityCache
let box id argument = SemNamed(id)("Box")([argument])

let recursive fill remaining entries =
    if remaining == 0
    then entries
    else
        entries
        |> rememberAdmissibilityAnswer(box(remaining)(SemInt))(remaining % 2 == 0)
        |> fill(remaining - 1)

let check unit =
    (let first = box(1)(SemInt)
    in
        let second = box(2)(SemInt)
        in
            let entries = rememberAdmissibilityAnswer(first)(true)([])
            in
                let entries2 = rememberAdmissibilityAnswer(second)(false)(entries)
                in
                    lookupAdmissibilityAnswer(first)(entries2) == Some(true) && lookupAdmissibilityAnswer(second)(entries2) == Some(false) && lookupAdmissibilityAnswer(box(1)(SemString))(entries2) == None && !concreteAdmissibilityType(box(1)(SemVariable(8))) && !concreteAdmissibilityType("a"
                    |> SemParameter(8)
                    |> box(1)) && Ashes.Collection.List.length(fill(130)([])) == 2)

let recursive run count =
    if count == 0
    then Ashes.IO.print("cache ok")
    else
        if check(Unit)
        then run(count - 1)
        else Ashes.IO.panic("cache mismatch")

match Ashes.IO.args with
    | "1000" :: [] -> run(1000)
    | "10000" :: [] -> run(10000)
    | _ -> Ashes.IO.panic("count required")
