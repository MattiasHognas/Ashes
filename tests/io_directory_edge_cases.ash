// expect: missing-ok|collision-error|entries-error|replace-error|source-directory-error
match Ashes.IO.Directory.removeTree("directory-edge-cases") with
    | Error(message) -> Ashes.IO.print(message)
    | Ok(_) ->
        let _ = Ashes.IO.write("missing-ok|")
        in
            match Ashes.IO.File.writeText("directory-edge-cases")("file") with
                | Error(message) -> Ashes.IO.print(message)
                | Ok(_) ->
                    let _ =
                        Ashes.IO.write(match Ashes.IO.Directory.createAll("directory-edge-cases/child") with
                            | Error(_) -> "collision-error|"
                            | Ok(_) -> "collision-wrong|")
                    in
                        let _ =
                            Ashes.IO.write(match Ashes.IO.Directory.entries("directory-edge-cases") with
                                | Error(_) -> "entries-error|"
                                | Ok(_) -> "entries-wrong|")
                        in
                            let _ =
                                Ashes.IO.write(match Ashes.IO.File.replace("missing-source")("directory-edge-cases") with
                                    | Error(_) -> "replace-error|"
                                    | Ok(_) -> "replace-wrong|")
                            in
                                match Ashes.IO.Directory.createAll("directory-edge-source") with
                                    | Error(message) -> Ashes.IO.print(message)
                                    | Ok(_) ->
                                        Ashes.IO.print(match Ashes.IO.File.replace("directory-edge-source")("directory-edge-destination") with
                                            | Error(_) -> "source-directory-error"
                                            | Ok(_) -> "source-directory-wrong")
