// Public package root for the self-hosted CLI orchestration layer: the actual `ashes` executable
// entry point.
//
// Boundary:
// - Package-level entry only: this package wires already-implemented frontend/formatter/semantics/
//   backend APIs into the observable `ashes` command contract (docs/md/reference/cli.md). It must
//   not duplicate parsing, formatting, inference, lowering, or code generation logic those packages
//   already own.
// - Only `add`, `compile` (single linux-x64 file), `fmt`, `init`, `remove`, `restore` (path
//   dependencies only), `run` (single file), `tree`, and `why` are implemented so far; the other
//   subcommands remain unported (see
//   docs/md/future/SELF_HOSTING.md's CLI checklist). Dispatch itself lives in
//   `AshesCompiler.Cli.Dispatch` (`runCli`), not here, so it stays directly testable the same way
//   every other command is.

import AshesCompiler.Cli.Dispatch
Ashes.IO.exit(runCli(Ashes.IO.args))
