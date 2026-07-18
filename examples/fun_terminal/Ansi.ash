import Ashes.Bytes as bytes
import Ashes.String as str
import Ashes.UInt as uint
let esc = bytes.subText(bytes.appendByte(bytes.empty(Unit))(uint.fromInt(27)))(0)(1)

let reset = esc + "[0m"

let paint code s = esc + "[" + code + "m" + s + reset

let red s = paint("1;31")(s)

let green s = paint("1;32")(s)

let yellow s = paint("1;33")(s)

let blue s = paint("1;34")(s)

let magenta s = paint("1;35")(s)

let cyan s = paint("1;36")(s)

let dim s = paint("2")(s)

let clearScreen _ = esc + "[2J" + esc + "[H"

let recursive stripe colors block =
    match colors with
        | [] -> ""
        | c :: rest -> paint(c)(block) + stripe(rest)(block)

let logo _ =
    (let bar = stripe(["1;31", "1;33", "1;32", "1;36", "1;34", "1;35"])("██████")
    in
        let pad = "  "
        in bar + "\n" + pad + paint("1;36")("█▀▀▄ ▀█▀ █▀▀▄ █▀▀▀   █▀▀▄ █▀▀█ █▀▀▄ █▀▀▀") + "\n" + pad + paint("1;36")("█▀▀   █  █  █ █ ▀█   █▀▀  █  █ █  █ █ ▀█") + "\n" + pad + paint("1;34")("▀    ▀▀▀ ▀  ▀ ▀▀▀▀   ▀    ▀▀▀▀ ▀  ▀ ▀▀▀▀") + "\n" + pad + dim("cannon rally over the net  -  first to 3 points") + "\n" + bar)
