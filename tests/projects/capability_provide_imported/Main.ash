// expect: stored seeded|seeded|saved
import Persistence
provide Store =
    | load =
        given (_) -> "seeded"
    | save =
        given (_text) -> Unit

let saved =
    given (text) ->
        match Store.save(text) with
            | Unit -> "saved"

Ashes.IO.print(Persistence.describe(Unit) + "|" + Store.load(Unit) + "|" + saved("x"))
