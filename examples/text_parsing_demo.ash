match Ashes.Text.uncons("é123") with
    | None -> Ashes.IO.print("empty")
    | Some((head, tail)) -> 
        match Ashes.Text.parseInt("123") with
            | Error(message) -> Ashes.IO.print(message)
            | Ok(number) -> 
                if number == 123
                then 
                    match Ashes.Text.parseFloat("1e3") with
                        | Error(message) -> Ashes.IO.print(message)
                        | Ok(value) -> 
                            if value == 1000.0
                            then Ashes.IO.print(head + "|" + tail + "|ok")
                            else Ashes.IO.print("bad float")
                else Ashes.IO.print("bad int")
