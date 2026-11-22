// expect: a1:11,a2:12,a3:13,x-y:10,z:10
// Closures of two shapes, one capturing an Int and a Str and one capturing a list of strings,
// are built in a loop and kept in a list, so their copies into the reference-counted heap go
// through the module's shared environment-normalizer dispatch, which picks each closure's own
// normalizer by its code address.
let adder (base: Int) (label: Str) =
    given (n: Int) -> label + ":" + Ashes.Text.fromInt(base + n)

let joiner (parts: List(Str)) =
    given (n: Int) -> Ashes.Text.join("-")(parts) + ":" + Ashes.Text.fromInt(n)

let recursive buildAdders (count: Int) (acc: List(Int -> Str)) =
    if count == 0
    then acc
    else buildAdders(count - 1)(adder(count)("a" + Ashes.Text.fromInt(count)) :: acc)

let recursive applyAll (fns: List(Int -> Str)) (n: Int) =
    match fns with
        | [] -> []
        | f :: rest -> f(n) :: applyAll(rest)(n)

let closures = buildAdders(3)([joiner(["x", "y"]), joiner(["z"])])

10
|> applyAll(closures)
|> Ashes.Text.join(",")
|> Ashes.IO.print
