// expect: buffered-stdout
// expect-stderr-contains: failure: expected
// exit: 7
let _ = Ashes.IO.writeBuffered("buffered-stdout")
in
    let _ = Ashes.IO.writeError("failure: ")
    in
        let _ = Ashes.IO.writeErrorLine("expected")
        in Ashes.IO.exit(7)
