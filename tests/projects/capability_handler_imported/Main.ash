// expect: mocked!|local
import Fetching as fetching
let local : Unit -> Str needs {Source} =
    given (_) -> perform Source.read(Unit)

let mocked =
    given (_) ->
        handle fetching.fetch(Unit) with
            | Source.read(_u) -> resume("mocked")
            | return(r) -> r

let handledLocally =
    given (_) ->
        handle local(Unit) with
            | Source.read(_u) -> resume("local")
            | return(r) -> r

Ashes.IO.print(mocked(Unit) + "|" + handledLocally(Unit))
