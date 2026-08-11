// expect: ok
import Ashes.Test.assertEqual
import Ashes.Trait
let check : a -> a -> a -> Unit requires {Eq(a)} =
    given (expected) ->
        given (operatorValue) ->
            given (methodValue) ->
                let checkedOperator = assertEqual(expected)(operatorValue)
                in assertEqual(expected)(methodValue)

let checkHash : a -> Unit requires {Hash(a)} =
    given (value) -> assertEqual(Hash.hash(value))(Hash.hash(value))

let intEq = check(true)(7 == 7)(Eq.equal(7)(7))

let intNe = check(true)(7 != 8)(Eq.notEqual(7)(8))

let intLt = check(true)(7 < 8)(Ord.less(7)(8))

let intLe = check(true)(7 <= 7)(Ord.lessOrEqual(7)(7))

let intGt = check(true)(8 > 7)(Ord.greater(8)(7))

let intGe = check(true)(8 >= 8)(Ord.greaterOrEqual(8)(8))

let intAdd = check(9)(7 + 2)(Add.add(7)(2))

let intSubtract = check(5)(7 - 2)(Subtract.subtract(7)(2))

let intMultiply = check(12)(6 * 2)(Multiply.multiply(6)(2))

let intDivide = check(4)(8 / 2)(Divide.divide(8)(2))

let intRemainder = check(1)(7 % 3)(Remainder.remainder(7)(3))

let intNegate = check(-5)(-5)(Negate.negate(5))

let intBitAnd = check(2)(3 & 6)(BitAnd.bitAnd(3)(6))

let intBitOr = check(7)(3 | 6)(BitOr.bitOr(3)(6))

let intBitXor = check(5)(3 ^ 6)(BitXor.bitXor(3)(6))

let intShiftLeft = check(16)(1 << 4)(ShiftLeft.shiftLeft(1)(4))

let intShiftRight = check(4)(8 >> 1)(ShiftRight.shiftRight(8)(1))

let intBitwiseNot = check(-6)(~5)(BitwiseNot.bitwiseNot(5))

let floatEq = check(true)(1.5 == 1.5)(Eq.equal(1.5)(1.5))

let floatNe = check(true)(1.5 != 2.5)(Eq.notEqual(1.5)(2.5))

let floatLt = check(true)(1.5 < 2.5)(Ord.less(1.5)(2.5))

let floatLe = check(true)(1.5 <= 1.5)(Ord.lessOrEqual(1.5)(1.5))

let floatGt = check(true)(2.5 > 1.5)(Ord.greater(2.5)(1.5))

let floatGe = check(true)(2.5 >= 2.5)(Ord.greaterOrEqual(2.5)(2.5))

let floatAdd = check(3.5)(1.5 + 2.0)(Add.add(1.5)(2.0))

let floatSubtract = check(1.5)(3.5 - 2.0)(Subtract.subtract(3.5)(2.0))

let floatMultiply = check(3.0)(1.5 * 2.0)(Multiply.multiply(1.5)(2.0))

let floatDivide = check(2.0)(4.0 / 2.0)(Divide.divide(4.0)(2.0))

let floatNegate = check(-1.5)(-1.5)(Negate.negate(1.5))

let bigEq = check(true)(7N == 7N)(Eq.equal(7N)(7N))

let bigNe = check(true)(7N != 8N)(Eq.notEqual(7N)(8N))

let bigLt = check(true)(7N < 8N)(Ord.less(7N)(8N))

let bigLe = check(true)(7N <= 7N)(Ord.lessOrEqual(7N)(7N))

let bigGt = check(true)(8N > 7N)(Ord.greater(8N)(7N))

let bigGe = check(true)(8N >= 8N)(Ord.greaterOrEqual(8N)(8N))

let bigAdd = check(9N)(7N + 2N)(Add.add(7N)(2N))

let bigSubtract = check(5N)(7N - 2N)(Subtract.subtract(7N)(2N))

let bigMultiply = check(12N)(6N * 2N)(Multiply.multiply(6N)(2N))

let bigDivide = check(4N)(8N / 2N)(Divide.divide(8N)(2N))

let bigRemainder = check(1N)(7N % 3N)(Remainder.remainder(7N)(3N))

let bigNegate = check(-5N)(-5N)(Negate.negate(5N))

let u8Eq = check(true)(7u8 == 7u8)(Eq.equal(7u8)(7u8))

let u8Ne = check(true)(7u8 != 8u8)(Eq.notEqual(7u8)(8u8))

let u8Lt = check(true)(7u8 < 8u8)(Ord.less(7u8)(8u8))

let u8Le = check(true)(7u8 <= 7u8)(Ord.lessOrEqual(7u8)(7u8))

let u8Gt = check(true)(8u8 > 7u8)(Ord.greater(8u8)(7u8))

let u8Ge = check(true)(8u8 >= 8u8)(Ord.greaterOrEqual(8u8)(8u8))

