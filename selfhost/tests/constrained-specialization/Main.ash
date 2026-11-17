import Tests.runCoreConstrainedSpecializationTests
import InferenceTests.runCoreConstrainedSpecializationInferenceTests
let inferredChecked = runCoreConstrainedSpecializationInferenceTests(Unit)

let count =
    match Ashes.IO.args with
        | "1" :: [] -> 1
        | "1000" :: [] -> 1000
        | "10000" :: [] -> 10000
        | _ -> Ashes.IO.panic("round count")

let recursive loop (remaining: Int) =
    if remaining <= 0
    then Unit
    else
        let checked = runCoreConstrainedSpecializationTests(Unit)
        in loop(remaining - 1)

count
|> loop
|> (given (_) -> Ashes.IO.print("concrete planning passed"))
