export (
    value make,
    type Box,
    type Choice(Yes),
)

type Box =
    | BoxValue(Int)

type Choice =
    | Yes
    | No

let secret = 40

let make =
    given (value) -> BoxValue(value + secret)
