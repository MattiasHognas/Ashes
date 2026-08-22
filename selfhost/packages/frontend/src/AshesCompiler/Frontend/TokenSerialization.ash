// Serializes public lexer tokens for implementation-neutral bootstrap parity comparisons.
//
// Invariants:
// - The first line is the versioned schema marker `ashes-token-v1`.
// - Records contain every public token field in kind, text, integer, float, position, length order.
// - Token text is lowercase UTF-8 hex, keeping records single-line without language-specific escaping.
// - A float payload uses its spelling when that spelling decodes to the payload, avoiding host-specific
//   float rendering while still detecting a lexer that decoded the spelling incorrectly.
// - Every line ends with LF regardless of the host platform.

import AshesCompiler.Frontend.Token
export (
    value tokenSerializationSchema,
    value serializeToken,
    value serializeTokens,
)

let tokenSerializationSchema = "ashes-token-v1"

let tokenHexDigit value =
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

let tokenTextHex value =
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
                in encode(position + 1)(encoded + tokenHexDigit(current / 16) + tokenHexDigit(current % 16))
        in encode(0)(""))

let tokenFloatPayload (token: Token) =
    match token.kind with
        | Float ->
            match Ashes.Text.parseFloat(token.text) with
                | Ok(value) ->
                    if value == token.floatValue
                    then token.text
                    else Ashes.Text.fromFloat(token.floatValue)
                | Error(_) -> Ashes.Text.fromFloat(token.floatValue)
        | _ -> "0"

let serializeToken (token: Token) = tokenKindName(token.kind) + "\t" + tokenTextHex(token.text) + "\t" + Ashes.Text.fromInt(token.intValue) + "\t" + tokenFloatPayload(token) + "\t" + Ashes.Text.fromInt(token.position) + "\t" + Ashes.Text.fromInt(token.length)

let recursive serializeTokenRecords tokens =
    match tokens with
        | [] -> ""
        | token :: tail -> serializeToken(token) + "\n" + serializeTokenRecords(tail)

let serializeTokens tokens = tokenSerializationSchema + "\n" + serializeTokenRecords(tokens)
