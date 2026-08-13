// expect: 1
let values = [1, 2, 3]
in
    match values with
        | head :: _ as all ->
            match all with
                | _ :: _ -> Ashes.IO.print(head)
                | [] -> Ashes.IO.print(0)
        | [] -> Ashes.IO.print(0)
