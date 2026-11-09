import AshesCompiler.Backend.IrCodegen.Support
let assertLookup (condition: Bool) =
    if condition
    then Unit
    else Ashes.IO.panic("index lookup mismatch")

let testIndexLookups unit =
    Unit
    |> (given (_) -> assertLookup(lookupIndexedInt(2)([(1, 10), (2, 20)]) + lookupIndexedStr("answer")([("other", 0), ("answer", 22)]) == 42))
    |> (given (_) -> assertLookup(lookupIndexedInt(1)([(1, "first"), (1, "second")]) == "first"))
    |> (given (_) ->
        [("ready", true)]
        |> lookupIndexedStr("ready")
        |> assertLookup)
    |> (given (_) -> assertLookup(lookupIndexedAll([3, 1, 3])([(1, 10), (2, 20), (3, 30)]) == [30, 10, 30]))
    |> (given (_) -> assertLookup(lookupIndexedAll([])([(1, 10)]) == []))

Unit
|> testIndexLookups
|> (given (_) -> Ashes.IO.print("index lookups passed"))
