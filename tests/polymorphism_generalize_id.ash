// expect: hello
let id x = x
in
    let n = id(42)
    in
        "hello"
        |> id
        |> Ashes.IO.print
