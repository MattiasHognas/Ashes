// expect: ok
import Ashes.Test.assertEqual
import Ashes.Trait
let check : a -> a -> a -> Unit needs {ConsoleIO} requires {Eq(a)} =
    given (expected) ->
        given (operatorValue) ->
            given (methodValue) ->
                let checkedOperator = assertEqual(expected)(operatorValue)
                in assertEqual(expected)(methodValue)

let checkHash : a -> Unit needs {ConsoleIO} requires {Hash(a)} =
    given (value) ->
        value
        |> Hash.hash
        |> assertEqual(Hash.hash(value))

let intEq =
    7
    |> Eq.equal(7)
    |> check(true)(7 == 7)

let intNe =
    8
    |> Eq.notEqual(7)
    |> check(true)(7 != 8)

let intLt =
    8
    |> Ord.less(7)
    |> check(true)(7 < 8)

let intLe =
    7
    |> Ord.lessOrEqual(7)
    |> check(true)(7 <= 7)

let intGt =
    7
    |> Ord.greater(8)
    |> check(true)(8 > 7)

let intGe =
    8
    |> Ord.greaterOrEqual(8)
    |> check(true)(8 >= 8)

let intAdd =
    2
    |> Add.add(7)
    |> check(9)(7 + 2)

let intSubtract =
    2
    |> Subtract.subtract(7)
    |> check(5)(7 - 2)

let intMultiply =
    2
    |> Multiply.multiply(6)
    |> check(12)(6 * 2)

let intDivide =
    2
    |> Divide.divide(8)
    |> check(4)(8 / 2)

let intRemainder =
    3
    |> Remainder.remainder(7)
    |> check(1)(7 % 3)

let intNegate =
    5
    |> Negate.negate
    |> check(-5)(-5)

let intBitAnd =
    6
    |> BitAnd.bitAnd(3)
    |> check(2)(3 & 6)

let intBitOr =
    6
    |> BitOr.bitOr(3)
    |> check(7)(3 | 6)

let intBitXor =
    6
    |> BitXor.bitXor(3)
    |> check(5)(3 ^ 6)

let intShiftLeft =
    4
    |> ShiftLeft.shiftLeft(1)
    |> check(16)(1 << 4)

let intShiftRight =
    1
    |> ShiftRight.shiftRight(8)
    |> check(4)(8 >> 1)

let intBitwiseNot =
    5
    |> BitwiseNot.bitwiseNot
    |> check(-6)(~5)

let floatEq =
    1.5
    |> Eq.equal(1.5)
    |> check(true)(1.5 == 1.5)

let floatNe =
    2.5
    |> Eq.notEqual(1.5)
    |> check(true)(1.5 != 2.5)

let floatLt =
    2.5
    |> Ord.less(1.5)
    |> check(true)(1.5 < 2.5)

let floatLe =
    1.5
    |> Ord.lessOrEqual(1.5)
    |> check(true)(1.5 <= 1.5)

let floatGt =
    1.5
    |> Ord.greater(2.5)
    |> check(true)(2.5 > 1.5)

let floatGe =
    2.5
    |> Ord.greaterOrEqual(2.5)
    |> check(true)(2.5 >= 2.5)

let floatAdd =
    2.0
    |> Add.add(1.5)
    |> check(3.5)(1.5 + 2.0)

let floatSubtract =
    2.0
    |> Subtract.subtract(3.5)
    |> check(1.5)(3.5 - 2.0)

let floatMultiply =
    2.0
    |> Multiply.multiply(1.5)
    |> check(3.0)(1.5 * 2.0)

let floatDivide =
    2.0
    |> Divide.divide(4.0)
    |> check(2.0)(4.0 / 2.0)

let floatNegate =
    1.5
    |> Negate.negate
    |> check(-1.5)(-1.5)

let bigEq =
    7N
    |> Eq.equal(7N)
    |> check(true)(7N == 7N)

let bigNe =
    8N
    |> Eq.notEqual(7N)
    |> check(true)(7N != 8N)

let bigLt =
    8N
    |> Ord.less(7N)
    |> check(true)(7N < 8N)

let bigLe =
    7N
    |> Ord.lessOrEqual(7N)
    |> check(true)(7N <= 7N)

let bigGt =
    7N
    |> Ord.greater(8N)
    |> check(true)(8N > 7N)

let bigGe =
    8N
    |> Ord.greaterOrEqual(8N)
    |> check(true)(8N >= 8N)

