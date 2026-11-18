// Ashes.Text.parseFloat returns the Float nearest to the decimal text, ties to even, for any number
// of digits and any exponent. It once accumulated digits with Float multiply-add, which landed one
// unit in the last place off for spellings such as 9223372036854773760 and 6.283185307179586.
// expect: true true true true true true true true true true true true
let parsedEquals (text: Str) (expected: Float) =
    match Ashes.Text.parseFloat(text) with
        | Ok(value) -> value == expected
        | Error(_) -> false

let isNegativeZero (text: Str) =
    match Ashes.Text.parseFloat(text) with
        | Ok(value) -> value == 0.0 && 1.0 / value < 0.0
        | Error(_) -> false

let isOutOfRange (text: Str) =
    match Ashes.Text.parseFloat(text) with
        | Ok(_) -> false
        | Error(message) -> message == "Ashes.Text.parseFloat() out of range"

let recursive halved (count: Int) (value: Float) =
    if count <= 0
    then value
    else halved(count - 1)(value * 0.5)

let showBool value =
    if value
    then "true"
    else "false"

let checks =
    [
        parsedEquals("9223372036854773760")(9223372036854773760.0),
        parsedEquals("6.283185307179586")(6.283185307179586),
        parsedEquals("0.30000000000000004")(0.1 + 0.2),
        parsedEquals("1e23")(100000000000000000000000.0),
        parsedEquals("9007199254740993")(9007199254740992.0),
        parsedEquals("1.00000000000000011102230246251565404236316680908203126")(1.0000000000000002),
        parsedEquals("123456789012345678901234567890e-20")(1234567890.1234567890123456789),
        1.0
        |> halved(1074)
        |> parsedEquals("2.4703282292062328e-324"),
        parsedEquals("1e-400")(0.0),
        isNegativeZero("-0.0"),
        isOutOfRange("1.7976931348623159e308"),
        parsedEquals("1.7976931348623158e308")(179769313486231570000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000.0)
    ]

let recursive showChecks values =
    match values with
        | [] -> ""
        | value :: tail -> " " + showBool(value) + showChecks(tail)

checks
|> showChecks
|> Ashes.Text.trim
|> Ashes.IO.print
