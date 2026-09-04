// expect: 15002 20000 xxxxx
// A tail-recursive walk consumes the list a `split` built inline in the call and conses each
// line onto its accumulator, so the lines move from the consumed list into the result. Each line
// is longer than one arena chunk, and 20000 unrelated allocations follow the walk before the
// join reads the lines back: the consumed list's release must leave the moved lines alive.

import Ashes.Text as text
let recursive build n acc =
    if n == 0
    then acc
    else build(n - 1)(acc + "x")

let recursive walk items acc =
    match items with
        | [] -> acc
        | line :: rest -> walk(rest)(line :: acc)

let collect (source: Str) = text.join("\n")(walk(text.split(source)("\n"))([]))

let recursive churn count acc =
    if count == 0
    then acc
    else churn(count - 1)("churn " + text.fromInt(count) :: acc)

let recursive countList items count =
    match items with
        | [] -> count
        | _ :: rest -> countList(rest)(count + 1)

let line = build(5000)("")

let source = line + "\n" + line + "\n" + line

let joined = collect(source)

let noise = churn(20000)([])

Ashes.IO.print(text.fromInt(text.length(joined)) + " " + text.fromInt(countList(noise)(0)) + " " + text.take(joined)(5))
