// expect: 250000
// Stress-tests the chunked arena allocator: 250 000 cons cells ≈ 6 MB forces at
// least one heap-chunk growth during the build phase, exercising EmitHeapGrow and
// the chunk-linked-list reclamation in EmitReclaimArenaChunks.
let recursive build n acc = 
    if n <= 0
    then acc
    else build(n - 1)(1 :: acc)
in 
    let recursive len xs acc = 
        match xs with
            | [] -> acc
            | _ :: rest -> len(rest)(acc + 1)
    in Ashes.IO.print(len(build(250000)([]))(0))
