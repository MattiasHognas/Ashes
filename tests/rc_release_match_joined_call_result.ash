// Regression: two fresh RC call results merged through a match join stay releasable at the
// consuming read (the joined value keeps its newly-produced ownership only when EVERY arm is
// fresh). Output checks correctness; the companion C# memory-plateau test checks the leak.
// expect: 3896
let recursive drive =
    given (i) ->
        given (acc) ->
            match i with
                | 0 -> acc
                | _ ->
                    let f =
                        given (n) -> Ashes.Text.fromInt(n) + "!"
                    in
                        let v =
                            match i - i / 2 * 2 with
                                | 0 -> f(i)
                                | _ -> f(i + 1)
                        in drive(i - 1)(acc + Ashes.Text.byteLength(v))

Ashes.IO.print(drive 1000 0)
