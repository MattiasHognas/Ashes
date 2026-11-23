// expect: stored seeded|seeded|saved
import Persistence
provide Store =
    | load =
        given (_) ->
            match Ashes.IO.File.readText("capability_provide_imported_absent.txt") with
                | Ok(contents) -> contents
                | Error(_missing) -> "seeded"
    | save =
        given (text) -> Ashes.IO.writeErrorLine("saving " + text)

let saved =
    given (text) ->
        match Store.save(text) with
            | Unit -> "saved"

Ashes.IO.print(Persistence.describe(Unit) + "|" + Store.load(Unit) + "|" + saved("x"))
