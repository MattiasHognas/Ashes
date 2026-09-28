// expect: qrs1,qrs1,qrs1|3,:,2,:,1,:|qrs2,qrs2,qrs2|3,:,2,:,1,:|qrs3,qrs3,qrs3|3,:,2,:,1,:|

let recursive collectViews (bytes: Bytes) (n: Int) (acc: List(Str)) =
    if n == 0
    then acc
    else collectViews(bytes)(n - 1)(Ashes.Byte.subView(bytes)(16)(4) :: acc)

let recursive collect (n: Int) (acc: List(Str)) =
    if n == 0
    then acc
    else collect(n - 1)(Ashes.Text.fromInt(n) :: acc)

let recursive interleave (acc: List(Str)) (parts: List(Str)) =
    match parts with
        | [] -> acc
        | part :: rest -> interleave(part :: ":" :: acc)(rest)

let recursive rounds (n: Int) (limit: Int) (out: Str) =
    if n > limit
    then out
    else
        let bytes = Ashes.Byte.fromText("abcdefghijklmnopqrs" + Ashes.Text.fromInt(n) + "tuvwxyz")
        in
            let views = collectViews(bytes)(3)([])
            in
                let chained =
                    []
                    |> collect(3)
                    |> interleave([])
                in rounds(n + 1)(limit)(out + Ashes.Text.join(",")(views) + "|" + Ashes.Text.join(",")(chained) + "|")

""
|> rounds(1)(3)
|> Ashes.IO.print
