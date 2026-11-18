let fromRunes runes =
    (let recursive go remaining output =
        match remaining with
            | [] -> output
            | head :: tail -> go(tail)(output + Ashes.Rune.toText(head))
    in go(runes)(""))

let recursive countCodepoints bytes i limit acc =
    if i >= limit
    then acc
    else
        let b =
            i
            |> Ashes.Byte.get(bytes)
            |> Ashes.Number.UInt.toInt
        in
            let isStart =
                if b < 128
                then true
                else b >= 192
            in
                countCodepoints(bytes)(i + 1)(limit)(if isStart
                then acc + 1
                else acc)

let length text =
    (let bytes = Ashes.Byte.fromText(text)
    in
        countCodepoints(bytes)(0)(Ashes.Byte.length(bytes))(0))

let recursive cpByteOffset bytes i cpSeen targetCp limit =
    if i >= limit
    then limit
    else
        let b =
            i
            |> Ashes.Byte.get(bytes)
            |> Ashes.Number.UInt.toInt
        in
            let isStart =
                if b < 128
                then true
                else b >= 192
            in
                if isStart
                then
                    if cpSeen == targetCp
                    then i
                    else cpByteOffset(bytes)(i + 1)(cpSeen + 1)(targetCp)(limit)
                else cpByteOffset(bytes)(i + 1)(cpSeen)(targetCp)(limit)

let substring text start count =
    if start <= -1
    then ""
    else
        if count <= 0
        then ""
        else
            let bytes = Ashes.Byte.fromText(text)
            in
                let limit = Ashes.Byte.length(bytes)
                in
                    let byteStart = cpByteOffset(bytes)(0)(0)(start)(limit)
                    in
                        let byteEnd = cpByteOffset(bytes)(0)(0)(start + count)(limit)
                        in Ashes.Byte.subText(bytes)(byteStart)(byteEnd - byteStart)

let drop text count =
    if count <= 0
    then text
    else
        let bytes = Ashes.Byte.fromText(text)
        in
            let limit = Ashes.Byte.length(bytes)
            in
                let byteStart = cpByteOffset(bytes)(0)(0)(count)(limit)
                in Ashes.Byte.subText(bytes)(byteStart)(limit - byteStart)

let take text count =
    if count <= 0
    then ""
    else substring(text)(0)(count)

let recursive byteFind tb needle from tlen nlen firstByte =
    (let idx = Ashes.Byte.indexOf(tb)(firstByte)(from)
    in
        if idx < 0
        then -1
        else
            if idx + nlen > tlen
            then -1
            else
                if Ashes.Byte.subView(tb)(idx)(nlen) == needle
                then idx
                else byteFind(tb)(needle)(idx + 1)(tlen)(nlen)(firstByte))

let firstByteOf text =
    0
    |> Ashes.Byte.get(Ashes.Byte.fromText(text))
    |> Ashes.Number.UInt.toInt

let startsWith text prefix =
    (let nlen = Ashes.Text.byteLength(prefix)
    in
        if nlen == 0
        then true
        else
            let tb = Ashes.Byte.fromText(text)
            in
                if nlen > Ashes.Byte.length(tb)
                then false
                else Ashes.Byte.subView(tb)(0)(nlen) == prefix)

let indexOf text needle =
    if needle == ""
    then 0
    else
        let tb = Ashes.Byte.fromText(text)
        in
            let bytePos =
                needle
                |> firstByteOf
                |> byteFind(tb)(needle)(0)(Ashes.Byte.length(tb))(Ashes.Text.byteLength(needle))
            in
                if bytePos < 0
                then -1
                else countCodepoints(tb)(0)(bytePos)(0)

let contains text needle =
    if needle == ""
    then true
    else
        let tb = Ashes.Byte.fromText(text)
        in
            byteFind(tb)(needle)(0)(Ashes.Byte.length(tb))(Ashes.Text.byteLength(needle))(firstByteOf(needle)) >= 0

let recursive reverseTexts remaining acc =
    match remaining with
        | [] -> acc
        | head :: tail -> reverseTexts(tail)(head :: acc)

