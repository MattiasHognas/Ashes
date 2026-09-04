// expect-compile-error: ASH008
// A recursive relay loop passes the process on to itself, so it is not a proven borrow and the
// first pipeline stage moves the process into it. The second stage's closure captured the process
// before that move and still uses it, which is the same use-after-move as the direct let form.
import Ashes.IO
import Ashes.IO.Process
let recursive relayStdout process =
    match Ashes.IO.Process.readStdoutLine(process) with
        | None -> Unit
        | Some(line) ->
            let _ = Ashes.IO.print(line)
            in relayStdout(process)

match Ashes.IO.Process.spawn("/bin/echo")(["hello"]) with
    | Error(msg) -> Ashes.IO.print(msg)
    | Ok(process) ->
        Unit
        |> (given (_) -> relayStdout(process))
        |> (given (_) -> Ashes.IO.Process.waitForExit(process))
        |> (given (code) -> Ashes.IO.print("exit " + Ashes.Text.fromInt(code)))
