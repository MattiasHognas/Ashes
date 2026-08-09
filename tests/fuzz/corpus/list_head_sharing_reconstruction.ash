let rebuild =
    given (values: List(Str)) ->
        match values with
            | [] -> []
            | head :: tail -> head :: tail
in
    let sharedHead = "shared"
    in
        let sharedList = sharedHead :: ["tail"]
        in (sharedHead, rebuild(sharedList))
