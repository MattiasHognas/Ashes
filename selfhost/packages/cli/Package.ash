// Public package root for the self-hosted CLI orchestration layer: the actual `ashes` executable
// entry point.
//
// Boundary:
// - Package-level entry only: this package wires already-implemented frontend/formatter/semantics
//   APIs into the observable `ashes` command contract (docs/md/reference/cli.md). It must not
//   duplicate parsing, formatting, or inference logic those packages already own.
// - Only `add`, `fmt`, `init`, `remove`, `restore` (path dependencies only), `tree`, and `why` are
//   implemented so far; the other subcommands remain unported (see
//   docs/md/future/SELF_HOSTING.md's CLI checklist). Dispatch itself lives in
//   `AshesCompiler.Cli.Dispatch` (`runCli`), not here, so it stays directly testable the same way
//   every other command is.

import AshesCompiler.Cli.Dispatch
Ashes.IO.exit(runCli(Ashes.IO.args))
