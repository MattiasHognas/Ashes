// The `ashes` CLI's shared entry point: dispatches a subcommand name to its implementation,
// matching stage 0's top-level argument scanning, help, and exit-code discipline (`Main` in
// `src/Ashes.Cli/Program.cs`, docs/md/reference/cli.md).
//
// Invariants:
// - No arguments at all falls through to usage (exit code 2), matching stage 0's `Usage()`
//   default. A bare `--help`/`-h` as the FIRST argument is global help (exit code 0) regardless of
//   what follows it — mirrors stage 0's own check, which runs on `args[0]` alone, before any
//   subcommand-specific parsing sees the rest.
// - The command name is matched case-insensitively (stage 0 lowercases `args[0]` before
//   dispatch); every other argument is passed through unchanged and unsliced beyond dropping the
//   command name itself.
// - Dispatch only covers the six subcommands this package has actually ported so far
//   (`add`/`fmt`/`init`/`remove`/`tree`/`why`) plus the shared usage/help paths —
//   `compile`/`run`/`repl`/`test`/`restore`/the registry commands, and `--version`/`-v` (stage 0's
//   version string comes from assembly metadata this package has no equivalent of yet) all remain
//   unported and fall through to usage like any other unknown command, rather than reproducing
//   behavior that doesn't exist here yet. Ashes doesn't preserve backward compatibility for
//   retired commands: an unrecognized name (including a former command name) is just an
//   unrecognized name.

import AshesCompiler.Cli.Add
import AshesCompiler.Cli.Fmt
import AshesCompiler.Cli.Init
import AshesCompiler.Cli.Remove
import AshesCompiler.Cli.Tree
import AshesCompiler.Cli.Why
export (
    value usageText,
    value dispatchCommand,
    value runCli,
)

let recursive lowerAsciiChar value =
    match value with
        | "A" -> "a"
        | "B" -> "b"
        | "C" -> "c"
        | "D" -> "d"
        | "E" -> "e"
        | "F" -> "f"
        | "G" -> "g"
        | "H" -> "h"
        | "I" -> "i"
        | "J" -> "j"
        | "K" -> "k"
        | "L" -> "l"
        | "M" -> "m"
        | "N" -> "n"
        | "O" -> "o"
        | "P" -> "p"
        | "Q" -> "q"
        | "R" -> "r"
        | "S" -> "s"
        | "T" -> "t"
        | "U" -> "u"
        | "V" -> "v"
        | "W" -> "w"
        | "X" -> "x"
        | "Y" -> "y"
        | "Z" -> "z"
        | _ -> value

let recursive lowerAscii (text: Str) =
    match Ashes.Text.unconsText(text) with
        | None -> ""
        | Some((head, tail)) -> lowerAsciiChar(head) + lowerAscii(tail)

let usageText = "Usage: ashes <command> [args]\n" + "\n" + "Commands:\n" + "  add      <package> [--project <manifest>] [--path <dir>] [--dev]\n" + "  fmt      <file|dir> [-w]\n" + "  init\n" + "  remove   <package> [--project <manifest>]\n" + "  tree     [--project <manifest>]\n" + "  why      <namespace> [--project <manifest>]\n"

let printUsage exitCode =
    (let _ = Ashes.IO.print(usageText)
    in exitCode)

// The stable, testable core of `ashes <command>`: `command` is ALREADY lowercased by the caller.
let dispatchCommand command rest =
    match command with
        | "add" -> runAdd(rest)
        | "fmt" -> runFmt(rest)
        | "init" -> runInit(rest)
        | "remove" -> runRemove(rest)
        | "tree" -> runTree(rest)
        | "why" -> runWhy(rest)
        | _ -> printUsage(2)

// The full `ashes` entry point: parses `args`, dispatches to the matching subcommand, and returns
// the process exit code.
let runCli args =
    match args with
        | [] -> printUsage(2)
        | command :: rest ->
            let lowered = lowerAscii(command)
            in
                if lowered == "--help"
                then printUsage(0)
                else
                    if lowered == "-h"
                    then printUsage(0)
                    else dispatchCommand(lowered)(rest)
