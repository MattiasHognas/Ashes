// Regression: multiple runtime-managed TCO parameters are a parallel assignment. Their successor
// values must be normalized before predecessors are released, and every exit parameter must be
// dropped exactly once after the returned Unit is produced.
// expect: 3
import Ashes.IO as io
import Ashes.Text as text
let recursive loop : Int -> BigInt -> BigInt -> Unit =
    given (n) ->
        given (left) ->
            given (right) ->
                if n == 0
                then io.print(text.fromBigInt(left + right))
                else loop(n - 1)(right)(left)

loop(1)(1N)(2N)