let bigAdd =
    2N
    |> Add.add(7N)
    |> check(9N)(7N + 2N)

let bigSubtract =
    2N
    |> Subtract.subtract(7N)
    |> check(5N)(7N - 2N)

let bigMultiply =
    2N
    |> Multiply.multiply(6N)
    |> check(12N)(6N * 2N)

let bigDivide =
    2N
    |> Divide.divide(8N)
    |> check(4N)(8N / 2N)

let bigRemainder =
    3N
    |> Remainder.remainder(7N)
    |> check(1N)(7N % 3N)

let bigNegate =
    5N
    |> Negate.negate
    |> check(-5N)(-5N)

let u8Eq =
    7u8
    |> Eq.equal(7u8)
    |> check(true)(7u8 == 7u8)

let u8Ne =
    8u8
    |> Eq.notEqual(7u8)
    |> check(true)(7u8 != 8u8)

let u8Lt =
    8u8
    |> Ord.less(7u8)
    |> check(true)(7u8 < 8u8)

let u8Le =
    7u8
    |> Ord.lessOrEqual(7u8)
    |> check(true)(7u8 <= 7u8)

let u8Gt =
    7u8
    |> Ord.greater(8u8)
    |> check(true)(8u8 > 7u8)

let u8Ge =
    8u8
    |> Ord.greaterOrEqual(8u8)
    |> check(true)(8u8 >= 8u8)

let u8Add =
    2u8
    |> Add.add(7u8)
    |> check(9u8)(7u8 + 2u8)

let u8Subtract =
    2u8
    |> Subtract.subtract(7u8)
    |> check(5u8)(7u8 - 2u8)

let u8Multiply =
    2u8
    |> Multiply.multiply(6u8)
    |> check(12u8)(6u8 * 2u8)

let u8Divide =
    2u8
    |> Divide.divide(8u8)
    |> check(4u8)(8u8 / 2u8)

let u8Remainder =
    3u8
    |> Remainder.remainder(7u8)
    |> check(1u8)(7u8 % 3u8)

let u8Negate =
    5u8
    |> Negate.negate
    |> check(-5u8)(-5u8)

let u8BitAnd =
    6u8
    |> BitAnd.bitAnd(3u8)
    |> check(2u8)(3u8 & 6u8)

let u8BitOr =
    6u8
    |> BitOr.bitOr(3u8)
    |> check(7u8)(3u8 | 6u8)

let u8BitXor =
    6u8
    |> BitXor.bitXor(3u8)
    |> check(5u8)(3u8 ^ 6u8)

let u8ShiftLeft =
    4u8
    |> ShiftLeft.shiftLeft(1u8)
    |> check(16u8)(1u8 << 4u8)

let u8ShiftRight =
    1u8
    |> ShiftRight.shiftRight(8u8)
    |> check(4u8)(8u8 >> 1u8)

let u8BitwiseNot =
    5u8
    |> BitwiseNot.bitwiseNot
    |> check(~5u8)(~5u8)

let u16Eq =
    7u16
    |> Eq.equal(7u16)
    |> check(true)(7u16 == 7u16)

let u16Ne =
    8u16
    |> Eq.notEqual(7u16)
    |> check(true)(7u16 != 8u16)

let u16Lt =
    8u16
    |> Ord.less(7u16)
    |> check(true)(7u16 < 8u16)

let u16Le =
    7u16
    |> Ord.lessOrEqual(7u16)
    |> check(true)(7u16 <= 7u16)

let u16Gt =
    7u16
    |> Ord.greater(8u16)
    |> check(true)(8u16 > 7u16)

let u16Ge =
    8u16
    |> Ord.greaterOrEqual(8u16)
    |> check(true)(8u16 >= 8u16)

let u16Add =
    2u16
    |> Add.add(7u16)
    |> check(9u16)(7u16 + 2u16)

let u16Subtract =
    2u16
    |> Subtract.subtract(7u16)
    |> check(5u16)(7u16 - 2u16)

let u16Multiply =
    2u16
    |> Multiply.multiply(6u16)
    |> check(12u16)(6u16 * 2u16)

let u16Divide =
    2u16
    |> Divide.divide(8u16)
    |> check(4u16)(8u16 / 2u16)

let u16Remainder =
    3u16
    |> Remainder.remainder(7u16)
    |> check(1u16)(7u16 % 3u16)

