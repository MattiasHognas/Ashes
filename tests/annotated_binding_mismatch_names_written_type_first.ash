// A value that does not fit its binding's written type is reported with the written type first.
// expect-compile-error: ASH002 Type mismatch: Int vs Str.
let count : Int = "abc"

count
