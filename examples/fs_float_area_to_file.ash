let r = 2.0
in 
    let area = 3.14159 * r * r
    in 
        match Ashes.File.writeText("area.txt")(if area >= 12.0
        then "large"
        else "small") with
            | Error(msg) -> Ashes.IO.print(msg)
            | Ok(_) -> 
                match Ashes.File.readText("area.txt") with
                    | Ok(text) -> Ashes.IO.print(text)
                    | Error(msg) -> Ashes.IO.print(msg)
