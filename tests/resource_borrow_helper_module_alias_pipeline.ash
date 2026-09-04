// expect: hello exit 0
// skip-on: win-x64
// The pipeline spelling of the aliased read-only helper: every stage borrows the process, so the
// closures capturing it stay valid through waitForExit.
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
        Unit
        |> (given (_) -> readFirstLine(process))
        |> (given (line) ->
            line + " exit " + Ashes.Text.fromInt(Process.waitForExit(process)))
        |> Ashes.IO.print
