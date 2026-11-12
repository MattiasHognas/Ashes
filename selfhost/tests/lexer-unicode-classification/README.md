# Lexer Unicode classifications

The lexer uses three fixed Unicode predicates instead of compiling regular expressions at
startup. Their generated source must preserve the bundled PCRE2 payload's answers to `^\p{L}$`,
`^[\p{L}\p{Nd}]$`, and `^\s$` with UTF and UCP enabled. ASCII fast paths stay in `Lexer.ash`.

After changing `Pcre2Version` and refreshing the linux-x64 payload, regenerate with:

```bash
bash scripts/selfhost-dev/generate-lexer-unicode.sh --write
```

Use `--verify` to check that the checked-in source still matches the pinned payload. The generator
links directly against that bitcode, without system PCRE2 headers or a system PCRE2 library. It
retains its scratch directory and prints the path to `reference.bin`, one byte for each code point
from 0 through U+10FFFF. The compiler itself does not invoke the generator.

Compile this test project with each compiler stage under test, then run the resulting executable
with that reference path as its sole argument. Use the normal self-hosting memory scopes and job
wrapper. It checks all 1,114,112 entries, including surrogate rejection, and fails on any mismatch
or a wrong-sized reference file. The ordinary frontend suite separately checks token spans and
identifier/whitespace integration.
