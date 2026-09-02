// The raw linux-x64 syscall wrappers `AshesCompiler.Backend.IrCodegen`'s builtin emitters call:
// inline `syscall` assembly (`rcx`/`r11` clobbered, a fourth argument riding `r10` and a fifth and
// sixth `r8`/`r9`) plus one named wrapper per syscall the codegen actually needs, each carrying its
// own number. Source of truth: `LlvmCodegenPlatform.cs`'s `EmitSyscallX86`/`EmitSyscall4X86`.
//
// This is the OS+architecture-specific layer, split out from `IrCodegen.Support` (which is
// platform-neutral) so a second target adds a sibling module rather than branches inside these
// bodies: `IrCodegen.Syscalls.LinuxArm64` would carry aarch64's own numbers and `svc #0` sequence,
// and a Windows target reaches the equivalent primitives through imported KERNEL32 calls instead.
// The builtin emitters themselves are deliberately NOT duplicated per target when that day comes —
// their algorithms are platform-independent (`emitFileReadText`'s open/measure/allocate/read-loop/
// validate/close sequence is identical on Windows), so the intended move is to thread a record of
// these primitives through them, the way `DirectoryExternals` already threads libc handles, and to
// split by file only where the ALGORITHM genuinely diverges (`Directory.entries`' `readdir` stream
// versus `FindFirstFile` iteration is the standing example).

import AshesCompiler.Backend.Llvm
import Ashes.Number.UInt
export (
    value emitLinuxSyscallCall,
    value emitLinuxSyscallCall4,
    value emitLinuxSyscallCall6,
    value emitLinuxProcessExitWithCode,
    value emitLinuxProcessExit,
    value emitLinuxWrite,
    value emitLinuxRead,
    value emitLinuxOpenat,
    value emitLinuxClose,
    value emitLinuxMkdir,
    value emitLinuxRename,
    value emitLinuxLseek,
    value emitLinuxChmod,
    value emitLinuxMmapReadPrivate,
    value emitLinuxPipe2,
    value emitLinuxDup2,
    value emitLinuxFork,
    value emitLinuxExecve,
    value emitLinuxWait4,
    value emitLinuxKill,
)

// Any linux-x64 3-argument syscall, matching `LlvmCodegenPlatform.cs`'s own `EmitSyscallX86`
// exactly: `syscall` through inline assembly with the same register-constraint string (`rax` holds
// the syscall number going in and doubles as the return-value register `LLVMGetInlineAsm` still
// declares, whether or not a given syscall — `exit` never does — actually returns to use it),
// `rdi`/`rsi`/`rdx` as the three syscall arguments, `rcx`/`r11` clobbered (the `syscall`
// instruction itself overwrites them) alongside memory. Shared by `exit` (`60`) and `write` (`1`)
// — the only two syscalls this codegen needs so far.
let emitLinuxSyscallCall builder i64 nr arg1 arg2 arg3 name =
    (let syscallType = functionType(i64)([i64, i64, i64, i64])(4u32)(false)
    in
        let syscallAsm = getInlineAsm(syscallType)("syscall")("={rax},{rax},{rdi},{rsi},{rdx},~{rcx},~{r11},~{memory}")(true)(false)
        in buildCall(builder)(syscallType)(syscallAsm)([nr, arg1, arg2, arg3])(4u32)(name))

// `exit` (not `exit_group`) terminates only the calling thread — the right choice for a
// single-threaded program, matching what the real compiler emits here too. A `syscall` that
// terminates the process never returns, so the block ends with `buildUnreachable`, never a `ret`.
// `exitCode` is an already-built `i64` value, not a compile-time literal, so both the entry
// function's own always-`0` `Return` and `PanicStr`'s always-`1` exit share this one helper.
let emitLinuxProcessExitWithCode builder i64 exitCode =
    (let zero = constInt(i64)(0u64)(false)
    in
        let _ =
            emitLinuxSyscallCall(builder)(i64)(constInt(i64)(60u64)(false))(exitCode)(zero)(zero)("sys_exit")
        in buildUnreachable(builder))

let emitLinuxProcessExit builder i64 =
    false
    |> constInt(i64)(0u64)
    |> emitLinuxProcessExitWithCode(builder)(i64)

