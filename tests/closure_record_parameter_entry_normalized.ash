// expect: 1377788
// A record holding a closure passes through a function that always returns its parameter, so the
// parameter is entry-normalized: a caller keeping its own reference hands over a copy whose
// closure child is copied with its captured string, and both the original and the forwarded
// record read the string back while every copy is released once.

type Box =
    | reader: Int -> Str

let box (s: Str) =
    Box(reader = given (u: Int) -> s)

let pass (b: Box) = b

let recursive loop (n: Int) (total: Int) =
    if n == 0
    then total
    else
        let kept = box(Ashes.Text.fromInt(n) + "-abcdefgh")
        in
            let forwarded = pass(kept)
            in
                match forwarded with
                    | Box { reader = reader } ->
                        match kept with
                            | Box { reader = original } -> loop(n - 1)(total + Ashes.Text.byteLength(reader(0)) + Ashes.Text.byteLength(original(1)))

Ashes.IO.print(Ashes.Text.fromInt(loop(50000)(0)))
