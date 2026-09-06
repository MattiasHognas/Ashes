// The float-to-text emitters for `AshesCompiler.Backend.IrCodegen`: `Text.fromFloat` (six
// decimals, trailing zeros trimmed down to one) and `Text.formatFloat` (a fixed decimal count,
// clamped to 0..18), stage 0's `EmitFloatToDecimalString` family. The text is written forward
// into one 64-byte stack buffer (the sign, the integer digits, a dot and the fraction digits,
// and the `e+N` exponent of a magnitude past the signed 64-bit range) and handed as one string
// to the instruction's placement. Depends only on the LLVM bindings.

import AshesCompiler.Backend.Llvm
export (
    value emitTextFromFloat,
    value emitTextFormatFloat,
)

let floatTextBufferBytes = 64u64

let floatTextConst i64 (value: u64) = constInt(i64)(value)(false)

// The address of the buffer's byte at `index`.
let floatTextBytePtr builder i64 bufferType buffer index name = buildGEP(builder)(bufferType)(buffer)([floatTextConst(i64)(0u64), index])(2u32)(name)

// Stores the byte `value` (an `i64`) at the cursor and advances the cursor by one.
let emitFloatTextAppendByte builder i64 i8 bufferType buffer cursorSlot value name =
    (let cursor = buildLoad(builder)(i64)(cursorSlot)(name + "_cursor")
    in
        Unit
        |> (given (_) ->
            name + "_ptr"
            |> floatTextBytePtr(builder)(i64)(bufferType)(buffer)(cursor)
            |> buildStore(builder)(buildTrunc(builder)(value)(i8)(name + "_byte")))
        |> (given (_) ->
            buildStore(builder)(buildAdd(builder)(cursor)(floatTextConst(i64)(1u64))(name + "_next"))(cursorSlot)))

let emitFloatTextAppendAscii builder i64 i8 bufferType buffer cursorSlot (code: u64) name =
    emitFloatTextAppendByte(builder)(i64)(i8)(bufferType)(buffer)(cursorSlot)(floatTextConst(i64)(code))(name)

// The number of decimal digits of the non-negative `value`, at least one.
let emitFloatTextDigitCount context function_ builder i64 value name =
    (let workSlot = buildEntryAlloca(builder)(i64)(name + "_work")
    in
        let countSlot = buildEntryAlloca(builder)(i64)(name + "_count")
        in
            let bodyBlock = appendBasicBlock(context)(function_)(name + "_body")
            in
                let doneBlock = appendBasicBlock(context)(function_)(name + "_done")
                in
                    Unit
                    |> (given (_) -> buildStore(builder)(value)(workSlot))
                    |> (given (_) ->
                        buildStore(builder)(floatTextConst(i64)(0u64))(countSlot))
                    |> (given (_) -> buildBr(builder)(bodyBlock))
                    |> (given (_) -> positionBuilderAtEnd(builder)(bodyBlock))
                    |> (given (_) -> buildLoad(builder)(i64)(countSlot)(name + "_so_far"))
                    |> (given (count) ->
                        buildStore(builder)(buildAdd(builder)(count)(floatTextConst(i64)(1u64))(name + "_next"))(countSlot))
                    |> (given (_) ->
                        buildUDiv(builder)(buildLoad(builder)(i64)(workSlot)(name + "_value"))(floatTextConst(i64)(10u64))(name + "_rest"))
                    |> (given (rest) ->
                        Unit
                        |> (given (_) -> buildStore(builder)(rest)(workSlot))
                        |> (given (_) ->
                            buildCondBr(builder)(buildICmp(builder)(intPredicateNe)(rest)(floatTextConst(i64)(0u64))(name + "_more"))(bodyBlock)(doneBlock)))
                    |> (given (_) -> positionBuilderAtEnd(builder)(doneBlock))
                    |> (given (_) -> buildLoad(builder)(i64)(countSlot)(name + "_result")))

