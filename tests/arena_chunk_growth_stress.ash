// expect: 200000
// Stress-tests the chunked arena allocator while keeping recursion depth lower on
// Windows: four 50 000-element lists keep 200 000 cons cells live (≈ 4.8 MB),
// which still forces heap-chunk growth and exercises chunk reclamation.
let rec build n acc = 
    if n <= 0
    then acc
    else build(n - 1)(1 :: acc)
in 
    let rec len xs acc = 
        match xs with
            | [] -> acc
            | _ :: rest -> len(rest)(acc + 1)
    in 
        let chunk = 50000
        in 
            let a = build(chunk)([])
            in 
                let b = build(chunk)([])
                in 
                    let c = build(chunk)([])
                    in 
                        let d = build(chunk)([])
                        in Ashes.IO.print(len(a)(0) + len(b)(0) + len(c)(0) + len(d)(0))
