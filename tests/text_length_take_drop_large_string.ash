// expect: 300000 5 299995 xxxxx 300003 ab
// Text.length, take, and drop once walked a string one code point at a time through unconsText,
// with length and take recursing once per code point: a string of a few hundred thousand bytes
// overflowed the stack. All three now walk the bytes with an index and slice, so they stay flat
// on a large string and still count code points, not bytes.
import Ashes.Text as text
let recursive build n acc =
    if n == 0
    then acc
    else build(n - 1)(acc + "x")

let source = build(300000)("")

let mixed = source + "aäb"

Ashes.IO.print(text.fromInt(text.length(source)) + " " + text.fromInt(text.length(text.take(source)(5))) + " " + text.fromInt(text.length(text.drop(source)(5))) + " " + text.take(source)(5) + " " + text.fromInt(text.length(mixed)) + " " + text.take(text.drop(mixed)(300000))(1) + text.drop(mixed)(300002))
