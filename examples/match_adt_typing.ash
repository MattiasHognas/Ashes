let unwrapOr opt def =
    match opt with
        | None -> def
        | Some(x) -> x
in
    let getOrDefault res def =
        match res with
            | Ok(v) -> v
            | Error(_) -> def
    in Ashes.IO.print(unwrapOr(Some(10))(0) + getOrDefault(Ok(5))(0))
