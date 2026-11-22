// A match arm returning a reference-counted let binding bound before the match, read again after
// the join, beside an arm that builds a fresh string.
// expect: 40/__in_place_41 __in_place_42/__in_place_42
let run (pick: Int) (seed: Int) =
    (let callName =
        let next = seed + 1
        in "__in_place_" + Ashes.Text.fromInt(next)
    in
        let groupName =
            match pick with
                | 0 -> Ashes.Text.fromInt(seed)
                | _ -> callName
        in groupName + "/" + callName)

Ashes.IO.print(run(0)(40) + " " + run(1)(41))
