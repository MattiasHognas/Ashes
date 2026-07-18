# Self-Hosting: Building the Ashes Compiler in Ashes

| Capability | Complete |
|---|---|
| Unsigned integer support (`u8`, `u16`, `u32`, `u64`) | Yes |
| Byte type (`u8`) and byte literals | Yes |
| Bitwise operators (`&`, `\|`, `^`, `<<`, `>>`, `~`) | Yes |
| Numeric text conversions (`parseInt`, `parseFloat`, `fromInt`, `fromFloat`, `toHex`) | Yes |
| FFI surface (`external` functions/types, pointer signatures, symbol@library imports) | Yes |
| Immutable `Bytes` type with indexed access and append helpers | Yes |
| Little-endian byte encode/decode helpers (`u16/u32/u64`) | Yes |
| Binary file output (`Ashes.IO.File.writeBytes`) | Yes |
| String helper module (`substring`, `length`, `indexOf`, `startsWith`, `contains`, `split`, `trim`, char predicates) | Yes |
| Persistent immutable map (`Ashes.Collection.Map`) | Yes |
| Persistent immutable array (`Ashes.Collection.Array`) | Yes |
| Records and record-update syntax | Yes |
| User-written type annotations | Yes |
| Project/module compilation support across multiple files | Yes |
| Catchable error propagation for compile pipeline flows | Yes |
| Memory-management hardening (grow-on-demand arena + scope reclamation) | Yes |
| Large-ADT exhaustiveness/performance hardening | Yes |
| JSON parsing/serialization support for `ashes.json` and JSON-RPC (CLI/LSP/DAP) | Yes |
| Stdio JSON-RPC framing utilities (Content-Length read/write over byte streams) for LSP/DAP | Yes |
| Interactive subprocess control with piped stdin/stdout/stderr, async reads, and request timeouts (DAP debugger backends) | Yes |
| Regex utilities/module for protocol and tooling text parsing (import/project/LSP/DAP parsing paths) | Yes |
