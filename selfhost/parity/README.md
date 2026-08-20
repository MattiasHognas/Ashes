# Self-hosted parity fixtures

Parity fixtures compare versioned, serialized public results from the permanent stage-0 compiler and
the self-hosted implementation. Each implementation reads the same source and expected-output files;
neither implementation calls into the other. Fixture formats belong here as their corresponding
self-hosted phases become stable.

## Token streams

Token fixtures live in `frontend/tokens` as matching `<name>.source` and `<name>.tokens` files. The
`ashes-token-v1` format starts with its schema marker, then records one token per LF-terminated line.
Each record has these tab-separated fields:

1. token-kind name;
2. lowercase hex of the token text's UTF-8 bytes;
3. decoded integer payload;
4. decoded float payload;
5. token start as a UTF-8 byte offset;
6. token length in UTF-8 bytes.

UTF-8 hex keeps embedded tabs, newlines, carriage returns, and quotes from changing the record shape.
For a float token, the payload field preserves the token spelling when parsing that spelling produces
the decoded payload. This avoids host-specific float rendering while still exposing a lexer that
decoded its spelling incorrectly. Non-float tokens serialize their zero float payload as `0`.

The shared fixtures currently cover every keyword, every operator and delimiter, decoded numeric,
string, and rune payloads, comments, and Unicode identifiers and byte spans. Malformed input belongs
to the future structured-diagnostic parity format rather than the token-only format.
