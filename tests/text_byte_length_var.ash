// expect: 4 6 4 4
type Box =
    | Box(Str)

let fromField = 
    match Box("nope") with
        | Box(s) -> Ashes.Text.byteLength(s)

let viaLet = 
    (let s2 = "héllo"
    in Ashes.Text.byteLength(s2))

let inBody b = 
    match b with
        | Box(s3) -> Ashes.Text.byteLength(s3)

let viaParam s4 = Ashes.Text.byteLength(s4)

Ashes.IO.print(Ashes.Text.fromInt(fromField) + " " + Ashes.Text.fromInt(viaLet) + " " + Ashes.Text.fromInt(inBody(Box("nope"))) + " " + Ashes.Text.fromInt(viaParam("nope")))
