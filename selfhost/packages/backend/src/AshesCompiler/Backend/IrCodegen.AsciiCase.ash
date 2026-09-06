// `Text.asciiUpper`/`Text.asciiLower` for `AshesCompiler.Backend.IrCodegen`, stage 0's
// `EmitAsciiCaseString`: the source's bytes are copied into a string placed where the
// instruction asks, then every ASCII letter of the copy is flipped to the other case in place;
// every other byte, multi-byte UTF-8 sequences included, is left as it is. Depends on the LLVM
// bindings and `IrCodegen.Support`.

import AshesCompiler.Backend.Llvm
import AshesCompiler.Backend.IrCodegen.Support
export (
    value emitTextAsciiCase,
)

let asciiCaseConst i8 (value: u64) = constInt(i8)(value)(false)

// Maps the byte at `index` of the copy's bytes.
let emitAsciiCaseByte builder i64 i8 destBytes index (upper: Bool) name =
    (let bytePtr = buildGEP(builder)(i8)(destBytes)([index])(1u32)(name + "_byte_ptr")
    in
        let byteValue = buildLoad(builder)(i8)(bytePtr)(name + "_byte")
        in
            let lowBound =
                asciiCaseConst(i8)(if upper
                then 97u64
                else 65u64)
            in
                let highBound =
                    asciiCaseConst(i8)(if upper
                    then 122u64
                    else 90u64)
                in
                    let isLetter =
                        buildAnd(builder)(buildICmp(builder)(intPredicateUge)(byteValue)(lowBound)(name + "_ge_low"))(buildICmp(builder)(intPredicateUle)(byteValue)(highBound)(name + "_le_high"))(name + "_is_letter")
                    in
                        name + "_mapped"
                        |> buildSelect(builder)(isLetter)(buildXor(builder)(byteValue)(asciiCaseConst(i8)(32u64))(name + "_flipped"))(byteValue)
                        |> (given (mapped) -> buildStore(builder)(mapped)(bytePtr)))

// The placed copy of `sourceRef` with its ASCII letters mapped to `upper` or lower case;
// `place` takes the source's bytes address and length and answers the placed copy.
let emitTextAsciiCase context function_ builder i64 i8 ptrType place (upper: Bool) sourceRef =
    (let name =
        if upper
        then "ascii_upper"
        else "ascii_lower"
    in
        match emitStringParts(builder)(i64)(ptrType)(sourceRef)(name + "_src") with
            | (len, srcBytesAddr) ->
                let result = place(srcBytesAddr)(len)
                in
                    match emitStringParts(builder)(i64)(ptrType)(result)(name + "_dest") with
                        | (_destLen, destBytesAddr) ->
                            let destBytes = buildIntToPtr(builder)(destBytesAddr)(ptrType)(name + "_dest_ptr")
                            in
                                let indexSlot = buildEntryAlloca(builder)(i64)(name + "_index")
                                in
                                    let checkBlock = appendBasicBlock(context)(function_)(name + "_check")
                                    in
                                        let bodyBlock = appendBasicBlock(context)(function_)(name + "_body")
                                        in
                                            let doneBlock = appendBasicBlock(context)(function_)(name + "_done")
                                            in
                                                Unit
                                                |> (given (_) ->
                                                    buildStore(builder)(constInt(i64)(0u64)(false))(indexSlot))
                                                |> (given (_) -> buildBr(builder)(checkBlock))
                                                |> (given (_) -> positionBuilderAtEnd(builder)(checkBlock))
                                                |> (given (_) -> buildLoad(builder)(i64)(indexSlot)(name + "_index_value"))
                                                |> (given (index) ->
                                                    Unit
                                                    |> (given (_) ->
                                                        buildCondBr(builder)(buildICmp(builder)(intPredicateUlt)(index)(len)(name + "_more"))(bodyBlock)(doneBlock))
                                                    |> (given (_) -> positionBuilderAtEnd(builder)(bodyBlock))
                                                    |> (given (_) -> emitAsciiCaseByte(builder)(i64)(i8)(destBytes)(index)(upper)(name))
                                                    |> (given (_) ->
                                                        buildStore(builder)(buildAdd(builder)(index)(constInt(i64)(1u64)(false))(name + "_index_next"))(indexSlot)))
                                                |> (given (_) -> buildBr(builder)(checkBlock))
                                                |> (given (_) -> positionBuilderAtEnd(builder)(doneBlock))
                                                |> (given (_) -> result))
