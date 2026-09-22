// A program whose last line is a bare call to a member of a recursive group: the entry function
// returns nothing, so the call is not fused into a tail call there.
// expect:
let recursive isEven n =
    if n == 0
    then true
    else isOdd(n - 1)
and isOdd n =
    if n == 0
    then false
    else isEven(n - 1)

isEven(4)