// `write(fd, ptr, len)` — the raw, unbuffered path `LlvmCodegenPlatform.cs`'s own `EmitWriteBytesRaw`
// takes when a program never touches `Ashes.IO.writeBuffered`/`flush` (the only path this codegen
// implements; a buffered stdout ring, its lock, and the flush-on-exit contract are a separate,
// bigger, unattempted slice). `ptr` is an `i64` address (from `buildPtrToInt`), not an LLVM
// pointer value — every syscall argument here is a plain register-width word.
let emitLinuxWrite builder i64 fd ptr len =
    emitLinuxSyscallCall(builder)(i64)(constInt(i64)(1u64)(false))(fd)(ptr)(len)("sys_write")

// `read(fd, ptr, len)` — the syscall number 0 twin of `emitLinuxWrite`, needed by `readLine`. The
// return value is bytes read, `0` at EOF, or a negative errno on failure — `readLine`'s own loop
// treats "not strictly positive" as "stop reading" either way, matching stage 0's own refill check.
let emitLinuxRead builder i64 fd ptr len =
    emitLinuxSyscallCall(builder)(i64)(constInt(i64)(0u64)(false))(fd)(ptr)(len)("sys_read")

// Any linux-x64 4-argument syscall — the same `emitLinuxSyscallCall` mechanism with a fourth input
// register. `syscall`'s ABI passes a fourth argument in `r10`, NOT `rcx` (the `syscall` instruction
// itself clobbers `rcx` with the return address), so the constraint string gains `{r10}` as a fifth
// input alongside the existing three, `rcx`/`r11`/memory still the only clobbers. Needed for
// `openat` (`AT_FDCWD`, path, flags, mode) — every File builtin that opens a path funnels through it.
let emitLinuxSyscallCall4 builder i64 nr arg1 arg2 arg3 arg4 name =
    (let syscallType = functionType(i64)([i64, i64, i64, i64, i64])(5u32)(false)
    in
        let syscallAsm = getInlineAsm(syscallType)("syscall")("={rax},{rax},{rdi},{rsi},{rdx},{r10},~{rcx},~{r11},~{memory}")(true)(false)
        in buildCall(builder)(syscallType)(syscallAsm)([nr, arg1, arg2, arg3, arg4])(5u32)(name))

// `openat(AT_FDCWD, path, flags, mode)` — stage 0's own `EmitLinuxSyscall` translates a plain
// `open` request to `openat` with `AT_FDCWD` (`-100`, bit-reinterpreted to its `u64` register value
// the same way the string-header view-bit sign trick does) the same way, since `open` itself is
// unavailable on some kernels/architectures and `openat` is the portable primitive.
let emitLinuxOpenat builder i64 pathAddr flags mode =
    emitLinuxSyscallCall4(builder)(i64)(constInt(i64)(257u64)(false))(constInt(i64)(Ashes.Number.UInt.fromInt64(-100))(false))(pathAddr)(flags)(mode)("sys_openat")

let emitLinuxClose builder i64 fd =
    (let zero = constInt(i64)(0u64)(false)
    in
        emitLinuxSyscallCall(builder)(i64)(constInt(i64)(3u64)(false))(fd)(zero)(zero)("sys_close"))

// `mkdir(path, mode)` — syscall 83, two real arguments (the third register is unused by the
// kernel handler, so the shared 3-argument `emitLinuxSyscallCall` mechanism passes a harmless `0`).
let emitLinuxMkdir builder i64 pathAddr mode =
    emitLinuxSyscallCall(builder)(i64)(constInt(i64)(83u64)(false))(pathAddr)(mode)(constInt(i64)(0u64)(false))("sys_mkdir")

// `rename(oldpath, newpath)` — syscall 82, two real arguments, same "harmless extra `0`" shape as
// `emitLinuxMkdir`.
let emitLinuxRename builder i64 oldPathAddr newPathAddr =
    emitLinuxSyscallCall(builder)(i64)(constInt(i64)(82u64)(false))(oldPathAddr)(newPathAddr)(constInt(i64)(0u64)(false))("sys_rename")

// `lseek(fd, offset, whence)` — syscall 8; `whence` `2` (`SEEK_END`) measures a file, `0`
// (`SEEK_SET`) rewinds it before the read loop.
let emitLinuxLseek builder i64 fd offset whence =
    emitLinuxSyscallCall(builder)(i64)(constInt(i64)(8u64)(false))(fd)(offset)(whence)("sys_lseek")

