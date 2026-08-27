// expect: zero=1;one=1;other=1;
type Case =
    | tag: Int
    | label: Str

let recursive appendPair key value entries =
    match entries with
        | [] -> [(key, value)]
        | head :: tail -> head :: appendPair(key)(value)(tail)

let recursive addCases cases entries =
    match cases with
        | [] -> entries
        | Case { label = l } :: rest -> addCases(rest)(appendPair(l)(1)(entries))

let recursive render entries =
    match entries with
        | [] -> ""
        | (k, v) :: tail -> k + "=" + Ashes.Text.fromInt(v) + ";" + render(tail)

let cases = [Case(tag = 0, label = "zero"), Case(tag = 1, label = "one"), Case(tag = 2, label = "other")]

Ashes.IO.print(render(addCases(cases)([])))
