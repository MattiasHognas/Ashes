// expect: default second|no default|missing|both|other|other|neither|left|right|neither
type Def =
    | name: Str
    | defaultImplementation: Maybe(Str)

let recursive findDef name defs =
    match defs with
        | [] -> None
        | (Def { name = n, defaultImplementation = _d } as d) :: tail ->
            if n + "" == name
            then Some(d)
            else findDef(name)(tail)

let classify name defs =
    match findDef(name)(defs) with
        | None -> "missing"
        | Some(Def { name = _n, defaultImplementation = None }) -> "no default"
        | Some(Def { name = _n, defaultImplementation = Some(body) }) -> "default " + body

let corners pair =
    match pair with
        | (true, true) -> "both"
        | (true, false) -> "other"
        | (false, true) -> "other"
        | _ -> "neither"

let sides pair =
    match pair with
        | (Some(_), None) -> "left"
        | (None, Some(_)) -> "right"
        | _ -> "neither"

let defs = [Def(name = "first", defaultImplementation = Some("second")), Def(name = "base", defaultImplementation = None)]

Ashes.IO.print(
    classify("first")(defs) + "|" + classify("base")(defs) + "|" + classify("zzz")(defs) + "|" + corners((true, true)) + "|" + corners((true, false)) + "|" + corners((false, true)) + "|" + corners((false, false)) + "|" + sides((Some(1), None)) + "|" + sides((None, Some(2))) + "|" + sides((None, None))
)