let u16Negate =
    5u16
    |> Negate.negate
    |> check(-5u16)(-5u16)

let u16BitAnd =
    6u16
    |> BitAnd.bitAnd(3u16)
    |> check(2u16)(3u16 & 6u16)

let u16BitOr =
    6u16
    |> BitOr.bitOr(3u16)
    |> check(7u16)(3u16 | 6u16)

let u16BitXor =
    6u16
    |> BitXor.bitXor(3u16)
    |> check(5u16)(3u16 ^ 6u16)

let u16ShiftLeft =
    4u16
    |> ShiftLeft.shiftLeft(1u16)
    |> check(16u16)(1u16 << 4u16)

let u16ShiftRight =
    1u16
    |> ShiftRight.shiftRight(8u16)
    |> check(4u16)(8u16 >> 1u16)

let u16BitwiseNot =
    5u16
    |> BitwiseNot.bitwiseNot
    |> check(~5u16)(~5u16)

let u32Eq =
    7u32
    |> Eq.equal(7u32)
    |> check(true)(7u32 == 7u32)

let u32Ne =
    8u32
    |> Eq.notEqual(7u32)
    |> check(true)(7u32 != 8u32)

let u32Lt =
    8u32
    |> Ord.less(7u32)
    |> check(true)(7u32 < 8u32)

let u32Le =
    7u32
    |> Ord.lessOrEqual(7u32)
    |> check(true)(7u32 <= 7u32)

let u32Gt =
    7u32
    |> Ord.greater(8u32)
    |> check(true)(8u32 > 7u32)

let u32Ge =
    8u32
    |> Ord.greaterOrEqual(8u32)
    |> check(true)(8u32 >= 8u32)

let u32Add =
    2u32
    |> Add.add(7u32)
    |> check(9u32)(7u32 + 2u32)

let u32Subtract =
    2u32
    |> Subtract.subtract(7u32)
    |> check(5u32)(7u32 - 2u32)

let u32Multiply =
    2u32
    |> Multiply.multiply(6u32)
    |> check(12u32)(6u32 * 2u32)

let u32Divide =
    2u32
    |> Divide.divide(8u32)
    |> check(4u32)(8u32 / 2u32)

let u32Remainder =
    3u32
    |> Remainder.remainder(7u32)
    |> check(1u32)(7u32 % 3u32)

let u32Negate =
    5u32
    |> Negate.negate
    |> check(-5u32)(-5u32)

let u32BitAnd =
    6u32
    |> BitAnd.bitAnd(3u32)
    |> check(2u32)(3u32 & 6u32)

let u32BitOr =
    6u32
    |> BitOr.bitOr(3u32)
    |> check(7u32)(3u32 | 6u32)

let u32BitXor =
    6u32
    |> BitXor.bitXor(3u32)
    |> check(5u32)(3u32 ^ 6u32)

let u32ShiftLeft =
    4u32
    |> ShiftLeft.shiftLeft(1u32)
    |> check(16u32)(1u32 << 4u32)

let u32ShiftRight =
    1u32
    |> ShiftRight.shiftRight(8u32)
    |> check(4u32)(8u32 >> 1u32)

let u32BitwiseNot =
    5u32
    |> BitwiseNot.bitwiseNot
    |> check(~5u32)(~5u32)

let u64Eq =
    7u64
    |> Eq.equal(7u64)
    |> check(true)(7u64 == 7u64)

let u64Ne =
    8u64
    |> Eq.notEqual(7u64)
    |> check(true)(7u64 != 8u64)

let u64Lt =
    8u64
    |> Ord.less(7u64)
    |> check(true)(7u64 < 8u64)

let u64Le =
    7u64
    |> Ord.lessOrEqual(7u64)
    |> check(true)(7u64 <= 7u64)

let u64Gt =
    7u64
    |> Ord.greater(8u64)
    |> check(true)(8u64 > 7u64)

let u64Ge =
    8u64
    |> Ord.greaterOrEqual(8u64)
    |> check(true)(8u64 >= 8u64)

let u64Add =
    2u64
    |> Add.add(7u64)
    |> check(9u64)(7u64 + 2u64)

let u64Subtract =
    2u64
    |> Subtract.subtract(7u64)
    |> check(5u64)(7u64 - 2u64)

let u64Multiply =
    2u64
    |> Multiply.multiply(6u64)
    |> check(12u64)(6u64 * 2u64)

