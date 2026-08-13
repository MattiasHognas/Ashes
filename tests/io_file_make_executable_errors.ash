// expect: file-ok|missing-error|directory-error
match Ashes.IO.Directory.removeTree("executable-permission-errors") with
    | Error(message) -> Ashes.IO.print(message)
    | Ok(_) ->
        match Ashes.IO.Directory.createAll("executable-permission-errors/directory") with
            | Error(message) -> Ashes.IO.print(message)
            | Ok(_) ->
                match Ashes.IO.File.writeText("executable-permission-errors/file")("content") with
                    | Error(message) -> Ashes.IO.print(message)
                    | Ok(_) ->
                        let _ =
                            Ashes.IO.write(match Ashes.IO.File.makeExecutable("executable-permission-errors/file") with
                                | Ok(_) -> "file-ok|"
                                | Error(_) -> "file-error|")
                        in
                            let _ =
                                Ashes.IO.write(match Ashes.IO.File.makeExecutable("executable-permission-errors/missing") with
                                    | Ok(_) -> "missing-wrong|"
                                    | Error(_) -> "missing-error|")
                            in
                                Ashes.IO.print(match Ashes.IO.File.makeExecutable("executable-permission-errors/directory") with
                                    | Ok(_) -> "directory-wrong"
                                    | Error(_) -> "directory-error")
