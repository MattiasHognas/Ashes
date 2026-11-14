// The renderer inserts the actual optimizer helper without changing its types.
// OPTIMIZER_LOOKUP

let lookupAlias = lookupAssociation

let recursive lookupAcross key tables =
    match tables with
        | [] -> None
        | entries :: rest ->
            match lookupAssociation(key)(entries) with
                | Some(value) -> Some(value)
                | None -> lookupAcross(key)(rest)

let checkRound n =
    (let text = "key-" + Ashes.Text.fromInt(n)
    in
        let numbers = [(n - 1, "other"), (n, text)]
        in
            let labels = [("absent", n - 1), (text, n)]
            in
                let direct =
                    match (lookupAssociation(n)(numbers), lookupAssociation(text)(labels)) with
                        | (Some(foundText), Some(foundNumber)) -> foundText == text && foundNumber == n
                        | _ -> false
                in
                    let values =
                        match (lookupAssociation(n)([(n, [text, "tail"])]), lookupAssociation(text)([(text, false)])) with
                            | (Some(head :: "tail" :: []), Some(false)) -> head == text
                            | _ -> false
                    in
                        let aliases =
                            match (lookupAlias(n)(numbers), lookupAlias(text)(labels)) with
                                | (Some(foundText), Some(foundNumber)) -> foundText == text && foundNumber == n
                                | _ -> false
                        in
                            let forwarded =
                                match (lookupAcross(n)([[], numbers]), lookupAcross(text)([[], labels])) with
                                    | (Some(foundText), Some(foundNumber)) -> foundText == text && foundNumber == n
                                    | _ -> false
                            in
                                let partial =
                                    let byNumber = lookupAssociation(n)
                                    in
                                        let byLabel = lookupAssociation(text)
                                        in
                                            match (byNumber(numbers), byLabel(labels)) with
                                                | (Some(foundText), Some(foundNumber)) -> foundText == text && foundNumber == n
                                                | _ -> false
                                in direct && values && aliases && forwarded && partial)

let recursive rounds count =
    if count == 0
    then Ashes.IO.print("mixed constrained lookup passed")
    else
        if checkRound(count)
        then rounds(count - 1)
        else Ashes.IO.panic("mixed constrained lookup failed")

match Ashes.IO.args with
    | "1" :: [] -> rounds(1)
    | "1000" :: [] -> rounds(1000)
    | "10000" :: [] -> rounds(10000)
    | _ -> Ashes.IO.panic("count")
