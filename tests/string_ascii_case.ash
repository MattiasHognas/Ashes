// Regression: Ashes.String.toAsciiUpper / toAsciiLower (backed by the Ashes.Text.asciiUpper /
// asciiLower single-pass byte-map intrinsics). ASCII-only by design: a-z <-> A-Z flip bit 0x20;
// every byte of a multibyte UTF-8 sequence is >= 0x80 and passes through byte-identical, so
// non-ASCII text (here: Åäö, grüße, 日本) survives unchanged. Digits/punctuation untouched.
// Both the direct intrinsics and the String wrappers are exercised, plus idempotence and empty.
// expect: HELLO 123 | hello 123 | MIXED Åäö GRüßE 日本 ABCZ | mixed Åäö grÜsse 日本 abcz | SAME | same | ok
import Ashes.String as str
import Ashes.Text as text
import Ashes.IO as io
let up = str.toAsciiUpper("Hello 123")

let low = str.toAsciiLower("Hello 123")

let up2 = text.asciiUpper("mixed Åäö grüße 日本 abcZ")

let low2 = text.asciiLower("MIXED Åäö GRÜSSE 日本 ABCz")

let emptyOk =
    if str.toAsciiUpper("") == ""
    then "ok"
    else "bad"

io.print(up + " | " + low + " | " + up2 + " | " + low2 + " | " + str.toAsciiUpper(str.toAsciiUpper("same")) + " | " + str.toAsciiLower(str.toAsciiLower("SAME")) + " | " + emptyOk)
