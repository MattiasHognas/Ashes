// Ashes.String.substring is codepoint-indexed. It was take(drop(...)) — an O(start + count^2)
// char-walk with per-char string concatenation, so a sliding k-mer window was catastrophic
// (8000 chars -> ~63 s). It now does a single codepoint->byte offset walk + Ashes.Bytes.subText
// (O(start + count), no concatenation; ~2000x faster on that window), staying UTF-8 correct.
// For byte-indexed O(1) slicing, Ashes.Bytes.subText remains the fast path.
// expect: hello|world|llo|||é la|caf
import Ashes.String as str
import Ashes.IO as io
io.print(str.substring("hello world")(0)(5) + "|" + str.substring("hello world")(6)(5) + "|" + str.substring("hello")(2)(100) + "|" + str.substring("hello")(10)(3) + "|" + str.substring("hello")(0)(0) + "|" + str.substring("café latte")(3)(4) + "|" + str.substring("café")(0)(3))