let recursive piecesBetween tb separator from tlen slen sfirst acc =
    (let idx = byteFind(tb)(separator)(from)(tlen)(slen)(sfirst)
    in
        if idx < 0
        then reverseTexts(Ashes.Byte.subText(tb)(from)(tlen - from) :: acc)([])
        else piecesBetween(tb)(separator)(idx + slen)(tlen)(slen)(sfirst)(Ashes.Byte.subText(tb)(from)(idx - from) :: acc))

let split text separator =
    if separator == ""
    then [text]
    else
        let tb = Ashes.Byte.fromText(text)
        in
            piecesBetween(tb)(separator)(0)(Ashes.Byte.length(tb))(Ashes.Text.byteLength(separator))(firstByteOf(separator))([])

let isDigitText text =
    match text with
        | "0" -> true
        | "1" -> true
        | "2" -> true
        | "3" -> true
        | "4" -> true
        | "5" -> true
        | "6" -> true
        | "7" -> true
        | "8" -> true
        | "9" -> true
        | _ -> false

let isLetterText text =
    match text with
        | "a" -> true
        | "b" -> true
        | "c" -> true
        | "d" -> true
        | "e" -> true
        | "f" -> true
        | "g" -> true
        | "h" -> true
        | "i" -> true
        | "j" -> true
        | "k" -> true
        | "l" -> true
        | "m" -> true
        | "n" -> true
        | "o" -> true
        | "p" -> true
        | "q" -> true
        | "r" -> true
        | "s" -> true
        | "t" -> true
        | "u" -> true
        | "v" -> true
        | "w" -> true
        | "x" -> true
        | "y" -> true
        | "z" -> true
        | "A" -> true
        | "B" -> true
        | "C" -> true
        | "D" -> true
        | "E" -> true
        | "F" -> true
        | "G" -> true
        | "H" -> true
        | "I" -> true
        | "J" -> true
        | "K" -> true
        | "L" -> true
        | "M" -> true
        | "N" -> true
        | "O" -> true
        | "P" -> true
        | "Q" -> true
        | "R" -> true
        | "S" -> true
        | "T" -> true
        | "U" -> true
        | "V" -> true
        | "W" -> true
        | "X" -> true
        | "Y" -> true
        | "Z" -> true
        | _ -> false

let isWhiteSpaceText text =
    match text with
        | " " -> true
        | "\t" -> true
        | "\n" -> true
        | "\r" -> true
        | _ -> false

let isWhiteSpaceByte b =
    if b == 32
    then true
    else
        if b == 9
        then true
        else
            if b == 10
            then true
            else b == 13

let recursive dropLeadingWhiteSpace tb n i =
    if i >= n
    then ""
    else
        if i
        |> Ashes.Byte.get(tb)
        |> Ashes.Number.UInt.toInt
        |> isWhiteSpaceByte
        then dropLeadingWhiteSpace(tb)(n)(i + 1)
        else Ashes.Byte.subText(tb)(i)(n - i)

let trimStart text =
    (let tb = Ashes.Byte.fromText(text)
    in
        dropLeadingWhiteSpace(tb)(Ashes.Byte.length(tb))(0))

let recursive lastAndInit text =
    match Ashes.Text.unconsText(text) with
        | None -> None
        | Some((head, tail)) ->
            match Ashes.Text.unconsText(tail) with
                | None -> Some(("", head))
                | Some((_tailHead, _tailRest)) ->
                    match lastAndInit(tail) with
                        | None -> None
                        | Some((init, last)) -> Some((head + init, last))

let recursive dropTrailingWhiteSpace tb j =
    if j <= 0
    then ""
    else
        if j - 1
        |> Ashes.Byte.get(tb)
        |> Ashes.Number.UInt.toInt
        |> isWhiteSpaceByte
        then dropTrailingWhiteSpace(tb)(j - 1)
        else Ashes.Byte.subText(tb)(0)(j)

let trimEnd text =
    (let tb = Ashes.Byte.fromText(text)
    in
        tb
        |> Ashes.Byte.length
        |> dropTrailingWhiteSpace(tb))

