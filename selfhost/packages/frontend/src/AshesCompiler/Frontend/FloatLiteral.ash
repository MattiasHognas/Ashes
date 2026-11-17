// Converts a decimal float literal spelling to the nearest Float.
//
// Invariants:
// - The spelling is ASCII digits, one dot, and ASCII digits, as the lexer accepts it.
// - The result is correctly rounded (ties to even), so every literal decodes to the same Float on
//   every compiler stage, independent of how Ashes.Text.parseFloat accumulates digits.
// - A value above the Float range is positive infinity; a value below it rounds to a subnormal or 0.0.
// - Literals whose digits fit 2^53 with at most 22 fraction digits use one exact division; all others
//   use exact BigInt arithmetic.
export (
    value floatLiteralValue,
)

let floatLiteralByteAt (bytes: Bytes) (position: Int) =
    position
    |> Ashes.Byte.get(bytes)
    |> Ashes.Number.UInt.toInt

let floatLiteralIsDigit (code: Int) = code >= 48 && code <= 57

let recursive floatLiteralDotIndex (bytes: Bytes) (count: Int) (position: Int) =
    if position >= count
    then -1
    else
        if floatLiteralByteAt(bytes)(position) == 46
        then position
        else floatLiteralDotIndex(bytes)(count)(position + 1)

let recursive floatLiteralDigitsBetween (bytes: Bytes) (position: Int) (stop: Int) =
    if position >= stop
    then true
    else
        if position
        |> floatLiteralByteAt(bytes)
        |> floatLiteralIsDigit
        then floatLiteralDigitsBetween(bytes)(position + 1)(stop)
        else false

let recursive floatLiteralMantissa (bytes: Bytes) (count: Int) (position: Int) (value: BigInt) =
    if position >= count
    then value
    else
        let code = floatLiteralByteAt(bytes)(position)
        in
            if code == 46
            then floatLiteralMantissa(bytes)(count)(position + 1)(value)
            else floatLiteralMantissa(bytes)(count)(position + 1)(value * 10N + Ashes.Number.BigInt.fromInt(code - 48))

let recursive floatLiteralBigPower (base: BigInt) (exponent: Int) =
    if exponent <= 0
    then 1N
    else
        let half = floatLiteralBigPower(base)(exponent / 2)
        in
            if exponent % 2 == 0
            then half * half
            else half * half * base

let recursive floatLiteralPowerOfTen (exponent: Int) (value: Float) =
    if exponent <= 0
    then value
    else floatLiteralPowerOfTen(exponent - 1)(value * 10.0)

let recursive floatLiteralPowerOfTwo (exponent: Int) (value: Float) =
    if exponent > 0
    then floatLiteralPowerOfTwo(exponent - 1)(value * 2.0)
    else
        if exponent < 0
        then floatLiteralPowerOfTwo(exponent + 1)(value * 0.5)
        else value

let floatLiteralBigToInt (value: BigInt) =
    match Ashes.Number.BigInt.toInt(value) with
        | Ok(result) -> result
        | Error(_) -> 0

let floatLiteralShiftedQuotient (mantissa: BigInt) (denominator: BigInt) (shift: Int) =
    if shift >= 0
    then mantissa * floatLiteralBigPower(2N)(shift) / denominator
    else mantissa / (denominator * floatLiteralBigPower(2N)(-shift))

let floatLiteralShiftedRemainder (mantissa: BigInt) (denominator: BigInt) (shift: Int) =
    if shift >= 0
    then mantissa * floatLiteralBigPower(2N)(shift) % denominator
    else mantissa % (denominator * floatLiteralBigPower(2N)(-shift))

let recursive floatLiteralFindShift (mantissa: BigInt) (denominator: BigInt) (shift: Int) =
    (let quotient = floatLiteralShiftedQuotient(mantissa)(denominator)(shift)
    in
        if quotient < 9007199254740992N
        then floatLiteralFindShift(mantissa)(denominator)(shift + 1)
        else
            if quotient >= 18014398509481984N
            then floatLiteralFindShift(mantissa)(denominator)(shift - 1)
            else shift)

let floatLiteralDigitCount (value: BigInt) =
    value
    |> Ashes.Text.fromBigInt
    |> Ashes.Byte.fromText
    |> Ashes.Byte.length

let floatLiteralRounded (mantissa: BigInt) (fractionDigits: Int) =
    (let denominator = floatLiteralBigPower(10N)(fractionDigits)
    in
        let estimatedBits = (floatLiteralDigitCount(mantissa) - fractionDigits) * 3322 / 1000
        in
            let normalShift = floatLiteralFindShift(mantissa)(denominator)(54 - estimatedBits)
            in
                let shift =
                    if normalShift > 1075
                    then 1075
                    else normalShift
                in
                    let quotient = floatLiteralShiftedQuotient(mantissa)(denominator)(shift)
                    in
                        let inexact = floatLiteralShiftedRemainder(mantissa)(denominator)(shift) != 0N
                        in
                            let half = quotient / 2N
                            in
                                let roundUp = quotient % 2N == 1N && (inexact || half % 2N == 1N)
                                in
                                    let significand =
                                        if roundUp
                                        then half + 1N
                                        else half
                                    in
                                        let exponent = 1 - shift
                                        in
                                            let scaled =
                                                significand
                                                |> floatLiteralBigToInt
                                                |> Ashes.Number.Math.toFloat
                                            in
                                                if exponent > 971
                                                then scaled * floatLiteralPowerOfTwo(971)(1.0) * floatLiteralPowerOfTwo(exponent - 971)(1.0)
                                                else scaled * floatLiteralPowerOfTwo(exponent)(1.0))

let floatLiteralFromParts (mantissa: BigInt) (fractionDigits: Int) =
    if mantissa == 0N
    then 0.0
    else
        if mantissa <= 9007199254740992N && fractionDigits <= 22
        then
            Ashes.Number.Math.toFloat(floatLiteralBigToInt(mantissa)) / floatLiteralPowerOfTen(fractionDigits)(1.0)
        else floatLiteralRounded(mantissa)(fractionDigits)

let floatLiteralValue (text: Str) =
    (let bytes = Ashes.Byte.fromText(text)
    in
        let count = Ashes.Byte.length(bytes)
        in
            let dot = floatLiteralDotIndex(bytes)(count)(0)
            in
                if dot < 1 || dot >= count - 1
                then Error("Invalid float literal: " + text + ".")
                else
                    if floatLiteralDigitsBetween(bytes)(0)(dot) && floatLiteralDigitsBetween(bytes)(dot + 1)(count)
                    then
                        count - dot - 1
                        |> floatLiteralFromParts(floatLiteralMantissa(bytes)(count)(0)(0N))
                        |> Ok
                    else Error("Invalid float literal: " + text + "."))