// One step of the digit loop: the least significant remaining digit goes to the position
// `last - index`, the value loses that digit, and the index advances.
let emitFloatTextDigitStep builder i64 i8 bufferType buffer workSlot indexSlot last name =
    (let index = buildLoad(builder)(i64)(indexSlot)(name + "_index_value")
    in
        let work = buildLoad(builder)(i64)(workSlot)(name + "_work_value")
        in
            let digit =
                buildURem(builder)(work)(floatTextConst(i64)(10u64))(name + "_digit")
            in
                let position = buildSub(builder)(last)(index)(name + "_position")
                in
                    Unit
                    |> (given (_) ->
                        name + "_digit_ptr"
                        |> floatTextBytePtr(builder)(i64)(bufferType)(buffer)(position)
                        |> buildStore(builder)(buildTrunc(builder)(buildAdd(builder)(digit)(floatTextConst(i64)(48u64))(name + "_ascii"))(i8)(name + "_digit_byte")))
                    |> (given (_) ->
                        buildStore(builder)(buildUDiv(builder)(work)(floatTextConst(i64)(10u64))(name + "_work_next"))(workSlot))
                    |> (given (_) ->
                        buildStore(builder)(buildAdd(builder)(index)(floatTextConst(i64)(1u64))(name + "_index_next"))(indexSlot)))

// Writes exactly `count` decimal digits of the non-negative `value` at the cursor, most
// significant first (leading zeros when `count` exceeds the value's digits), and advances
// the cursor by `count`.
let emitFloatTextDigits context function_ builder i64 i8 bufferType buffer cursorSlot value count name =
    (let workSlot = buildEntryAlloca(builder)(i64)(name + "_work")
    in
        let indexSlot = buildEntryAlloca(builder)(i64)(name + "_index")
        in
            let checkBlock = appendBasicBlock(context)(function_)(name + "_check")
            in
                let bodyBlock = appendBasicBlock(context)(function_)(name + "_body")
                in
                    let doneBlock = appendBasicBlock(context)(function_)(name + "_done")
                    in
                        let cursor = buildLoad(builder)(i64)(cursorSlot)(name + "_cursor")
                        in
                            let last =
                                buildAdd(builder)(cursor)(buildSub(builder)(count)(floatTextConst(i64)(1u64))(name + "_count_less_one"))(name + "_last")
                            in
                                Unit
                                |> (given (_) -> buildStore(builder)(value)(workSlot))
                                |> (given (_) ->
                                    buildStore(builder)(floatTextConst(i64)(0u64))(indexSlot))
                                |> (given (_) -> buildBr(builder)(checkBlock))
                                |> (given (_) -> positionBuilderAtEnd(builder)(checkBlock))
                                |> (given (_) -> buildLoad(builder)(i64)(indexSlot)(name + "_index_test"))
                                |> (given (index) ->
                                    buildCondBr(builder)(buildICmp(builder)(intPredicateEq)(index)(count)(name + "_done_test"))(doneBlock)(bodyBlock))
                                |> (given (_) -> positionBuilderAtEnd(builder)(bodyBlock))
                                |> (given (_) -> emitFloatTextDigitStep(builder)(i64)(i8)(bufferType)(buffer)(workSlot)(indexSlot)(last)(name))
                                |> (given (_) -> buildBr(builder)(checkBlock))
                                |> (given (_) -> positionBuilderAtEnd(builder)(doneBlock))
                                |> (given (_) ->
                                    buildStore(builder)(buildAdd(builder)(cursor)(count)(name + "_advanced"))(cursorSlot)))

// The decimal digits of the non-negative `value` in their natural width.
let emitFloatTextDecimal context function_ builder i64 i8 bufferType buffer cursorSlot value name =
    name
    |> emitFloatTextDigitCount(context)(function_)(builder)(i64)(value)
    |> (given (count) -> emitFloatTextDigits(context)(function_)(builder)(i64)(i8)(bufferType)(buffer)(cursorSlot)(value)(count)(name))