let u8Add = check(9u8)(7u8 + 2u8)(Add.add(7u8)(2u8))

let u8Subtract = check(5u8)(7u8 - 2u8)(Subtract.subtract(7u8)(2u8))

let u8Multiply = check(12u8)(6u8 * 2u8)(Multiply.multiply(6u8)(2u8))

let u8Divide = check(4u8)(8u8 / 2u8)(Divide.divide(8u8)(2u8))

let u8Remainder = check(1u8)(7u8 % 3u8)(Remainder.remainder(7u8)(3u8))

let u8Negate = check(-5u8)(-5u8)(Negate.negate(5u8))

let u8BitAnd = check(2u8)(3u8 & 6u8)(BitAnd.bitAnd(3u8)(6u8))

let u8BitOr = check(7u8)(3u8 | 6u8)(BitOr.bitOr(3u8)(6u8))

let u8BitXor = check(5u8)(3u8 ^ 6u8)(BitXor.bitXor(3u8)(6u8))

let u8ShiftLeft = check(16u8)(1u8 << 4u8)(ShiftLeft.shiftLeft(1u8)(4u8))

let u8ShiftRight = check(4u8)(8u8 >> 1u8)(ShiftRight.shiftRight(8u8)(1u8))

let u8BitwiseNot = check(~5u8)(~5u8)(BitwiseNot.bitwiseNot(5u8))

let u16Eq = check(true)(7u16 == 7u16)(Eq.equal(7u16)(7u16))

let u16Ne = check(true)(7u16 != 8u16)(Eq.notEqual(7u16)(8u16))

let u16Lt = check(true)(7u16 < 8u16)(Ord.less(7u16)(8u16))

let u16Le = check(true)(7u16 <= 7u16)(Ord.lessOrEqual(7u16)(7u16))

let u16Gt = check(true)(8u16 > 7u16)(Ord.greater(8u16)(7u16))

let u16Ge = check(true)(8u16 >= 8u16)(Ord.greaterOrEqual(8u16)(8u16))

let u16Add = check(9u16)(7u16 + 2u16)(Add.add(7u16)(2u16))

let u16Subtract = check(5u16)(7u16 - 2u16)(Subtract.subtract(7u16)(2u16))

let u16Multiply = check(12u16)(6u16 * 2u16)(Multiply.multiply(6u16)(2u16))

let u16Divide = check(4u16)(8u16 / 2u16)(Divide.divide(8u16)(2u16))

let u16Remainder = check(1u16)(7u16 % 3u16)(Remainder.remainder(7u16)(3u16))

let u16Negate = check(-5u16)(-5u16)(Negate.negate(5u16))

let u16BitAnd = check(2u16)(3u16 & 6u16)(BitAnd.bitAnd(3u16)(6u16))

let u16BitOr = check(7u16)(3u16 | 6u16)(BitOr.bitOr(3u16)(6u16))

let u16BitXor = check(5u16)(3u16 ^ 6u16)(BitXor.bitXor(3u16)(6u16))

let u16ShiftLeft = check(16u16)(1u16 << 4u16)(ShiftLeft.shiftLeft(1u16)(4u16))

let u16ShiftRight = check(4u16)(8u16 >> 1u16)(ShiftRight.shiftRight(8u16)(1u16))

let u16BitwiseNot = check(~5u16)(~5u16)(BitwiseNot.bitwiseNot(5u16))

let u32Eq = check(true)(7u32 == 7u32)(Eq.equal(7u32)(7u32))

let u32Ne = check(true)(7u32 != 8u32)(Eq.notEqual(7u32)(8u32))

let u32Lt = check(true)(7u32 < 8u32)(Ord.less(7u32)(8u32))

let u32Le = check(true)(7u32 <= 7u32)(Ord.lessOrEqual(7u32)(7u32))

let u32Gt = check(true)(8u32 > 7u32)(Ord.greater(8u32)(7u32))

let u32Ge = check(true)(8u32 >= 8u32)(Ord.greaterOrEqual(8u32)(8u32))

let u32Add = check(9u32)(7u32 + 2u32)(Add.add(7u32)(2u32))

let u32Subtract = check(5u32)(7u32 - 2u32)(Subtract.subtract(7u32)(2u32))

let u32Multiply = check(12u32)(6u32 * 2u32)(Multiply.multiply(6u32)(2u32))

let u32Divide = check(4u32)(8u32 / 2u32)(Divide.divide(8u32)(2u32))

let u32Remainder = check(1u32)(7u32 % 3u32)(Remainder.remainder(7u32)(3u32))

let u32Negate = check(-5u32)(-5u32)(Negate.negate(5u32))

let u32BitAnd = check(2u32)(3u32 & 6u32)(BitAnd.bitAnd(3u32)(6u32))

let u32BitOr = check(7u32)(3u32 | 6u32)(BitOr.bitOr(3u32)(6u32))

let u32BitXor = check(5u32)(3u32 ^ 6u32)(BitXor.bitXor(3u32)(6u32))

