// expect: xxxxx|abcdefghij|Abc.DeAd.1bc|---|A.B.C.D.E.A.B.C.D.E.A.B.C.D.E
let recursive loop n acc =
    (let r = acc
    in
        if n == 0
        then r
        else loop(n - 1)(r + "x"))

let recursive walk n acc extra =
    match Ashes.Text.unconsText(extra) with
        | None -> acc
        | Some((h, t)) ->
            let r = acc
            in
                let rest = t
                in
                    if n == 0
                    then r
                    else walk(n - 1)(r + h)(rest)

let upperAscii value =
    match value with
        | "a" -> "A"
        | "b" -> "B"
        | "c" -> "C"
        | "d" -> "D"
        | "e" -> "E"
        | _ -> value

let isAsciiLetterOrDigit value =
    match value with
        | "a" -> true
        | "b" -> true
        | "c" -> true
        | "d" -> true
        | "e" -> true
        | "A" -> true
        | "1" -> true
        | _ -> false

let recursive pascalCaseCharacters remaining capitalize result =
    match Ashes.Text.unconsText(remaining) with
        | None -> result
        | Some((head, tail)) ->
            if head == "."
            then pascalCaseCharacters(tail)(true)(result + ".")
            else
                if isAsciiLetterOrDigit(head)
                then continuePascalCase(tail)(capitalize)(result)(head)
                else pascalCaseCharacters(tail)(true)(result)
and continuePascalCase (tail: Str) (capitalize: Bool) (result: Str) (head: Str) =
    if capitalize
    then pascalCaseCharacters(tail)(false)(result + upperAscii(head))
    else pascalCaseCharacters(tail)(false)(result + head)

let pascalCase value =
    match pascalCaseCharacters(value)(true)("") with
        | "" -> value
        | converted -> converted

Ashes.IO.print(
    loop(5)("") + "|" + walk(100)("")("abcdefghij") + "|" + pascalCase("abc.de-ad.1bc") + "|" + pascalCase("---") + "|" + pascalCase("a.b.c.d.e.a.b.c.d.e.a.b.c.d.e")
)