// The fraction digits: `scaled` is the fraction scaled by `10^digits`. With `trim`, trailing
// zero digits are dropped down to one digit first (stage 0's
// `EmitFractionalDigitsToStringTrim`).
let emitFloatTextFraction context function_ builder i64 i8 bufferType buffer cursorSlot scaled digits (trim: Bool) name =
    if trim
    then
        let workSlot = buildEntryAlloca(builder)(i64)(name + "_trim_work")
        in
            let widthSlot = buildEntryAlloca(builder)(i64)(name + "_trim_width")
            in
                let checkBlock = appendBasicBlock(context)(function_)(name + "_trim_check")
                in
                    let bodyBlock = appendBasicBlock(context)(function_)(name + "_trim_body")
                    in
                        let emitBlock = appendBasicBlock(context)(function_)(name + "_trim_emit")
                        in
                            Unit
                            |> (given (_) -> buildStore(builder)(scaled)(workSlot))
                            |> (given (_) -> buildStore(builder)(digits)(widthSlot))
                            |> (given (_) -> buildBr(builder)(checkBlock))
                            |> (given (_) -> positionBuilderAtEnd(builder)(checkBlock))
                            |> (given (_) -> (buildLoad(builder)(i64)(workSlot)(name + "_trim_value"), buildLoad(builder)(i64)(widthSlot)(name + "_trim_width_value")))
                            |> (given (loaded) ->
                                match loaded with
                                    | (work, width) ->
                                        Unit
                                        |> (given (_) ->
                                            buildAnd(builder)(buildICmp(builder)(intPredicateUgt)(width)(floatTextConst(i64)(1u64))(name + "_can_trim"))(buildICmp(builder)(intPredicateEq)(buildURem(builder)(work)(floatTextConst(i64)(10u64))(name + "_trim_rem"))(floatTextConst(i64)(0u64))(name + "_trailing_zero"))(name + "_should_trim"))
                                        |> (given (shouldTrim) -> buildCondBr(builder)(shouldTrim)(bodyBlock)(emitBlock))
                                        |> (given (_) -> positionBuilderAtEnd(builder)(bodyBlock))
                                        |> (given (_) ->
                                            buildStore(builder)(buildUDiv(builder)(work)(floatTextConst(i64)(10u64))(name + "_trimmed_work"))(workSlot))
                                        |> (given (_) ->
                                            buildStore(builder)(buildSub(builder)(width)(floatTextConst(i64)(1u64))(name + "_trimmed_width"))(widthSlot))
                                        |> (given (_) -> buildBr(builder)(checkBlock)))
                            |> (given (_) -> positionBuilderAtEnd(builder)(emitBlock))
                            |> (given (_) ->
                                emitFloatTextDigits(context)(function_)(builder)(i64)(i8)(bufferType)(buffer)(cursorSlot)(buildLoad(builder)(i64)(workSlot)(name + "_fraction_value"))(buildLoad(builder)(i64)(widthSlot)(name + "_fraction_width"))(name))
    else emitFloatTextDigits(context)(function_)(builder)(i64)(i8)(bufferType)(buffer)(cursorSlot)(scaled)(digits)(name)

// `10^decimals` as an `i64`, by repeated multiplication.
let emitFloatTextScale context function_ builder i64 decimals name =
    (let scaleSlot = buildEntryAlloca(builder)(i64)(name + "_value")
    in
        let counterSlot = buildEntryAlloca(builder)(i64)(name + "_counter")
        in
            let checkBlock = appendBasicBlock(context)(function_)(name + "_check")
            in
                let bodyBlock = appendBasicBlock(context)(function_)(name + "_body")
                in
                    let doneBlock = appendBasicBlock(context)(function_)(name + "_done")
                    in
                        Unit
                        |> (given (_) ->
                            buildStore(builder)(floatTextConst(i64)(1u64))(scaleSlot))
                        |> (given (_) ->
                            buildStore(builder)(floatTextConst(i64)(0u64))(counterSlot))
                        |> (given (_) -> buildBr(builder)(checkBlock))
                        |> (given (_) -> positionBuilderAtEnd(builder)(checkBlock))
                        |> (given (_) -> buildLoad(builder)(i64)(counterSlot)(name + "_counter_value"))
                        |> (given (counter) ->
                            Unit
                            |> (given (_) ->
                                buildCondBr(builder)(buildICmp(builder)(intPredicateSlt)(counter)(decimals)(name + "_continue"))(bodyBlock)(doneBlock))
                            |> (given (_) -> positionBuilderAtEnd(builder)(bodyBlock))
                            |> (given (_) ->
                                buildStore(builder)(buildMul(builder)(buildLoad(builder)(i64)(scaleSlot)(name + "_current"))(floatTextConst(i64)(10u64))(name + "_next"))(scaleSlot))
                            |> (given (_) ->
                                buildStore(builder)(buildAdd(builder)(counter)(floatTextConst(i64)(1u64))(name + "_counter_next"))(counterSlot)))
                        |> (given (_) -> buildBr(builder)(checkBlock))
                        |> (given (_) -> positionBuilderAtEnd(builder)(doneBlock))
                        |> (given (_) -> buildLoad(builder)(i64)(scaleSlot)(name + "_final")))

