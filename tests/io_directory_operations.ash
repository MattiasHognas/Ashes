// expect: a.txt|destination.txt|nested|z.txt|replace-ok|remove-ok
import Ashes.Text
match Ashes.IO.Directory.removeTree("directory-operations") with
    | Error(message) -> Ashes.IO.print(message)
    | Ok(_) ->
        match Ashes.IO.Directory.createAll("directory-operations/nested") with
            | Error(message) -> Ashes.IO.print(message)
            | Ok(_) ->
                match Ashes.IO.File.writeText("directory-operations/z.txt")("z") with
                    | Error(message) -> Ashes.IO.print(message)
                    | Ok(_) ->
                        match Ashes.IO.File.writeText("directory-operations/a.txt")("source") with
                            | Error(message) -> Ashes.IO.print(message)
                            | Ok(_) ->
                                match Ashes.IO.File.writeText("directory-operations/destination.txt")("old") with
                                    | Error(message) -> Ashes.IO.print(message)
                                    | Ok(_) ->
                                        match Ashes.IO.Directory.entries("directory-operations") with
                                            | Error(message) -> Ashes.IO.print(message)
                                            | Ok(names) ->
                                                let _ = Ashes.IO.write(Ashes.Text.join("|")(names) + "|")
                                                in
                                                    match Ashes.IO.File.replace("directory-operations/a.txt")("directory-operations/destination.txt") with
                                                        | Error(message) -> Ashes.IO.print(message)
                                                        | Ok(_) ->
                                                            match Ashes.IO.File.readText("directory-operations/destination.txt") with
                                                                | Error(message) -> Ashes.IO.print(message)
                                                                | Ok(text) ->
                                                                    let _ =
                                                                        Ashes.IO.write(if text == "source"
                                                                        then "replace-ok|"
                                                                        else "replace-wrong|")
                                                                    in
                                                                        match Ashes.IO.Directory.removeTree("directory-operations") with
                                                                            | Error(message) -> Ashes.IO.print(message)
                                                                            | Ok(_) ->
                                                                                match Ashes.IO.File.exists("directory-operations") with
                                                                                    | Error(message) -> Ashes.IO.print(message)
                                                                                    | Ok(exists) ->
                                                                                        Ashes.IO.print(if exists
                                                                                        then "remove-wrong"
                                                                                        else "remove-ok")
