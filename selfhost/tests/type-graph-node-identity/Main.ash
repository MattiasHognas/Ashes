import TypeGraphIndexTests
let recursive fresh remaining =
    if remaining == 0
    then Ashes.IO.print("fresh graph identities passed")
    else
        remaining
        |> runFreshTypeGraphIndexTest
        |> (given (_) -> fresh(remaining - 1))

match Ashes.IO.args with
    | "check" :: [] ->
        Unit
        |> runTypeGraphIndexTests
        |> (given (_) -> runTypeGraphPointerIndexTests(Unit))
        |> (given (_) -> runTypeGraphCollisionTests(Unit))
        |> (given (_) -> Ashes.IO.print("graph identity and collision tests passed"))
    | "1000" :: [] -> fresh(1000)
    | "10000" :: [] -> fresh(10000)
    | _ -> Ashes.IO.panic("mode required")
