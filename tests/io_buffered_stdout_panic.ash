// expect: pendingboom
// exit: 1
let _ = Ashes.IO.writeBuffered("pending")
in Ashes.IO.panic("boom")
