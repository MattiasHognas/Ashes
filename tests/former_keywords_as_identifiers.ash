// expect: ok
// `effect` and `uses` are ordinary identifiers: they were never keywords.
let uses =
    given (effect) -> effect

Ashes.IO.print(uses("ok"))
