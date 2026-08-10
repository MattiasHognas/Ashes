// expect: ok
// skip-on: win-x64
// Spawns many long-lived children without an explicit kill/wait. Each Process is dropped at
// its match-arm scope exit, which closes its three pipe fds, terminates it, and reaps it.
// This exercises the Process-drop codegen path at scale; the
// fd-release guarantee itself is verified directly under a low `ulimit -n`.
import Ashes.IO.Process
import Ashes.IO
let recursive loop n =
    if n <= 0
    then "ok"
    else
        match Ashes.IO.Process.spawn("/bin/sleep")(["60"]) with
            | Error(_e) -> "spawn-failed"
            | Ok(_proc) -> loop(n - 1)
in Ashes.IO.print(loop(2000))
