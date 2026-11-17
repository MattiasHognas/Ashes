// A float literal decodes to the nearest Float, ties to even, however many digits it spells.
// The self-hosted lexer once accumulated digits with Float multiply-add, so 9223372036854773760.0
// (2^63 - 2048) decoded one unit in the last place low and the compiler's own literals differed
// between stages.
// expect: 2048 0 4 true false true true true true
let near63 = Ashes.Number.Math.truncToInt(9223372036854773760.0 - 9223372036854771712.0)

let tieToEven = Ashes.Number.Math.truncToInt(9007199254740993.0 - 9007199254740992.0)

let tieUp = Ashes.Number.Math.truncToInt(9007199254740995.0 - 9007199254740992.0)

let showBool value =
    if value
    then "true"
    else "false"

let checks =
    [
        0.1 + 0.2 == 0.30000000000000004,
        0.1 + 0.2 == 0.3,
        100000000000000000000000.0 == 99999999999999991611392.0,
        1.00000000000000011102230246251565404236316680908203126 == 1.0000000000000002,
        1.00000000000000011102230246251565404236316680908203125 == 1.0,
        0.000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000049406564584124654 > 0.0
    ]

let recursive showChecks values =
    match values with
        | [] -> ""
        | value :: tail -> " " + showBool(value) + showChecks(tail)

Ashes.IO.print(Ashes.Text.fromInt(near63) + " " + Ashes.Text.fromInt(tieToEven) + " " + Ashes.Text.fromInt(tieUp) + showChecks(checks))
