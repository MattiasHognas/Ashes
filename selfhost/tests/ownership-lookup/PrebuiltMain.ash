import AshesCompiler.Semantics.OwnershipInference as Current
import Baseline as Old
import PrebuiltFixture
let expected = Old.inferProgramParameterOwnership(fixture)

let recursive plateau (count: Int) (old: Bool) =
    if count == 0
    then Ashes.IO.print("passed")
    else
        let actual =
            if old
            then Old.inferProgramParameterOwnership(fixture)
            else Current.inferProgramParameterOwnership(fixture)
        in
            if actual == expected
            then plateau(count - 1)(old)
            else Ashes.IO.panic("ownership mismatch")

match Ashes.IO.args with
    | "new100" :: [] -> plateau(100)(false)
    | "new1000" :: [] -> plateau(1000)(false)
    | "old100" :: [] -> plateau(100)(true)
    | "old1000" :: [] -> plateau(1000)(true)
    | _ -> Ashes.IO.panic("select old/new and round count")
