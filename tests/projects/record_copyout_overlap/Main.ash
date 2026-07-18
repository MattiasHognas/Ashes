// Regression: the arena copy-out after RestoreArenaState allocates the destination at the
// reset cursor, at or below the still-readable source, so the ranges may overlap when the
// callee allocated little before the copied record. The copy must be an overlap-safe forward
// move: as llvm.memcpy, LLVM -O2 vectorizes the overlapping copy and record fields take
// neighboring fields' values (here ballY silently became velY's 2.50).
// expect: direct: 30.50 8.58 | wrapped: 30.50 8.58
import Phys
let show (s: Ball) = Ashes.Text.formatFloat(s.ballX)(2) + " " + Ashes.Text.formatFloat(s.ballY)(2)

let direct = advance(start)

let wrapped = wrap(start)(0)

Ashes.IO.print("direct: " + show(direct) + " | wrapped: " + show(wrapped))
