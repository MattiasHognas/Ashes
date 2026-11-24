// The libc process calls partitioned code generation runs its child processes with: `fork` to
// start one, `waitpid` to reap any, `_exit` to end a child without running the parent's exit
// paths, and `getpid` to name the run's temporary directory. linux-x64 and linux-arm64 both reach
// them through glibc (`libc.so.6`); the Windows targets never fork and build partitions in process.
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
    value exitChildProcess,
    value currentProcessId,
)

external libcFork() -> Int = "fork@libc.so.6"
external libcWaitPid(Int, Int, Int) -> Int = "waitpid@libc.so.6"
external libcExit(Int) -> void = "_exit@libc.so.6"
external libcGetPid() -> Int = "getpid@libc.so.6"

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

// `_exit(code)`: ends the calling process at once, without the atexit handlers and static
// destructors (LLVM's among them) a normal exit would run a second time in a forked child.
let exitChildProcess (code: Int) = libcExit(code)

let currentProcessId unit =
    Unit
    |> libcGetPid
    |> low32BitsSigned
