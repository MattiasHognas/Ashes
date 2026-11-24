// A recursive binding with a written arithmetic requirement, used at Int and at Float: the operator
// over a parameter and the binding's own recursive result dispatches through the requirement's
// dictionary.
// expect: 8 6.0
let times = 3

let recursive addTimes : a -> Int -> a requires {Add(a)} =
    given (x) ->
        given (n) ->
            if n >= times
            then x
            else x + addTimes(x)(n + 1)

Ashes.IO.print(Ashes.Text.fromInt(addTimes(2)(0)) + " " + Ashes.Text.fromFloat(addTimes(1.5)(0)))
