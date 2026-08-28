// Public package root for the self-hosted CLI orchestration layer.
//
// Boundary:
// - Package-level entry only: this package wires already-implemented frontend/formatter/semantics
//   APIs into the observable `ashes` command contract (docs/md/reference/cli.md). It must not
//   duplicate parsing, formatting, or inference logic those packages already own.
// - Only `fmt` and `init` are implemented so far; the other subcommands remain unported (see
//   docs/md/future/SELF_HOSTING.md's CLI checklist).

import AshesCompiler.Cli.Fmt
import AshesCompiler.Cli.Init
Unit
