// expect: done
// A member of a recursive group whose members differ in arity performs a capability. The group is
// not merged into a dispatch loop, so this covers the plain group typing path on its own.
let recursive ping =
    given (n: Int) ->
        if n == 0
        then Ashes.IO.print("done")
        else pong(n - 1)(1)
and pong =
    given (n: Int) ->
        given (k: Int) -> ping(n - k)

ping(4)
