let recursive oldLookup name pairs =
    match pairs with
        | [] -> None
        | (key, value) :: rest ->
            if key == name
            then Some(value)
            else oldLookup(name)(rest)

let recursive oldStage hops name pairs =
    if hops <= 0
    then Some(name)
    else
        match oldLookup(name)(pairs) with
            | None -> None
            | Some(next) -> oldStage(hops - 1)(next)(pairs)

let recursive oldReaches depth current target pairs =
    if current == target
    then true
    else
        if depth == 0
        then false
        else
            match oldLookup(current)(pairs) with
                | None -> false
                | Some(next) -> oldReaches(depth - 1)(next)(target)(pairs)

let recursive indexOf pairs =
    match pairs with
        | [] -> Ashes.Collection.Map.empty
        | (key, value) :: rest ->
            rest
            |> indexOf
            |> Ashes.Collection.Map.setStr(key)(value)

let recursive compareTargets depth current pairs index targets =
    match targets with
        | [] -> true
        | target :: rest -> curryChainReaches(depth)(current)(target)(index) == oldReaches(depth)(current)(target)(pairs) && compareTargets(depth)(current)(pairs)(index)(rest)

let recursive compareNames depth pairs index names =
    match names with
        | [] -> true
        | name :: rest -> innermostStageLabel(depth)(name)(index) == oldStage(depth)(name)(pairs) && compareTargets(depth)(name)(pairs)(index)(["a", "b", "c", "missing", ""]) && compareNames(depth)(pairs)(index)(rest)

let recursive compareDepth depth pairs index =
    if depth < 0
    then true
    else compareNames(depth)(pairs)(index)(["a", "b", "c", "missing", ""]) && compareDepth(depth - 1)(pairs)(index)

let fresh seed =
    (let name = "closure" + Ashes.Text.fromInt(seed)
    in
        let base = Ashes.Collection.Map.setStr(name)("old")(Ashes.Collection.Map.empty)
        in
            let newest = Ashes.Collection.Map.setStr(name)("new")(base)
            in innermostStageLabel(1)(name)(base) == Some("old") && innermostStageLabel(1)(name)(newest) == Some("new") && innermostStageLabel(0)(name)(newest) == Some(name) && innermostStageLabel(2)(name)(newest) == None)

let recursive run count =
    if count == 0
    then Ashes.IO.print("done")
    else
        if fresh(count)
        then run(count - 1)
        else Ashes.IO.panic("snapshot mismatch")

let check unit =
    (let pairs = [("a", "b"), ("b", "c"), ("c", "a"), ("a", "obsolete"), ("", "b")]
    in
        compareDepth(64)(pairs)(indexOf(pairs)) && compareDepth(3)([])(Ashes.Collection.Map.empty) && fresh(42))

match Ashes.IO.args with
    | "check" :: [] ->
        if check(Unit)
        then Ashes.IO.print("closure index ok")
        else Ashes.IO.panic("closure index mismatch")
    | "1000" :: [] -> run(1000)
    | "10000" :: [] -> run(10000)
    | _ -> Ashes.IO.panic("mode required")
