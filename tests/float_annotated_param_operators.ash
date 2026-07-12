// Regression: operators on parameters whose Float type comes only from a type annotation must
// resolve as Float, not default an unresolved type variable to Int. Pre-fix, `a * b` on annotated
// Float params (and a recursive Float accumulator leading with a still-unresolved param) reported
// ASH002 Float-vs-Int, because the overload was picked before the annotation was applied. The
// annotation now seeds the parameter types before the body is lowered.
// expect: 12.0 0.5
import Ashes.IO as io
import Ashes.Text as text
let mul : Float -> Float -> Float = 
    given (a) -> 
        given (b) -> a * b

let recursive shrink : Float -> Float -> Int -> Float = 
    given (acc) -> 
        given (x) -> 
            given (n) -> 
                if n == 0
                then acc
                else 
                    let x2 = x * x
                    in shrink(acc + x2 - x)(x)(n - 1)

io.write(text.fromFloat(mul(3.0)(4.0)) + " " + text.fromFloat(shrink(1.0)(0.5)(2)) + "\n")