// The fixed-point text of the non-negative `absValue` with `decimals` fraction digits: the
// integer part, then, when `decimals` is positive, a dot and the fraction rounded half up (a
// carry out of the fraction bumps the integer part), stage 0's
// `EmitUnsignedFloatToDecimalString`. `decimals` stays within 0..18 so the scale fits a signed
// 64-bit word, and `absValue` below the signed 64-bit range so the integer conversion is defined.
let emitFloatTextUnsigned context function_ builder i64 i8 f64 bufferType buffer cursorSlot absValue decimals (trim: Bool) name =
    (let integerPart = buildFPToSI(builder)(absValue)(i64)(name + "_integer")
    in
        let fractional =
            buildFSub(builder)(absValue)(buildSIToFP(builder)(integerPart)(f64)(name + "_integer_f64"))(name + "_fractional")
        in
            let scale = emitFloatTextScale(context)(function_)(builder)(i64)(decimals)(name + "_scale")
            in
                let scaledRaw =
                    buildFPToSI(builder)(buildFAdd(builder)(buildFMul(builder)(fractional)(buildSIToFP(builder)(scale)(f64)(name + "_scale_f64"))(name + "_scaled_f64"))(constReal(f64)(0.5))(name + "_scaled_rounded"))(i64)(name + "_scaled_raw")
                in
                    let hasCarry = buildICmp(builder)(intPredicateSge)(scaledRaw)(scale)(name + "_has_carry")
                    in
                        let scaled =
                            buildSelect(builder)(hasCarry)(floatTextConst(i64)(0u64))(scaledRaw)(name + "_scaled")
                        in
                            let integerFinal =
                                buildSelect(builder)(hasCarry)(buildAdd(builder)(integerPart)(floatTextConst(i64)(1u64))(name + "_integer_carry"))(integerPart)(name + "_integer_final")
                            in
                                let fractionBlock = appendBasicBlock(context)(function_)(name + "_fraction")
                                in
                                    let joinBlock = appendBasicBlock(context)(function_)(name + "_join")
                                    in
                                        Unit
                                        |> (given (_) -> emitFloatTextDecimal(context)(function_)(builder)(i64)(i8)(bufferType)(buffer)(cursorSlot)(integerFinal)(name + "_integer_text"))
                                        |> (given (_) ->
                                            buildCondBr(builder)(buildICmp(builder)(intPredicateSgt)(decimals)(floatTextConst(i64)(0u64))(name + "_has_fraction"))(fractionBlock)(joinBlock))
                                        |> (given (_) -> positionBuilderAtEnd(builder)(fractionBlock))
                                        |> (given (_) -> emitFloatTextAppendAscii(builder)(i64)(i8)(bufferType)(buffer)(cursorSlot)(46u64)(name + "_dot"))
                                        |> (given (_) -> emitFloatTextFraction(context)(function_)(builder)(i64)(i8)(bufferType)(buffer)(cursorSlot)(scaled)(decimals)(trim)(name + "_fraction_text"))
                                        |> (given (_) -> buildBr(builder)(joinBlock))
                                        |> (given (_) -> positionBuilderAtEnd(builder)(joinBlock)))

