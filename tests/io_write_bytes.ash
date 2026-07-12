// Ashes.IO.writeBytes writes a raw Bytes buffer to stdout verbatim (no UTF-8 Str constraint, unlike
// write) -- the path binary output such as a packed PBM image needs. Here the bytes spell printable
// ASCII ("P4\n") so the harness can assert stdout exactly; the raw/non-UTF-8 capability is the same
// code path. Exercises both the qualified alias call and a plain u8 list -> Bytes builder.
// expect: P4
import Ashes.IO as io
import Ashes.Bytes as bytes
let header = bytes.fromList([80u8, 52u8, 10u8])

io.writeBytes(header)
