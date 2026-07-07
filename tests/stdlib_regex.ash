// expect: ok
import Ashes.Regex
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

let getRe p = 
    match Ashes.Regex.compile(p) with
        | Ok(r) -> r
        | Error(_e) -> Regex(0)

let digits = getRe("[0-9]+")

let firstSpan re text = 
    match Ashes.Regex.find(re)(text) with
        | None -> "none"
        | Some(s) -> showSpan(s)

let a1 = assertEqual(true)(Ashes.Regex.isMatch(digits)("abc123"))

let a2 = assertEqual(false)(Ashes.Regex.isMatch(digits)("abcdef"))

let a3 = assertEqual("3:6")(firstSpan(digits)("abc123def"))

let a4 = assertEqual("1:2,3:5,6:9,")(showSpans(Ashes.Regex.findAll(digits)("a1b22c333")))

let a5 = assertEqual("a#b#c#")(Ashes.Regex.replace(digits)("a1b22c333")("#"))

let pair = getRe("([a-z]+)=([0-9]+)")

let capsText = 
    match Ashes.Regex.captures(pair)("x=42") with
        | None -> "none"
        | Some(cs) -> showCaps(cs)

let a6 = assertEqual("x=42,x,42,")(capsText)

let a7 = assertEqual("0:6,7:13,")(showSpans(Ashes.Regex.findAll(getRe("\\w+"))("héllo wörld")))

let alt = getRe("(a)|(b)")

let altCapsText = 
    match Ashes.Regex.captures(alt)("b") with
        | None -> "none"
        | Some(cs) -> showCaps(cs)

let a8 = assertEqual("b,-,b,")(altCapsText)

let compiledBad = 
    match Ashes.Regex.compile("[0-9") with
        | Ok(_r) -> false
        | Error(_m) -> true

let a9 = assertEqual(true)(compiledBad)

Ashes.IO.print("ok")
