// expect: abcd
let _ = Ashes.IO.writeBuffered("a")
in
    let _ = Ashes.IO.flush(Unit)
    in
        let _ = Ashes.IO.writeBuffered("b")
        in
            let _ = Ashes.IO.write("c")
            in Ashes.IO.writeBufferedLine("d")
