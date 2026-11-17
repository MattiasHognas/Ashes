import RecursiveTraitEvidenceTests
let recursive rounds remaining =
    if remaining == 0
    then Ashes.IO.print("recursive trait evidence passed")
    else
        match runRecursiveTraitEvidenceTests(Unit) with
            | _ -> rounds(remaining - 1)

match Ashes.IO.args with
    | [] -> rounds(1)
    | "1" :: [] -> rounds(1)
    | "1000" :: [] -> rounds(1000)
    | "10000" :: [] -> rounds(10000)
    | _ -> Ashes.IO.panic("count")
