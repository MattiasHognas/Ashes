import RecordFieldInferenceTests
let recursive rounds count =
    if count == 0
    then Ashes.IO.print("record field inference passed")
    else
        match runRecordFieldInferenceTests(Unit) with
            | _ -> rounds(count - 1)

match Ashes.IO.args with
    | "1" :: [] -> rounds(1)
    | "1000" :: [] -> rounds(1000)
    | "10000" :: [] -> rounds(10000)
    | _ -> Ashes.IO.panic("expected count")