// The scientific text of a magnitude past the signed 64-bit range: the magnitude is divided
// by ten until it drops below ten, counting the exponent, and rendered as the fixed-point
// mantissa followed by `e+` and the exponent.
let emitFloatTextScientific context function_ builder i64 i8 f64 bufferType buffer cursorSlot absValue decimals (trim: Bool) name =
    (let normalizedSlot = buildEntryAlloca(builder)(f64)(name + "_normalized")
    in
        let exponentSlot = buildEntryAlloca(builder)(i64)(name + "_exponent")
        in
            let checkBlock = appendBasicBlock(context)(function_)(name + "_check")
            in
                let bodyBlock = appendBasicBlock(context)(function_)(name + "_body")
                in
                    let finishBlock = appendBasicBlock(context)(function_)(name + "_finish")
                    in
                        Unit
                        |> (given (_) -> buildStore(builder)(absValue)(normalizedSlot))
                        |> (given (_) ->
                            buildStore(builder)(floatTextConst(i64)(0u64))(exponentSlot))
                        |> (given (_) -> buildBr(builder)(checkBlock))
                        |> (given (_) -> positionBuilderAtEnd(builder)(checkBlock))
                        |> (given (_) -> buildLoad(builder)(f64)(normalizedSlot)(name + "_normalized_value"))
                        |> (given (normalized) ->
                            Unit
                            |> (given (_) ->
                                buildCondBr(builder)(buildFCmp(builder)(realPredicateOge)(normalized)(constReal(f64)(10.0))(name + "_should_scale"))(bodyBlock)(finishBlock))
                            |> (given (_) -> positionBuilderAtEnd(builder)(bodyBlock))
                            |> (given (_) ->
                                buildStore(builder)(buildFDiv(builder)(normalized)(constReal(f64)(10.0))(name + "_normalized_next"))(normalizedSlot))
                            |> (given (_) ->
                                buildStore(builder)(buildAdd(builder)(buildLoad(builder)(i64)(exponentSlot)(name + "_exponent_value"))(floatTextConst(i64)(1u64))(name + "_exponent_next"))(exponentSlot)))
                        |> (given (_) -> buildBr(builder)(checkBlock))
                        |> (given (_) -> positionBuilderAtEnd(builder)(finishBlock))
                        |> (given (_) ->
                            emitFloatTextUnsigned(context)(function_)(builder)(i64)(i8)(f64)(bufferType)(buffer)(cursorSlot)(buildLoad(builder)(f64)(normalizedSlot)(name + "_normalized_final"))(decimals)(trim)(name + "_mantissa"))
                        |> (given (_) -> emitFloatTextAppendAscii(builder)(i64)(i8)(bufferType)(buffer)(cursorSlot)(101u64)(name + "_e"))
                        |> (given (_) -> emitFloatTextAppendAscii(builder)(i64)(i8)(bufferType)(buffer)(cursorSlot)(43u64)(name + "_plus"))
                        |> (given (_) ->
                            emitFloatTextDecimal(context)(function_)(builder)(i64)(i8)(bufferType)(buffer)(cursorSlot)(buildLoad(builder)(i64)(exponentSlot)(name + "_exponent_final"))(name + "_exponent_text")))

