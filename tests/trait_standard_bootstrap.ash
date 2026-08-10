// expect: true|42|[1, 2]|Some(7)|true|(8, "x")
import Ashes.Trait
let equal = Eq.equal([1, 2])([1, 2])

let okLeft : Result(Str, Int) = Ok(1)

let okRight : Result(Str, Int) = Ok(1)
in Ashes.IO.print(Show.show(equal) + "|" + Show.show(Add.add(40)(2)) + "|" + Show.show([1, 2]) + "|" + Show.show(Some(7)) + "|" + Show.show(Eq.equal(okLeft)(okRight)) + "|" + Show.show((8, "x")))
