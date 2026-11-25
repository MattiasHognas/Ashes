// The libc process calls partitioned code generation runs its child processes with: `fork` to
// start one, `waitpid` to reap any, `_exit` to end a child without running the parent's exit
// paths, and `getpid` to name the run's temporary directory; `sysconf` for the number of online
// processors; `prctl`, `getppid`, `setpgid`, `kill` and `sleep` to tie the children's lifetime to
// the parent's. linux-x64 and linux-arm64 both reach them through glibc (`libc.so.6`); the Windows
// targets never fork and build partitions in process.
//
// `pid_t` is a 32-bit C `int`, so a result is read from the low 32 bits of the returned word and
// sign-extended: `-1` comes back as `0xffffffff`, not as a negative word.
//
// `waitForAnyChild` is `waitpid(-1, NULL, 0)`: it blocks until any child exits and returns its
// process id, negative when there is none to wait for. The exit status is not read: a child
// reports its outcome in files.
export (
    value forkProcess,
    value waitForAnyChild,
    value waitForChild,
    value exitChildProcess,
    value currentProcessId,
    value onlineProcessorCount,
    value endWithParent,
    value startDirectoryWatchdog,
    value stopDirectoryWatchdog,
)

external libcFork() -> Int = "fork@libc.so.6"
external libcWaitPid(Int, Int, Int) -> Int = "waitpid@libc.so.6"
external libcExit(Int) -> void = "_exit@libc.so.6"
external libcGetPid() -> Int = "getpid@libc.so.6"
external libcGetParentPid() -> Int = "getppid@libc.so.6"
external libcSysconf(Int) -> Int = "sysconf@libc.so.6"
external libcPrctl(Int, Int) -> Int = "prctl@libc.so.6"
external libcSetProcessGroup(Int, Int) -> Int = "setpgid@libc.so.6"
external libcKill(Int, Int) -> Int = "kill@libc.so.6"
external libcSleep(Int) -> Int = "sleep@libc.so.6"

let low32BitsSigned (word: Int) =
    (let low = word & 4294967295
    in
        if low >= 2147483648
        then low - 4294967296
        else low)

// `fork()`: 0 in the child, the child's process id in the parent, negative when it failed.
let forkProcess unit =
    Unit
    |> libcFork
    |> low32BitsSigned

let waitForAnyChild unit =
    0
    |> libcWaitPid(-1)(0)
    |> low32BitsSigned

// `waitpid(processId, NULL, 0)`: blocks until that child exits.
let waitForChild (processId: Int) =
    0
    |> libcWaitPid(processId)(0)
    |> low32BitsSigned

// `_exit(code)`: ends the calling process at once, without the atexit handlers and static
// destructors (LLVM's among them) a normal exit would run a second time in a forked child.
let exitChildProcess (code: Int) = libcExit(code)

let currentProcessId unit =
    Unit
    |> libcGetPid
    |> low32BitsSigned

let parentProcessId unit =
    Unit
    |> libcGetParentPid
    |> low32BitsSigned

// `sysconf(_SC_NPROCESSORS_ONLN)`, glibc's name 84 on every Linux architecture; at least 1, and 1
// where the call fails.
let onlineProcessorCount unit =
    (let count = libcSysconf(84)
    in
        if count < 1
        then 1
        else count)

// In a freshly forked child of `parentId`: `prctl(PR_SET_PDEATHSIG, SIGKILL)` has the kernel kill
// this process when its parent ends, however it ends (a signal, a crash, `SIGKILL`), so no
// partition child outlives the compile. A parent that ended between the fork and the call is
// caught by the parent check after it.
let endWithParent (parentId: Int) =
    (let _ = libcPrctl(1)(9)
    in
        if parentProcessId(Unit) != parentId
        then exitChildProcess(1)
        else Unit)

let recursive waitWhileParentLives (parentId: Int) =
    if parentProcessId(Unit) == parentId
    then
        let _ = libcSleep(1)
        in waitWhileParentLives(parentId)
    else Unit

// Forks a watchdog that removes `directory` once the compiling process ends without removing it
// itself, as when it is interrupted or terminated. The watchdog leaves the parent's process group,
// so the terminal's interrupt, which ends the parent and the partition children, does not reach
// it; it polls once a second for its parent to change (an ended parent's children are reparented),
// then removes the directory and exits. It answers the watchdog's process id, or -1 when it could
// not be started, in which case the build goes on without it.
let startDirectoryWatchdog (directory: Str) =
    (let parentId = currentProcessId(Unit)
    in
        let processId = forkProcess(Unit)
        in
            if processId == 0
            then
                let _ = libcSetProcessGroup(0)(0)
                in
                    let _ = waitWhileParentLives(parentId)
                    in
                        let _ = Ashes.IO.Directory.removeTree(directory)
                        in
                            let _ = exitChildProcess(0)
                            in -1
            else
                if processId < 0
                then -1
                else processId)

// Ends the watchdog with `SIGKILL` and reaps it, once the parent has removed the directory itself.
let stopDirectoryWatchdog (processId: Int) =
    if processId > 0
    then
        let _ = libcKill(processId)(9)
        in
            let _ = waitForChild(processId)
            in Unit
    else Unit