// The decimal text of the float whose raw `f64` bits `valueWord` carries, with `decimals`
// fraction digits (0..18): a leading `-` for a negative value, the fixed-point text of the
// magnitude when it fits the signed 64-bit range and the scientific text otherwise, handed to
// `place` as the buffer's address word and byte count.
let emitFloatToText context function_ builder i64 i8 place valueWord decimals (trim: Bool) name =
    (let f64 = doubleType(context)
    in
        let bufferType = arrayType(i8)(floatTextBufferBytes)
        in
            let buffer = buildEntryAlloca(builder)(bufferType)(name + "_buffer")
            in
                let cursorSlot = buildEntryAlloca(builder)(i64)(name + "_cursor")
                in
                    let value = buildBitCast(builder)(valueWord)(f64)(name + "_value")
                    in
                        let isNegative =
                            buildFCmp(builder)(realPredicateOlt)(value)(constReal(f64)(0.0))(name + "_is_negative")
                        in
                            let absValue =
                                buildSelect(builder)(isNegative)(buildFSub(builder)(constReal(f64)(0.0))(value)(name + "_abs_neg"))(value)(name + "_abs")
                            in
                                let signBlock = appendBasicBlock(context)(function_)(name + "_sign")
                                in
                                    let magnitudeBlock = appendBasicBlock(context)(function_)(name + "_magnitude")
                                    in
                                        let fixedBlock = appendBasicBlock(context)(function_)(name + "_fixed")
                                        in
                                            let scientificBlock = appendBasicBlock(context)(function_)(name + "_scientific")
                                            in
                                                let mergeBlock = appendBasicBlock(context)(function_)(name + "_merge")
                                                in
                                                    Unit
                                                    |> (given (_) ->
                                                        buildStore(builder)(floatTextConst(i64)(0u64))(cursorSlot))
                                                    |> (given (_) -> buildCondBr(builder)(isNegative)(signBlock)(magnitudeBlock))
                                                    |> (given (_) -> positionBuilderAtEnd(builder)(signBlock))
                                                    |> (given (_) -> emitFloatTextAppendAscii(builder)(i64)(i8)(bufferType)(buffer)(cursorSlot)(45u64)(name + "_minus"))
                                                    |> (given (_) -> buildBr(builder)(magnitudeBlock))
                                                    |> (given (_) -> positionBuilderAtEnd(builder)(magnitudeBlock))
                                                    |> (given (_) ->
                                                        buildCondBr(builder)(buildFCmp(builder)(realPredicateOgt)(absValue)(constReal(f64)(9223372036854773760.0))(name + "_needs_scientific"))(scientificBlock)(fixedBlock))
                                                    |> (given (_) -> positionBuilderAtEnd(builder)(fixedBlock))
                                                    |> (given (_) -> emitFloatTextUnsigned(context)(function_)(builder)(i64)(i8)(f64)(bufferType)(buffer)(cursorSlot)(absValue)(decimals)(trim)(name + "_fixed_text"))
                                                    |> (given (_) -> buildBr(builder)(mergeBlock))
                                                    |> (given (_) -> positionBuilderAtEnd(builder)(scientificBlock))
                                                    |> (given (_) -> emitFloatTextScientific(context)(function_)(builder)(i64)(i8)(f64)(bufferType)(buffer)(cursorSlot)(absValue)(decimals)(trim)(name + "_scientific_text"))
                                                    |> (given (_) -> buildBr(builder)(mergeBlock))
                                                    |> (given (_) -> positionBuilderAtEnd(builder)(mergeBlock))
                                                    |> (given (_) ->
                                                        name + "_length"
                                                        |> buildLoad(builder)(i64)(cursorSlot)
                                                        |> place(buildPtrToInt(builder)(floatTextBytePtr(builder)(i64)(bufferType)(buffer)(floatTextConst(i64)(0u64))(name + "_start_ptr"))(i64)(name + "_start_addr"))))

// `Text.fromFloat`: six fraction digits, trailing zeros trimmed down to one.
let emitTextFromFloat context function_ builder i64 i8 place valueWord =
    emitFloatToText(context)(function_)(builder)(i64)(i8)(place)(valueWord)(floatTextConst(i64)(6u64))(true)("from_float")

// `Text.formatFloat`: exactly `decimals` fraction digits, the count clamped to 0..18.
let emitTextFormatFloat context function_ builder i64 i8 place valueWord decimals =
    (let zero = floatTextConst(i64)(0u64)
    in
        let clampedLow =
            buildSelect(builder)(buildICmp(builder)(intPredicateSlt)(decimals)(zero)("format_float_below_zero"))(zero)(decimals)("format_float_clamped_low")
        in
            let clamped =
                buildSelect(builder)(buildICmp(builder)(intPredicateSgt)(clampedLow)(floatTextConst(i64)(18u64))("format_float_above_max"))(floatTextConst(i64)(18u64))(clampedLow)("format_float_clamped")
            in emitFloatToText(context)(function_)(builder)(i64)(i8)(place)(valueWord)(clamped)(false)("format_float"))