let u32ShiftLeft = check(16u32)(1u32 << 4u32)(ShiftLeft.shiftLeft(1u32)(4u32))

let u32ShiftRight = check(4u32)(8u32 >> 1u32)(ShiftRight.shiftRight(8u32)(1u32))

let u32BitwiseNot = check(~5u32)(~5u32)(BitwiseNot.bitwiseNot(5u32))

let u64Eq = check(true)(7u64 == 7u64)(Eq.equal(7u64)(7u64))

let u64Ne = check(true)(7u64 != 8u64)(Eq.notEqual(7u64)(8u64))

let u64Lt = check(true)(7u64 < 8u64)(Ord.less(7u64)(8u64))

let u64Le = check(true)(7u64 <= 7u64)(Ord.lessOrEqual(7u64)(7u64))

let u64Gt = check(true)(8u64 > 7u64)(Ord.greater(8u64)(7u64))

let u64Ge = check(true)(8u64 >= 8u64)(Ord.greaterOrEqual(8u64)(8u64))

let u64Add = check(9u64)(7u64 + 2u64)(Add.add(7u64)(2u64))

let u64Subtract = check(5u64)(7u64 - 2u64)(Subtract.subtract(7u64)(2u64))

let u64Multiply = check(12u64)(6u64 * 2u64)(Multiply.multiply(6u64)(2u64))

let u64Divide = check(4u64)(8u64 / 2u64)(Divide.divide(8u64)(2u64))

let u64Remainder = check(1u64)(7u64 % 3u64)(Remainder.remainder(7u64)(3u64))

let u64Negate = check(-5u64)(-5u64)(Negate.negate(5u64))

let u64BitAnd = check(2u64)(3u64 & 6u64)(BitAnd.bitAnd(3u64)(6u64))

let u64BitOr = check(7u64)(3u64 | 6u64)(BitOr.bitOr(3u64)(6u64))

let u64BitXor = check(5u64)(3u64 ^ 6u64)(BitXor.bitXor(3u64)(6u64))

let u64ShiftLeft = check(16u64)(1u64 << 4u64)(ShiftLeft.shiftLeft(1u64)(4u64))

let u64ShiftRight = check(4u64)(8u64 >> 1u64)(ShiftRight.shiftRight(8u64)(1u64))

let u64BitwiseNot = check(~5u64)(~5u64)(BitwiseNot.bitwiseNot(5u64))

let boolEq = check(true)(true == true)(Eq.equal(true)(true))

let boolNe = check(true)(true != false)(Eq.notEqual(true)(false))

let boolNot = check(false)(!true)(Not.not(true))

let strEq = check(true)("a" == "a")(Eq.equal("a")("a"))

let strNe = check(true)("a" != "b")(Eq.notEqual("a")("b"))

let strLt = check(true)("a" < "b")(Ord.less("a")("b"))

let strLe = check(true)("a" <= "a")(Ord.lessOrEqual("a")("a"))

let strGt = check(true)("b" > "a")(Ord.greater("b")("a"))

let strGe = check(true)("b" >= "b")(Ord.greaterOrEqual("b")("b"))

let strAdd = check("ab")("a" + "b")(Add.add("a")("b"))

let showInt = assertEqual("7")(Show.show(7))

let showFloat = assertEqual("1.5")(Show.show(1.5))

let showBig = assertEqual("7")(Show.show(7N))

let showU8 = assertEqual("7")(Show.show(7u8))

let showU16 = assertEqual("7")(Show.show(7u16))

let showU32 = assertEqual("7")(Show.show(7u32))

let showU64 = assertEqual("7")(Show.show(7u64))

let showBool = assertEqual("true")(Show.show(true))

let showStr = assertEqual("\"x\"")(Show.show("x"))

let hashInt = assertEqual(7)(Hash.hash(7))

let hashFloat = assertEqual(0)(Hash.hash(0.0))

let hashBig = checkHash(7N)

let hashU8 = assertEqual(7)(Hash.hash(7u8))

let hashU16 = assertEqual(7)(Hash.hash(7u16))

let hashU32 = assertEqual(7)(Hash.hash(7u32))

let hashU64 = assertEqual(7)(Hash.hash(7u64))

let hashBool = assertEqual(1)(Hash.hash(true))

let hashStr = checkHash("x")

let defaultInt = assertEqual(0)(Default.default(Unit))

let defaultFloat = assertEqual(0.0)(Default.default(Unit))

let defaultBig = assertEqual(0N)(Default.default(Unit))

let defaultU8 = assertEqual(0u8)(Default.default(Unit))

let defaultU16 = assertEqual(0u16)(Default.default(Unit))

let defaultU32 = assertEqual(0u32)(Default.default(Unit))

let defaultU64 = assertEqual(0u64)(Default.default(Unit))

let defaultBool = assertEqual(false)(Default.default(Unit))

let defaultStr = assertEqual("")(Default.default(Unit))

Ashes.IO.print("ok")
