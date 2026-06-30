// expect: ok
// skip-on: win-x64
// Spawns many short-lived children without an explicit close/wait. Each Process is dropped at
// its match-arm scope exit, which closes its three pipe fds and reaps it (see
// docs/future/RESOURCE_SAFETY.md). This exercises the Process-drop codegen path at scale; the
// fd-release guarantee itself is verified directly under a low `ulimit -n`.
import Ashes.Process
import Ashes.IO
let rec loop n = 
    if n <= 0
    then "ok"
    else 
        match Ashes.Process.spawn("/bin/true")([]) with
            | Error(_e) -> "spawn-failed"
            | Ok(_proc) -> loop(n - 1)
in Ashes.IO.print(loop(2000))
