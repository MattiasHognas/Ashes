// Regression: a read-only builtin releases a newly produced RC call result it consumed (the nested
// helper's string), while a let-BOUND binding read twice stays owned by its scope (released once,
// never per read). Mixing both in one accumulator exercises release-vs-decline on the same values;
// the output only checks correctness — the companion C# memory-plateau test checks the leak itself.
// expect: 11679
let recursive drive =
    given (i) ->
        given (acc) ->
            match i with
                | 0 -> acc
                | _ ->
                    let f =
                        given (n) -> Ashes.Text.fromInt(n) + "!"
                    in
                        let s = f(i)
                        in drive(i - 1)(acc + Ashes.Text.byteLength(s) + Ashes.Text.byteLength(s) + Ashes.Text.byteLength(f(i)))

Ashes.IO.print(drive 1000 0)