let trim text =
    text
    |> trimStart
    |> trimEnd

let compare left right =
    right
    |> Ashes.Byte.fromText
    |> Ashes.Byte.compare(Ashes.Byte.fromText(left))

let join (separator: Str) (parts: List(Str)) =
    (let recursive rev (acc: List(Str)) (xs: List(Str)) =
        match xs with
            | [] -> acc
            | head :: tail -> rev(head :: acc)(tail)
    in
        let recursive interleaveGo (acc: List(Str)) (ps: List(Str)) =
            match ps with
                | [] -> rev([])(acc)
                | first :: rest ->
                    match acc with
                        | [] -> interleaveGo(first :: [])(rest)
                        | _ -> interleaveGo(first :: separator :: acc)(rest)
        in
            let recursive pairwiseGo (acc: List(Str)) (ps: List(Str)) =
                match ps with
                    | [] -> rev([])(acc)
                    | first :: rest ->
                        match rest with
                            | [] -> rev([])(first :: acc)
                            | second :: more ->
                                let merged = first + second
                                in pairwiseGo(merged :: acc)(more)
            in
                let recursive reduce (ps: List(Str)) =
                    match ps with
                        | [] -> ""
                        | only :: rest ->
                            match rest with
                                | [] -> only
                                | _ ->
                                    ps
                                    |> pairwiseGo([])
                                    |> reduce
                in
                    parts
                    |> interleaveGo([])
                    |> reduce)

// parseFloat returns the Float nearest to the decimal text, ties to even. Up to 18 digits with a
// decimal exponent within 22 of zero and a mantissa within 2^53 take one exact Float multiply or
// divide; every other input is rounded exactly with BigInt arithmetic.
let parseFloatInvalidMessage = "Ashes.Text.parseFloat() invalid input"

let parseFloatRangeMessage = "Ashes.Text.parseFloat() out of range"

let parseFloatByteAt (bytes: Bytes) (position: Int) =
    position
    |> Ashes.Byte.get(bytes)
    |> Ashes.Number.UInt.toInt

let parseFloatByteIs (bytes: Bytes) (count: Int) (position: Int) (expected: Int) =
    if position >= count
    then false
    else parseFloatByteAt(bytes)(position) == expected

let recursive parseFloatDigitsEnd (bytes: Bytes) (count: Int) (position: Int) =
    if position >= count
    then position
    else
        let code = parseFloatByteAt(bytes)(position)
        in
            if code >= 48 && code <= 57
            then parseFloatDigitsEnd(bytes)(count)(position + 1)
            else position

let recursive parseFloatMantissa (bytes: Bytes) (position: Int) (stop: Int) (value: BigInt) =
    if position >= stop
    then value
    else parseFloatMantissa(bytes)(position + 1)(stop)(value * 10N + Ashes.Number.BigInt.fromInt(parseFloatByteAt(bytes)(position) - 48))

let recursive parseFloatExponentDigits (bytes: Bytes) (position: Int) (stop: Int) (value: Int) =
    if position >= stop
    then value
    else
        let next = value * 10 + parseFloatByteAt(bytes)(position) - 48
        in
            parseFloatExponentDigits(bytes)(position + 1)(stop)(if next > 100000000
            then 100000000
            else next)

let recursive parseFloatBigPower (base: BigInt) (exponent: Int) =
    if exponent <= 0
    then 1N
    else
        let half = parseFloatBigPower(base)(exponent / 2)
        in
            if exponent % 2 == 0
            then half * half
            else half * half * base

let recursive parseFloatPowerOfTen (exponent: Int) (value: Float) =
    if exponent <= 0
    then value
    else parseFloatPowerOfTen(exponent - 1)(value * 10.0)

let recursive parseFloatPowerOfTwo (exponent: Int) (value: Float) =
    if exponent > 0
    then parseFloatPowerOfTwo(exponent - 1)(value * 2.0)
    else
        if exponent < 0
        then parseFloatPowerOfTwo(exponent + 1)(value * 0.5)
        else value

