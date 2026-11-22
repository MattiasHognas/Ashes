// An if branch returning a reference-counted list bound by a let before the if, read again after
// the join.
// expect: 3 4
let choose (flag: Bool) (n: Int) =
    (let xs =
        let base = n * 2
        in [base, base + 1]
    in
        let picked =
            if flag
            then [n]
            else xs
        in Ashes.Collection.List.length(picked) + Ashes.Collection.List.length(xs))

Ashes.IO.print(Ashes.Text.fromInt(choose(true)(3)) + " " + Ashes.Text.fromInt(choose(false)(4)))
