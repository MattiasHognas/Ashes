// expect-compile-error: ASH008
// The direct let form of the pipeline-stage case: the recursive relay loop consumes the process, so
// the waitForExit after it is a use-after-move.
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
        let _ = relayStdout(process)
        in
            match Ashes.IO.Process.waitForExit(process) with
                | code -> Ashes.IO.print("exit " + Ashes.Text.fromInt(code))