let parseFloatBigToFloat (value: BigInt) =
    match Ashes.Number.BigInt.toInt(value) with
        | Ok(result) -> Ashes.Number.Math.toFloat(result)
        | Error(_) -> 0.0

let parseFloatShiftedQuotient (numerator: BigInt) (denominator: BigInt) (shift: Int) =
    if shift >= 0
    then numerator * parseFloatBigPower(2N)(shift) / denominator
    else numerator / (denominator * parseFloatBigPower(2N)(-shift))

let parseFloatShiftedRemainder (numerator: BigInt) (denominator: BigInt) (shift: Int) =
    if shift >= 0
    then numerator * parseFloatBigPower(2N)(shift) % denominator
    else numerator % (denominator * parseFloatBigPower(2N)(-shift))

let recursive parseFloatFindShift (numerator: BigInt) (denominator: BigInt) (shift: Int) =
    (let quotient = parseFloatShiftedQuotient(numerator)(denominator)(shift)
    in
        if quotient < 9007199254740992N
        then parseFloatFindShift(numerator)(denominator)(shift + 1)
        else
            if quotient >= 18014398509481984N
            then parseFloatFindShift(numerator)(denominator)(shift - 1)
            else shift)

let recursive parseFloatDigitCount (value: BigInt) (count: Int) =
    if value >= 10000000000000000N
    then parseFloatDigitCount(value / 10000000000000000N)(count + 16)
    else
        if value >= 10N
        then parseFloatDigitCount(value / 10N)(count + 1)
        else count + 1

let parseFloatRounded (numerator: BigInt) (denominator: BigInt) (estimatedBits: Int) =
    (let normalShift = parseFloatFindShift(numerator)(denominator)(54 - estimatedBits)
    in
        let shift =
            if normalShift > 1075
            then 1075
            else normalShift
        in
            let quotient = parseFloatShiftedQuotient(numerator)(denominator)(shift)
            in
                let inexact = parseFloatShiftedRemainder(numerator)(denominator)(shift) != 0N
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
                                    if exponent > 971 || exponent == 971 && significand == 9007199254740992N
                                    then Error(parseFloatRangeMessage)
                                    else Ok(parseFloatBigToFloat(significand) * parseFloatPowerOfTwo(exponent)(1.0)))

let recursive parseFloatSmallMantissa (bytes: Bytes) (position: Int) (stop: Int) (value: Int) =
    if position >= stop
    then value
    else parseFloatSmallMantissa(bytes)(position + 1)(stop)(value * 10 + parseFloatByteAt(bytes)(position) - 48)

let parseFloatExact (mantissa: BigInt) (decimalExponent: Int) =
    (let digitCount = parseFloatDigitCount(mantissa)(0)
    in
        if digitCount + decimalExponent <= -324
        then Ok(0.0)
        else
            if digitCount - 1 + decimalExponent >= 309
            then Error(parseFloatRangeMessage)
            else
                let estimatedBits = (digitCount + decimalExponent) * 3322 / 1000
                in
                    if decimalExponent >= 0
                    then parseFloatRounded(mantissa * parseFloatBigPower(10N)(decimalExponent))(1N)(estimatedBits)
                    else
                        parseFloatRounded(mantissa)(parseFloatBigPower(10N)(-decimalExponent))(estimatedBits))

let parseFloatFromSmall (mantissa: Int) (decimalExponent: Int) =
    if mantissa == 0
    then Ok(0.0)
    else
        if mantissa <= 9007199254740992 && decimalExponent >= -22 && decimalExponent <= 22
        then
            if decimalExponent >= 0
            then Ok(Ashes.Number.Math.toFloat(mantissa) * parseFloatPowerOfTen(decimalExponent)(1.0))
            else Ok(Ashes.Number.Math.toFloat(mantissa) / parseFloatPowerOfTen(-decimalExponent)(1.0))
        else
            parseFloatExact(Ashes.Number.BigInt.fromInt(mantissa))(decimalExponent)

