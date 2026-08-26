// Serializes a program parse's public diagnostic corpus for implementation-neutral bootstrap parity
// comparisons.
//
// Invariants:
// - The first line is the versioned schema marker `ashes-diagnostic-v1`.
// - The second line is `recovered-items\t<N>`, the top-level items a parse recovered despite any
//   diagnostics — proof that recovery continued past an error rather than aborting the parse.
// - Records contain code, message, byte start, and byte length, in collection order.
// - A diagnostic without a code serializes its code field as `-`.
// - Diagnostic text is lowercase UTF-8 hex, keeping records single-line without language-specific
//   escaping.
// - Every line ends with LF regardless of the host platform.

import AshesCompiler.Frontend.Token
export (
    value diagnosticSerializationSchema,
    value serializeDiagnostic,
    value serializeDiagnostics,
)

let diagnosticSerializationSchema = "ashes-diagnostic-v1"

let diagnosticHexDigit value =
    match value with
        | 0 -> "0"
        | 1 -> "1"
        | 2 -> "2"
        | 3 -> "3"
        | 4 -> "4"
        | 5 -> "5"
        | 6 -> "6"
        | 7 -> "7"
        | 8 -> "8"
        | 9 -> "9"
        | 10 -> "a"
        | 11 -> "b"
        | 12 -> "c"
        | 13 -> "d"
        | 14 -> "e"
        | _ -> "f"

let diagnosticTextHex value =
    (let bytes = Ashes.Byte.fromText(value)
    in
        let recursive encode position encoded =
            if position >= Ashes.Byte.length(bytes)
            then encoded
            else
                let current =
                    position
                    |> Ashes.Byte.get(bytes)
                    |> Ashes.Number.UInt.toInt
                in encode(position + 1)(encoded + diagnosticHexDigit(current / 16) + diagnosticHexDigit(current % 16))
        in encode(0)(""))

let diagnosticCodeText (code: Maybe(Str)) =
    match code with
        | Some(value) -> value
        | None -> "-"

let serializeDiagnostic (diagnostic: DiagnosticEntry) =
    (let span = diagnostic.span
    in diagnosticCodeText(diagnostic.code) + "\t" + diagnosticTextHex(diagnostic.message) + "\t" + Ashes.Text.fromInt(span.start) + "\t" + Ashes.Text.fromInt(span.end - span.start))

let recursive serializeDiagnosticRecords diagnostics =
    match diagnostics with
        | [] -> ""
        | diagnostic :: tail -> serializeDiagnostic(diagnostic) + "\n" + serializeDiagnosticRecords(tail)

let serializeDiagnostics recoveredItemCount diagnostics = diagnosticSerializationSchema + "\n" + "recovered-items\t" + Ashes.Text.fromInt(recoveredItemCount) + "\n" + serializeDiagnosticRecords(diagnostics)
