// expect: s 3
// A loop's exit wraps a `let`-bound cons cell over a list matched out of its parameter in a
// constructor the return copies onto the reference-counted heap: the copy retains the matched
// list itself, so the duplicate the arena cell took is withdrawn like one of a cell built in place.
type State =
    | S(List(Int))

let recursive advance r state =
    match state with
        | S(perm) ->
            let perm2 = 0 :: perm
            in
                if r == 0
                then S(perm2)
                else advance(r - 1)(S(perm2))

match advance(0)(S([1, 2])) with
    | S(xs) ->
        Ashes.IO.print("s " + Ashes.Text.fromInt(Ashes.Collection.List.length(xs)))