let parseFloatFromLarge (mantissa: BigInt) (decimalExponent: Int) =
    match Ashes.Number.BigInt.toInt(mantissa) with
        | Ok(small) -> parseFloatFromSmall(small)(decimalExponent)
        | Error(_) -> parseFloatExact(mantissa)(decimalExponent)

let parseFloatMagnitude (bytes: Bytes) (integerStart: Int) (integerEnd: Int) (fractionStart: Int) (fractionEnd: Int) (decimalExponent: Int) =
    if integerEnd - integerStart + fractionEnd - fractionStart <= 18
    then
        let integerPart = parseFloatSmallMantissa(bytes)(integerStart)(integerEnd)(0)
        in
            parseFloatFromSmall(parseFloatSmallMantissa(bytes)(fractionStart)(fractionEnd)(integerPart))(decimalExponent)
    else
        let integerPart = parseFloatMantissa(bytes)(integerStart)(integerEnd)(0N)
        in
            parseFloatFromLarge(parseFloatMantissa(bytes)(fractionStart)(fractionEnd)(integerPart))(decimalExponent)

let parseFloatSigned (negative: Bool) (magnitude: Result(Str, Float)) =
    match magnitude with
        | Ok(value) ->
            if negative
            then Ok(value * (0.0 - 1.0))
            else Ok(value)
        | Error(message) -> Error(message)

let parseFloatWithExponent (bytes: Bytes) (count: Int) (negative: Bool) (integerStart: Int) (integerEnd: Int) (fractionStart: Int) (fractionEnd: Int) =
    (let fractionDigits = fractionEnd - fractionStart
    in
        if fractionEnd >= count
        then
            -fractionDigits
            |> parseFloatMagnitude(bytes)(integerStart)(integerEnd)(fractionStart)(fractionEnd)
            |> parseFloatSigned(negative)
        else
            if parseFloatByteIs(bytes)(count)(fractionEnd)(101) || parseFloatByteIs(bytes)(count)(fractionEnd)(69)
            then
                let exponentNegative = parseFloatByteIs(bytes)(count)(fractionEnd + 1)(45)
                in
                    let digitsStart =
                        if exponentNegative || parseFloatByteIs(bytes)(count)(fractionEnd + 1)(43)
                        then fractionEnd + 2
                        else fractionEnd + 1
                    in
                        let digitsEnd = parseFloatDigitsEnd(bytes)(count)(digitsStart)
                        in
                            if digitsEnd == digitsStart || digitsEnd != count
                            then Error(parseFloatInvalidMessage)
                            else
                                let exponent = parseFloatExponentDigits(bytes)(digitsStart)(digitsEnd)(0)
                                in
                                    let signedExponent =
                                        if exponentNegative
                                        then -exponent
                                        else exponent
                                    in
                                        signedExponent - fractionDigits
                                        |> parseFloatMagnitude(bytes)(integerStart)(integerEnd)(fractionStart)(fractionEnd)
                                        |> parseFloatSigned(negative)
            else Error(parseFloatInvalidMessage))

let parseFloat (text: Str) =
    (let bytes = Ashes.Byte.fromText(text)
    in
        let count = Ashes.Byte.length(bytes)
        in
            let negative = parseFloatByteIs(bytes)(count)(0)(45)
            in
                let integerStart =
                    if negative
                    then 1
                    else 0
                in
                    let integerEnd = parseFloatDigitsEnd(bytes)(count)(integerStart)
                    in
                        if integerEnd == integerStart
                        then Error(parseFloatInvalidMessage)
                        else
                            if parseFloatByteIs(bytes)(count)(integerEnd)(46)
                            then
                                let fractionEnd = parseFloatDigitsEnd(bytes)(count)(integerEnd + 1)
                                in
                                    if fractionEnd == integerEnd + 1
                                    then Error(parseFloatInvalidMessage)
                                    else parseFloatWithExponent(bytes)(count)(negative)(integerStart)(integerEnd)(integerEnd + 1)(fractionEnd)
                            else parseFloatWithExponent(bytes)(count)(negative)(integerStart)(integerEnd)(integerEnd)(integerEnd))
