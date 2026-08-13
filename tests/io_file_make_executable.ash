// expect: executable-ok
// skip-on: win-x64
match Ashes.IO.Directory.removeTree("executable-permissions") with
    | Error(message) -> Ashes.IO.print(message)
    | Ok(_) ->
        match Ashes.IO.Directory.createAll("executable-permissions") with
            | Error(message) -> Ashes.IO.print(message)
            | Ok(_) ->
                match Ashes.IO.File.writeText("executable-permissions/tool")("#!/bin/sh\nprintf executable-ok\n") with
                    | Error(message) -> Ashes.IO.print(message)
                    | Ok(_) ->
                        match Ashes.IO.File.makeExecutable("executable-permissions/tool") with
                            | Error(message) -> Ashes.IO.print(message)
                            | Ok(_) ->
                                match Ashes.IO.Process.spawn("./executable-permissions/tool")([]) with
                                    | Error(message) -> Ashes.IO.print(message)
                                    | Ok(process) ->
                                        match Ashes.IO.Process.readStdoutLine(process) with
                                            | None -> Ashes.IO.print("no output")
                                            | Some(line) ->
                                                let _ = Ashes.IO.Process.waitForExit(process)
                                                in Ashes.IO.print(line)
