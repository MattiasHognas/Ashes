// Regression: a value destructured from a tuple pulled out of a TCO loop parameter's list element
// (two pattern levels deep -- the list cons, then the tuple) must survive the loop parameter's own
// drop once it escapes into the function's result. The list-cons level alone (`pair :: rest`) was
// already protected at the recursive-call site; the nested tuple level (`(lit, n)`) was not tracked
// at all, so the escaping string was freed out from under the returned record by the generic
// TCO-exit drop as soon as the same lookup table was walked a second time.
// expect: = +
import Ashes.IO as io
type Entry =
    | text: Str
    | n: Int

let table = [("=", 1), ("+", 2)]

let recursive lookup ch tbl =
    match tbl with
        | [] -> None
        | pair :: rest ->
            match pair with
                | (lit, n) ->
                    if lit == ch
                    then Some(Entry(text = lit, n = n))
                    else lookup(ch)(rest)

let e1 =
    match lookup("=")(table) with
        | Some(e) -> e
        | None -> Entry(text = "?", n = 0)

let e2 =
    match lookup("+")(table) with
        | Some(e) -> e
        | None -> Entry(text = "?", n = 0)

io.print(e1.text + " " + e2.text)
