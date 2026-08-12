// expect: ok
import Ashes.Text.Regex
import Ashes.Test
let showSpan span =
    match span with
        | (s, e) -> Ashes.Text.fromInt(s) + ":" + Ashes.Text.fromInt(e)

let recursive showSpans spans =
    match spans with
        | [] -> ""
        | s :: rest -> showSpan(s) + "," + showSpans(rest)

let recursive showCaps caps =
    match caps with
        | [] -> ""
        | c :: rest ->
            match c with
                | None -> "-," + showCaps(rest)
                | Some(t) -> t + "," + showCaps(rest)

let isMatch compiled text =
    match compiled with
        | Ok(re) -> Ashes.Text.Regex.isMatch(re)(text)
        | Error(_error) -> false

let find compiled text =
    match compiled with
        | Ok(re) -> Ashes.Text.Regex.find(re)(text)
        | Error(_error) -> None

let findAll compiled text =
    match compiled with
        | Ok(re) -> Ashes.Text.Regex.findAll(re)(text)
        | Error(_error) -> []

let replace compiled text replacement =
    match compiled with
        | Ok(re) -> Ashes.Text.Regex.replace(re)(text)(replacement)
        | Error(_error) -> ""

let captures compiled text =
    match compiled with
        | Ok(re) -> Ashes.Text.Regex.captures(re)(text)
        | Error(_error) -> None

let digits = Ashes.Text.Regex.compile("[0-9]+")

let firstSpan re text =
    match find(re)(text) with
        | None -> "none"
        | Some(s) -> showSpan(s)

let a1 = assertEqual(true)(isMatch(digits)("abc123"))

let a2 = assertEqual(false)(isMatch(digits)("abcdef"))

let a3 = assertEqual("3:6")(firstSpan(digits)("abc123def"))

let a4 = assertEqual("1:2,3:5,6:9,")(showSpans(findAll(digits)("a1b22c333")))

let a5 = assertEqual("a#b#c#")(replace(digits)("a1b22c333")("#"))

let pair = Ashes.Text.Regex.compile("([a-z]+)=([0-9]+)")

let capsText =
    match captures(pair)("x=42") with
        | None -> "none"
        | Some(cs) -> showCaps(cs)

let a6 = assertEqual("x=42,x,42,")(capsText)

let a7 = assertEqual("0:6,7:13,")(showSpans(findAll(Ashes.Text.Regex.compile("\\w+"))("héllo wörld")))

let alt = Ashes.Text.Regex.compile("(a)|(b)")

let altCapsText =
    match captures(alt)("b") with
        | None -> "none"
        | Some(cs) -> showCaps(cs)

let a8 = assertEqual("b,-,b,")(altCapsText)

let compiledBad =
    match Ashes.Text.Regex.compile("[0-9") with
        | Ok(_r) -> false
        | Error(_m) -> true

let a9 = assertEqual(true)(compiledBad)

Ashes.IO.print("ok")
