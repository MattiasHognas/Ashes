// expect: 9:9|-1:-1|20:20|2:2|1:1|-5:-5|false:false|2:2|7:7|5:5|16:16|4:4|-6:-6|true:true|true:true|true:true|true:true|true:true|true:true
import Ashes.Trait
let int : Int -> Str =
    given (value) -> Show.show(value)

let bool : Bool -> Str =
    given (value) -> Show.show(value)

let pair left right = left + ":" + right

let join left right = left + "|" + right

let output = join(pair(int(7 + 2))(int(Add.add(7)(2))))(join(pair(int(7 - 8))(int(Subtract.subtract(7)(8))))(join(pair(int(5 * 4))(int(Multiply.multiply(5)(4))))(join(pair(int(8 / 4))(int(Divide.divide(8)(4))))(join(pair(int(7 % 3))(int(Remainder.remainder(7)(3))))(join(pair(int(-5))(int(Negate.negate(5))))(join(pair(bool(!true))(bool(Not.not(true))))(join(pair(int(3 & 6))(int(BitAnd.bitAnd(3)(6))))(join(pair(int(3 | 6))(int(BitOr.bitOr(3)(6))))(join(pair(int(3 ^ 6))(int(BitXor.bitXor(3)(6))))(join(pair(int(1 << 4))(int(ShiftLeft.shiftLeft(1)(4))))(join(pair(int(8 >> 1))(int(ShiftRight.shiftRight(8)(1))))(join(pair(int(~5))(int(BitwiseNot.bitwiseNot(5))))(join(pair(bool(3 == 3))(bool(Eq.equal(3)(3))))(join(pair(bool(3 != 4))(bool(Eq.notEqual(3)(4))))(join(pair(bool(3 < 4))(bool(Ord.less(3)(4))))(join(pair(bool(3 <= 3))(bool(Ord.lessOrEqual(3)(3))))(join(pair(bool(4 > 3))(bool(Ord.greater(4)(3))))(pair(bool(4 >= 4))(bool(Ord.greaterOrEqual(4)(4)))))))))))))))))))))
in Ashes.IO.print(output)
