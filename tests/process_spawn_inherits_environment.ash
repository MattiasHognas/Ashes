// expect: env-inherited
// skip-on: win-x64
match Ashes.IO.Directory.removeTree("spawn-environment") with
    | Error(message) -> Ashes.IO.print(message)
    | Ok(_) ->
        match Ashes.IO.Directory.createAll("spawn-environment") with
            | Error(message) -> Ashes.IO.print(message)
            | Ok(_) ->
                match Ashes.IO.File.writeText("spawn-environment/probe")("#!/bin/sh\nif [ -n \"$PATH\" ]; then printf env-inherited; else printf env-missing; fi\n") with
                    | Error(message) -> Ashes.IO.print(message)
                    | Ok(_) ->
                        match Ashes.IO.File.makeExecutable("spawn-environment/probe") with
                            | Error(message) -> Ashes.IO.print(message)
                            | Ok(_) ->
                                match Ashes.IO.Process.spawn("./spawn-environment/probe")([]) with
                                    | Error(message) -> Ashes.IO.print(message)
                                    | Ok(process) ->
                                        match Ashes.IO.Process.readStdoutLine(process) with
                                            | None -> Ashes.IO.print("no output")
                                            | Some(line) ->
                                                let _ = Ashes.IO.Process.waitForExit(process)
                                                in Ashes.IO.print(line)
