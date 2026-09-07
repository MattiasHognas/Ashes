// expect: 9:9|-1:-1|20:20|2:2|1:1|-5:-5|false:false|2:2|7:7|5:5|16:16|4:4|-6:-6|true:true|true:true|true:true|true:true|true:true|true:true
import Ashes.Trait
let int : Int -> Str =
    given (value) -> Show.show(value)

let bool : Bool -> Str =
    given (value) -> Show.show(value)

let pair left right = left + ":" + right

let join left right = left + "|" + right

let output =
    4
    |> Ord.greaterOrEqual(4)
    |> bool
    |> pair(bool(4 >= 4))
    |> join(3
    |> Ord.greater(4)
    |> bool
    |> pair(bool(4 > 3)))
    |> join(3
    |> Ord.lessOrEqual(3)
    |> bool
    |> pair(bool(3 <= 3)))
    |> join(4
    |> Ord.less(3)
    |> bool
    |> pair(bool(3 < 4)))
    |> join(4
    |> Eq.notEqual(3)
    |> bool
    |> pair(bool(3 != 4)))
    |> join(3
    |> Eq.equal(3)
    |> bool
    |> pair(bool(3 == 3)))
    |> join(5
    |> BitwiseNot.bitwiseNot
    |> int
    |> pair(int(~5)))
    |> join(1
    |> ShiftRight.shiftRight(8)
    |> int
    |> pair(int(8 >> 1)))
    |> join(4
    |> ShiftLeft.shiftLeft(1)
    |> int
    |> pair(int(1 << 4)))
    |> join(6
    |> BitXor.bitXor(3)
    |> int
    |> pair(int(3 ^ 6)))
    |> join(6
    |> BitOr.bitOr(3)
    |> int
    |> pair(int(3 | 6)))
    |> join(6
    |> BitAnd.bitAnd(3)
    |> int
    |> pair(int(3 & 6)))
    |> join(true
    |> Not.not
    |> bool
    |> pair(bool(!true)))
    |> join(5
    |> Negate.negate
    |> int
    |> pair(int(-5)))
    |> join(3
    |> Remainder.remainder(7)
    |> int
    |> pair(int(7 % 3)))
    |> join(4
    |> Divide.divide(8)
    |> int
    |> pair(int(8 / 4)))
    |> join(4
    |> Multiply.multiply(5)
    |> int
    |> pair(int(5 * 4)))
    |> join(8
    |> Subtract.subtract(7)
    |> int
    |> pair(int(7 - 8)))
    |> join(2
    |> Add.add(7)
    |> int
    |> pair(int(7 + 2)))
in Ashes.IO.print(output)