// `chmod(path, mode)` — syscall 90, two real arguments, same "harmless extra `0`" shape as
// `emitLinuxMkdir`.
let emitLinuxChmod builder i64 pathAddr mode =
    emitLinuxSyscallCall(builder)(i64)(constInt(i64)(90u64)(false))(pathAddr)(mode)(constInt(i64)(0u64)(false))("sys_chmod")

// Any linux-x64 6-argument syscall — `mmap` takes (addr, len, prot, flags, fd, off); the fifth and
// sixth arguments ride `r8`/`r9` per the syscall ABI, joining `r10` for the fourth.
let emitLinuxSyscallCall6 builder i64 nr arg1 arg2 arg3 arg4 arg5 arg6 name =
    (let syscallType = functionType(i64)([i64, i64, i64, i64, i64, i64, i64])(7u32)(false)
    in
        let syscallAsm = getInlineAsm(syscallType)("syscall")("={rax},{rax},{rdi},{rsi},{rdx},{r10},{r8},{r9},~{rcx},~{r11},~{memory}")(true)(false)
        in buildCall(builder)(syscallType)(syscallAsm)([nr, arg1, arg2, arg3, arg4, arg5, arg6])(7u32)(name))

// `mmap(NULL, len, PROT_READ, MAP_PRIVATE, fd, 0)` — syscall 9, the read-only private mapping
// `Ashes.IO.File.mmap` builds its zero-copy `Bytes` view over. A failed mapping returns a negative
// errno encoded in the pointer word: any result above `-4096` as an unsigned value is an error.
let emitLinuxMmapReadPrivate builder i64 len fd =
    (let zero = constInt(i64)(0u64)(false)
    in
        emitLinuxSyscallCall6(builder)(i64)(constInt(i64)(9u64)(false))(zero)(len)(constInt(i64)(1u64)(false))(constInt(i64)(2u64)(false))(fd)(zero)("sys_mmap"))

// `pipe2(fds, 0)` — syscall 293, writing two `i32` fds into the buffer at `fdsAddr`; the modern
// primitive (`pipe` is unavailable on some architectures), same shape as stage 0's `SyscallPipe2`.
let emitLinuxPipe2 builder i64 fdsAddr =
    (let zero = constInt(i64)(0u64)(false)
    in
        emitLinuxSyscallCall(builder)(i64)(constInt(i64)(293u64)(false))(fdsAddr)(zero)(zero)("sys_pipe2"))

// `dup2(oldfd, newfd)` — syscall 33, the child-side stdio rewiring primitive.
let emitLinuxDup2 builder i64 oldFd newFd =
    emitLinuxSyscallCall(builder)(i64)(constInt(i64)(33u64)(false))(oldFd)(newFd)(constInt(i64)(0u64)(false))("sys_dup2")

// `fork()` — syscall 57. The argument registers are ignored on x86-64; the `17` in the first
// mirrors stage 0's own emission, where the same call site is `clone(SIGCHLD, 0, 0)` on arm64
// (SIGCHLD = 17 is what makes the child `wait()`-able there).
let emitLinuxFork builder i64 =
    emitLinuxSyscallCall(builder)(i64)(constInt(i64)(57u64)(false))(constInt(i64)(17u64)(false))(constInt(i64)(0u64)(false))(constInt(i64)(0u64)(false))("sys_fork")

// `execve(path, argv, envp)` — syscall 59; every argument is an address word.
let emitLinuxExecve builder i64 pathAddr argvAddr envpAddr =
    emitLinuxSyscallCall(builder)(i64)(constInt(i64)(59u64)(false))(pathAddr)(argvAddr)(envpAddr)("sys_execve")

// `wait4(pid, status, options, rusage)` — syscall 61, the rusage pointer explicitly zero.
let emitLinuxWait4 builder i64 pid statusAddr options =
    emitLinuxSyscallCall4(builder)(i64)(constInt(i64)(61u64)(false))(pid)(statusAddr)(options)(constInt(i64)(0u64)(false))("sys_wait4")

// `kill(pid, signal)` — syscall 62, same "harmless extra `0`" shape as `emitLinuxMkdir`.
let emitLinuxKill builder i64 pid signal =
    emitLinuxSyscallCall(builder)(i64)(constInt(i64)(62u64)(false))(pid)(signal)(constInt(i64)(0u64)(false))("sys_kill")
