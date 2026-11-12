// Define the test's native functions inside the emitted LLVM module. This exercises real ABI
// calls without depending on an installed fixture library or the linker's external-symbol table.
import AshesCompiler.Backend.Llvm
let fixtureEntry module_ context builder name =
    (let function_ = getNamedFunction(module_)(name)
    in
        let _ = setLinkage(function_)(linkageInternal)
        in
            let entry = appendBasicBlock(context)(function_)("fixture_entry")
            in
                let _ = positionBuilderAtEnd(builder)(entry)
                in function_)

let fixtureWordGlobal module_ context name value =
    (let i64 = int64Type(context)
    in
        let global = addGlobal(module_)(i64)(name)
        in
            let _ = setLinkage(global)(linkageInternal)
            in
                let _ =
                    false
                    |> constInt(i64)(Ashes.Number.UInt.fromInt64(value))
                    |> setInitializer(global)
                in global)

let fixtureBytes module_ context name codes =
    (let i8 = int8Type(context)
    in
        let count =
            codes
            |> Ashes.Collection.List.length
            |> Ashes.Number.UInt.fromInt64
        in
            let values =
                Ashes.Collection.List.map(given (code) ->
                    constInt(i8)(Ashes.Number.UInt.fromInt64(code))(false))(codes)
            in
                let global =
                    addGlobal(module_)(arrayType(i8)(count))(name)
                in
                    let _ = setLinkage(global)(linkageInternal)
                    in
                        let _ =
                            count
                            |> constArray(i8)(values)
                            |> setInitializer(global)
                        in
                            let _ = setGlobalConstant(global)(true)
                            in global)

let recursive fixtureStringFunctions module_ context builder entries =
    match entries with
        | [] -> Unit
        | (name, codes) :: rest ->
            let bytes = fixtureBytes(module_)(context)(name + "_bytes")(codes)
            in
                let _ = fixtureEntry(module_)(context)(builder)(name)
                in
                    let _ = buildRet(builder)(bytes)
                    in fixtureStringFunctions(module_)(context)(builder)(rest)

let addNativeStringFixtures module_ context builder =
    (let i64 = int64Type(context)
    in
        let ptrType = pointerType(context)(0u32)
        in
            let counter = fixtureWordGlobal(module_)(context)("native_release_count")(0)
            in
                Unit
                |> (given (_) ->
                    fixtureStringFunctions(module_)(context)(builder)([
                        ("nativeValid", [97, 226, 156, 147, 0]),
                        ("nativeEmpty", [0]),
                        ("nativeInvalid", [192, 128, 0]),
                        ("nativeSurrogate", [237, 160, 128, 0]),
                        ("nativeTruncated", [226, 130, 0]),
                        ("nativeAboveMax", [244, 144, 128, 128, 0]),
                        ("nativeMaxValid", [244, 143, 191, 191, 0]),
                        ("nativeOverlong", [224, 128, 128, 0]),
                        ("nativeFourOverlong", [240, 128, 128, 128, 0]),
                        ("nativeTwoByte", [194, 160, 0]),
                        ("nativeBorrowed", [97, 226, 156, 147, 0])
                    ]))
                |> (given (_) -> fixtureEntry(module_)(context)(builder)("nativeNull"))
                |> (given (_) ->
                    ptrType
                    |> constNull
                    |> buildRet(builder))
                |> (given (_) -> fixtureEntry(module_)(context)(builder)("nativeNullRequired"))
                |> (given (_) ->
                    ptrType
                    |> constNull
                    |> buildRet(builder))
                |> (given (_) -> fixtureEntry(module_)(context)(builder)("nativeRelease"))
                |> (given (_) ->
                    let previous = buildLoad(builder)(i64)(counter)("release_count")
                    in
                        let _ =
                            buildStore(builder)(buildAdd(builder)(previous)(constInt(i64)(1u64)(false))("release_next"))(counter)
                        in buildRetVoid(builder))
                |> (given (_) -> fixtureEntry(module_)(context)(builder)("nativeCount"))
                |> (given (_) ->
                    "count_value"
                    |> buildLoad(builder)(i64)(counter)
                    |> buildRet(builder))
                |> (given (_) -> fixtureEntry(module_)(context)(builder)("nativeOut"))
                |> (given (function_) ->
                    let bytes = fixtureBytes(module_)(context)("native_out_bytes")([97, 226, 156, 147, 0])
                    in
                        let _ =
                            0u32
                            |> getParam(function_)
                            |> buildStore(builder)(bytes)
                        in
                            false
                            |> constInt(i64)(5u64)
                            |> buildRet(builder))
                |> (given (_) -> (module_, builder)))

let addNativeOutFixtures module_ context builder =
    (let i64 = int64Type(context)
    in
        let first = fixtureWordGlobal(module_)(context)("native_first_handle")(101)
        in
            let _ = setGlobalConstant(first)(true)
            in
                let second = fixtureWordGlobal(module_)(context)("native_second_handle")(202)
                in
                    let _ = setGlobalConstant(second)(true)
                    in
                        let function_ = fixtureEntry(module_)(context)(builder)("nativeHandles")
                        in
                            let success = appendBasicBlock(context)(function_)("fixture_first")
                            in
                                let failure = appendBasicBlock(context)(function_)("fixture_second")
                                in
                                    Unit
                                    |> (given (_) ->
                                        buildCondBr(builder)(buildICmp(builder)(intPredicateEq)(getParam(function_)(0u32))(constInt(i64)(0u64)(false))("first_case"))(success)(failure))
                                    |> (given (_) -> positionBuilderAtEnd(builder)(success))
                                    |> (given (_) ->
                                        1u32
                                        |> getParam(function_)
                                        |> buildStore(builder)(first))
                                    |> (given (_) ->
                                        false
                                        |> constInt(i64)(7u64)
                                        |> buildRet(builder))
                                    |> (given (_) -> positionBuilderAtEnd(builder)(failure))
                                    |> (given (_) ->
                                        2u32
                                        |> getParam(function_)
                                        |> buildStore(builder)(second))
                                    |> (given (_) ->
                                        false
                                        |> constInt(i64)(9u64)
                                        |> buildRet(builder))
                                    |> (given (_) -> fixtureEntry(module_)(context)(builder)("nativeRead"))
                                    |> (given (reader) ->
                                        "handle_value"
                                        |> buildLoad(builder)(i64)(getParam(reader)(0u32))
                                        |> buildRet(builder))
                                    |> (given (_) -> (module_, builder)))
