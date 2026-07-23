// Regression: every TCO back edge must use the final runtime-managed parameter set. A later
// sibling branch can promote parameters that an earlier branch also replaces with arena values.
// expect: 9 x
import Ashes.IO as io
import Ashes.Text as text
let recursive loop : Int -> BigInt -> BigInt -> Str -> Unit =
    given (n) ->
        given (left) ->
            given (right) ->
                given (suffix) ->
                    if n == 0
                    then io.print(text.fromBigInt(left + right) + " " + suffix)
                    else
                        if n % 2 == 0
                        then loop(n - 1)(left + right)(right)(suffix + "x")
                        else loop(n - 1)(left)(left * right)(suffix)

loop(2)(1N)(2N)("")
