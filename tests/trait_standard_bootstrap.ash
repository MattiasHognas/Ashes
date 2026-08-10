// expect: true|42|[1, 2]|Some(7)|true|(8, "x")
import Ashes.Trait
let equal = Eq.equal([1, 2])([1, 2])
in Ashes.IO.print(Show.show(equal) + "|" + Show.show(Add.add(40)(2)) + "|" + Show.show([1, 2]) + "|" + Show.show(Some(7)) + "|" + Show.show(Eq.equal(Ok(1))(Ok(1))) + "|" + Show.show((8, "x")))