let u64Divide =
    2u64
    |> Divide.divide(8u64)
    |> check(4u64)(8u64 / 2u64)

let u64Remainder =
    3u64
    |> Remainder.remainder(7u64)
    |> check(1u64)(7u64 % 3u64)

let u64Negate =
    5u64
    |> Negate.negate
    |> check(-5u64)(-5u64)

let u64BitAnd =
    6u64
    |> BitAnd.bitAnd(3u64)
    |> check(2u64)(3u64 & 6u64)

let u64BitOr =
    6u64
    |> BitOr.bitOr(3u64)
    |> check(7u64)(3u64 | 6u64)

let u64BitXor =
    6u64
    |> BitXor.bitXor(3u64)
    |> check(5u64)(3u64 ^ 6u64)

let u64ShiftLeft =
    4u64
    |> ShiftLeft.shiftLeft(1u64)
    |> check(16u64)(1u64 << 4u64)

let u64ShiftRight =
    1u64
    |> ShiftRight.shiftRight(8u64)
    |> check(4u64)(8u64 >> 1u64)

let u64BitwiseNot =
    5u64
    |> BitwiseNot.bitwiseNot
    |> check(~5u64)(~5u64)

let boolEq =
    true
    |> Eq.equal(true)
    |> check(true)(true == true)

let boolNe =
    false
    |> Eq.notEqual(true)
    |> check(true)(true != false)

let boolNot =
    true
    |> Not.not
    |> check(false)(!true)

let strEq =
    "a"
    |> Eq.equal("a")
    |> check(true)("a" == "a")

let strNe =
    "b"
    |> Eq.notEqual("a")
    |> check(true)("a" != "b")

let strLt =
    "b"
    |> Ord.less("a")
    |> check(true)("a" < "b")

let strLe =
    "a"
    |> Ord.lessOrEqual("a")
    |> check(true)("a" <= "a")

let strGt =
    "a"
    |> Ord.greater("b")
    |> check(true)("b" > "a")

let strGe =
    "b"
    |> Ord.greaterOrEqual("b")
    |> check(true)("b" >= "b")

let strAdd =
    "b"
    |> Add.add("a")
    |> check("ab")("a" + "b")

let showInt =
    7
    |> Show.show
    |> assertEqual("7")

let showFloat =
    1.5
    |> Show.show
    |> assertEqual("1.5")

let showBig =
    7N
    |> Show.show
    |> assertEqual("7")

let showU8 =
    7u8
    |> Show.show
    |> assertEqual("7")

let showU16 =
    7u16
    |> Show.show
    |> assertEqual("7")

let showU32 =
    7u32
    |> Show.show
    |> assertEqual("7")

let showU64 =
    7u64
    |> Show.show
    |> assertEqual("7")

let showBool =
    true
    |> Show.show
    |> assertEqual("true")

let showStr =
    "x"
    |> Show.show
    |> assertEqual("\"x\"")

let hashInt =
    7
    |> Hash.hash
    |> assertEqual(7)

let hashFloat =
    0.0
    |> Hash.hash
    |> assertEqual(0)

let hashBig = checkHash(7N)

let hashU8 =
    7u8
    |> Hash.hash
    |> assertEqual(7)

let hashU16 =
    7u16
    |> Hash.hash
    |> assertEqual(7)

let hashU32 =
    7u32
    |> Hash.hash
    |> assertEqual(7)

let hashU64 =
    7u64
    |> Hash.hash
    |> assertEqual(7)

let hashBool =
    true
    |> Hash.hash
    |> assertEqual(1)

let hashStr = checkHash("x")

let defaultInt =
    Unit
    |> Default.default
    |> assertEqual(0)

let defaultFloat =
    Unit
    |> Default.default
    |> assertEqual(0.0)

let defaultBig =
    Unit
    |> Default.default
    |> assertEqual(0N)

let defaultU8 =
    Unit
    |> Default.default
    |> assertEqual(0u8)

let defaultU16 =
    Unit
    |> Default.default
    |> assertEqual(0u16)

let defaultU32 =
    Unit
    |> Default.default
    |> assertEqual(0u32)

let defaultU64 =
    Unit
    |> Default.default
    |> assertEqual(0u64)

let defaultBool =
    Unit
    |> Default.default
    |> assertEqual(false)

let defaultStr =
    Unit
    |> Default.default
    |> assertEqual("")

Ashes.IO.print("ok")
