// expect: hello exit 0
// skip-on: win-x64
// A read-only helper spelled through an import alias is the same proven borrow as the fully
// qualified spelling: the caller keeps the process and waits for it afterwards.
import Ashes.IO
import Ashes.IO.Process as Process
let readFirstLine =
    given (process) ->
        match Process.readStdoutLine(process) with
            | None -> "no output"
            | Some(line) -> line

match Process.spawn("/bin/echo")(["hello"]) with
    | Error(msg) -> Ashes.IO.print(msg)
    | Ok(process) ->
        let line = readFirstLine(process)
        in
            match Process.waitForExit(process) with
                | code -> Ashes.IO.print(line + " exit " + Ashes.Text.fromInt(code))
