// The `Ashes.Number.BigInt` runtime for `AshesCompiler.Backend.IrCodegen`, stage 0's
// `LlvmCodegenBuiltins.BigInt.cs`. A BigInt is a pointer to `{ i64 header, i64 limb[...] }`
// with `header = (negFlag << 32) | limbCount`, the magnitude base 2^64 little-endian and
// normalized (zero is header 0 with no limbs). The arithmetic lives in internal LLVM
// functions emitted once per program that uses BigInt (`bignum_add`/`_sub`/`_mul`/`_divmod`/
// `_cmp`/`_from_i64`/`_to_decimal`/`_from_decimal` over the `bi_normalize`/`bi_cmp_mag`/
// `bi_add_mag`/`bi_sub_mag` magnitude helpers); they never allocate, so each call site reads
// the operand limb counts, pre-sizes the result and scratch buffers where the instruction's
// placement asks, and passes them in. Division is Knuth's Algorithm D over 32-bit digits, so
// every intermediate fits native `i64` arithmetic and no `i128` division reaches the
// freestanding binary. Depends on the LLVM bindings, `IrCodegen.Support`, `IrCodegen.Arena`,
// and `IrCodegen.Rc`.

import AshesCompiler.Backend.Llvm
import AshesCompiler.Backend.IrCodegen.Support
import AshesCompiler.Backend.IrCodegen.Arena
import AshesCompiler.Backend.IrCodegen.Rc
// The limb count and sign of the bignum at `p`.
// The 32-bit digit view of a magnitude: base-2^32 digits, little-endian (digit 0 is the low
// half of limb 1). A bignum's digits start at `i32` index 2, past the 8-byte header; a raw
// scratch buffer's at index 0; `bias` says which.
export (
    type BigIntRuntime(..),
    value defineBigIntRuntime,
    value bigIntRuntimeOf,
    value emitBigIntFromInt,
    value emitBigIntBinary,
    value emitBigIntCompare,
    value emitBigIntToString,
    value emitBigIntToInt,
    value emitBigIntFromString,
)

// The runtime helper functions and their types.
type BigIntRuntime =
    | normalizeFn: LLVMValueRef
    | normalizeType: LLVMTypeRef
    | cmpMagFn: LLVMValueRef
    | cmpMagType: LLVMTypeRef
    | addMagFn: LLVMValueRef
    | subMagFn: LLVMValueRef
    | magType: LLVMTypeRef
    | fromI64Fn: LLVMValueRef
    | fromI64Type: LLVMTypeRef
    | cmpFn: LLVMValueRef
    | cmpType: LLVMTypeRef
    | addFn: LLVMValueRef
    | subFn: LLVMValueRef
    | mulFn: LLVMValueRef
    | arithType: LLVMTypeRef
    | divModFn: LLVMValueRef
    | divModType: LLVMTypeRef
    | toDecimalFn: LLVMValueRef
    | toDecimalType: LLVMTypeRef
    | fromDecimalFn: LLVMValueRef
    | fromDecimalType: LLVMTypeRef

// The builder toolkit the helper bodies share: loop-mutable state lives in allocas and the
// blocks follow the preheader, condition, body, exit idiom without phi nodes.
type Bi =
    | biContext: LLVMContextRef
    | biBuilder: LLVMBuilderRef
    | biI8: LLVMTypeRef
    | biI32: LLVMTypeRef
    | biI64: LLVMTypeRef
    | biI128: LLVMTypeRef
    | biPtr: LLVMTypeRef

let biK (e: Bi) (value: Int) =
    constInt(e.biI64)(Ashes.Number.UInt.fromInt64(value))(false)

let biK128 (e: Bi) (value: Int) =
    constInt(e.biI128)(Ashes.Number.UInt.fromInt64(value))(false)

let biBlk (e: Bi) fn name = appendBasicBlock(e.biContext)(fn)(name)

let biAt (e: Bi) block = positionBuilderAtEnd(e.biBuilder)(block)

let biSlot (e: Bi) name = buildAlloca(e.biBuilder)(e.biI64)(name)

let biLd (e: Bi) slot name = buildLoad(e.biBuilder)(e.biI64)(slot)(name)

let biSt (e: Bi) value slot = buildStore(e.biBuilder)(value)(slot)

let biAdd (e: Bi) a b name = buildAdd(e.biBuilder)(a)(b)(name)

let biSub (e: Bi) a b name = buildSub(e.biBuilder)(a)(b)(name)

let biMul (e: Bi) a b name = buildMul(e.biBuilder)(a)(b)(name)

let biUDiv (e: Bi) a b name = buildUDiv(e.biBuilder)(a)(b)(name)

let biURem (e: Bi) a b name = buildURem(e.biBuilder)(a)(b)(name)

let biAnd (e: Bi) a b name = buildAnd(e.biBuilder)(a)(b)(name)

let biOr (e: Bi) a b name = buildOr(e.biBuilder)(a)(b)(name)

let biXor (e: Bi) a b name = buildXor(e.biBuilder)(a)(b)(name)

let biShl (e: Bi) a b name = buildShl(e.biBuilder)(a)(b)(name)

let biLshr (e: Bi) a b name = buildLShr(e.biBuilder)(a)(b)(name)

let biAshr (e: Bi) a b name = buildAShr(e.biBuilder)(a)(b)(name)

let biCmp (e: Bi) predicate a b name = buildICmp(e.biBuilder)(predicate)(a)(b)(name)

let biSel (e: Bi) condition thenValue elseValue name = buildSelect(e.biBuilder)(condition)(thenValue)(elseValue)(name)

let biZExt64 (e: Bi) value name = buildZExt(e.biBuilder)(value)(e.biI64)(name)

let biZExt128 (e: Bi) value name = buildZExt(e.biBuilder)(value)(e.biI128)(name)

let biTrunc64 (e: Bi) value name = buildTrunc(e.biBuilder)(value)(e.biI64)(name)

let biTrunc8 (e: Bi) value name = buildTrunc(e.biBuilder)(value)(e.biI8)(name)

let biBr (e: Bi) destination = buildBr(e.biBuilder)(destination)

let biCBr (e: Bi) condition thenBlock elseBlock = buildCondBr(e.biBuilder)(condition)(thenBlock)(elseBlock)

let biRet (e: Bi) value = buildRet(e.biBuilder)(value)

let biRetVoid (e: Bi) = buildRetVoid(e.biBuilder)

let biWordPtr (e: Bi) basePtr index name = buildGEP(e.biBuilder)(e.biI64)(basePtr)([index])(1u32)(name)

let biLdW (e: Bi) basePtr index name =
    buildLoad(e.biBuilder)(e.biI64)(biWordPtr(e)(basePtr)(index)(name + "_p"))(name)

let biStW (e: Bi) basePtr index value =
    "wp"
    |> biWordPtr(e)(basePtr)(index)
    |> buildStore(e.biBuilder)(value)

let biBytePtr (e: Bi) basePtr index name = buildGEP(e.biBuilder)(e.biI8)(basePtr)([index])(1u32)(name)

let biLdByte (e: Bi) basePtr index name =
    biZExt64(e)(buildLoad(e.biBuilder)(e.biI8)(biBytePtr(e)(basePtr)(index)(name + "_p"))(name + "_8"))(name)

let biStByte (e: Bi) basePtr index value64 name =
    name + "_p"
    |> biBytePtr(e)(basePtr)(index)
    |> buildStore(e.biBuilder)(biTrunc8(e)(value64)(name + "_8"))

let biCount (e: Bi) p name =
    biAnd(e)(biLdW(e)(p)(biK(e)(0))(name + "_h"))(biK(e)(4294967295))(name)

let biNeg (e: Bi) p name =
    biAnd(e)(biLshr(e)(biLdW(e)(p)(biK(e)(0))(name + "_h2"))(biK(e)(32))(name + "_s"))(biK(e)(1))(name)

let biDigPtr (e: Bi) basePtr index (bias: Int) name =
    buildGEP(e.biBuilder)(e.biI32)(basePtr)([biAdd(e)(index)(biK(e)(bias))(name + "_i")])(1u32)(name)

let biLdDig (e: Bi) basePtr index (bias: Int) name =
    biZExt64(e)(buildLoad(e.biBuilder)(e.biI32)(biDigPtr(e)(basePtr)(index)(bias)(name + "_p"))(name + "_32"))(name)

let biStDig (e: Bi) basePtr index (bias: Int) value64 =
    "dig_dst"
    |> biDigPtr(e)(basePtr)(index)(bias)
    |> buildStore(e.biBuilder)(buildTrunc(e.biBuilder)(value64)(e.biI32)("dig_tr"))

let biParam fn (index: u32) = getParam(fn)(index)

let biCallNormalize (e: Bi) (runtime: BigIntRuntime) outP sign count = buildCall(e.biBuilder)(runtime.normalizeType)(runtime.normalizeFn)([outP, sign, count])(3u32)("")

let biCallCmpMag (e: Bi) (runtime: BigIntRuntime) a na b nb name = buildCall(e.biBuilder)(runtime.cmpMagType)(runtime.cmpMagFn)([a, na, b, nb])(4u32)(name)

let biCallMag (e: Bi) (runtime: BigIntRuntime) fn a na b nb outP name = buildCall(e.biBuilder)(runtime.magType)(fn)([a, na, b, nb, outP])(5u32)(name)

// A loop that decrements the count in `nSlot` while it is positive and the limb it indexes is
// zero; the builder ends at a fresh done block.
let emitBiStripZeros (e: Bi) fn p nSlot (tag: Str) =
    (let condBlock = biBlk(e)(fn)(tag + "_cond")
    in
        let checkBlock = biBlk(e)(fn)(tag + "_check")
        in
            let decBlock = biBlk(e)(fn)(tag + "_dec")
            in
                let doneBlock = biBlk(e)(fn)(tag + "_done")
                in
                    Unit
                    |> (given (_) -> biBr(e)(condBlock))
                    |> (given (_) -> biAt(e)(condBlock))
                    |> (given (_) ->
                        biCBr(e)(biCmp(e)(intPredicateSgt)(biLd(e)(nSlot)("n"))(biK(e)(0))("n_pos"))(checkBlock)(doneBlock))
                    |> (given (_) -> biAt(e)(checkBlock))
                    |> (given (_) ->
                        biCBr(e)(biCmp(e)(intPredicateEq)(biLdW(e)(p)(biLd(e)(nSlot)("ni"))("top"))(biK(e)(0))("top0"))(decBlock)(doneBlock))
                    |> (given (_) -> biAt(e)(decBlock))
                    |> (given (_) ->
                        biSt(e)(biSub(e)(biLd(e)(nSlot)("nd"))(biK(e)(1))("ndec"))(nSlot))
                    |> (given (_) -> biBr(e)(condBlock))
                    |> (given (_) -> biAt(e)(doneBlock)))

// `bi_normalize(out, neg, n)`: strips leading zero limbs, then writes the header
// `(neg << 32) | n`, or 0 for a zero magnitude.
let emitBiNormalize (e: Bi) fn =
    (let outP = biParam(fn)(0u32)
    in
        let neg = biParam(fn)(1u32)
        in
            let n0 = biParam(fn)(2u32)
            in
                let entry = biBlk(e)(fn)("entry")
                in
                    let nSlot =
                        Unit
                        |> (given (_) -> biAt(e)(entry))
                        |> (given (_) -> biSlot(e)("n"))
                    in
                        let setZero = biBlk(e)(fn)("set_zero")
                        in
                            let setHdr = biBlk(e)(fn)("set_hdr")
                            in
                                let endBlock = biBlk(e)(fn)("end")
                                in
                                    Unit
                                    |> (given (_) -> biSt(e)(n0)(nSlot))
                                    |> (given (_) -> emitBiStripZeros(e)(fn)(outP)(nSlot)("strip"))
                                    |> (given (_) ->
                                        biCBr(e)(biCmp(e)(intPredicateEq)(biLd(e)(nSlot)("nf"))(biK(e)(0))("is0"))(setZero)(setHdr))
                                    |> (given (_) -> biAt(e)(setZero))
                                    |> (given (_) ->
                                        0
                                        |> biK(e)
                                        |> biStW(e)(outP)(biK(e)(0)))
                                    |> (given (_) -> biBr(e)(endBlock))
                                    |> (given (_) -> biAt(e)(setHdr))
                                    |> (given (_) ->
                                        "hdr"
                                        |> biOr(e)(biShl(e)(neg)(biK(e)(32))("nsh"))(biLd(e)(nSlot)("nn"))
                                        |> biStW(e)(outP)(biK(e)(0)))
                                    |> (given (_) -> biBr(e)(endBlock))
                                    |> (given (_) -> biAt(e)(endBlock))
                                    |> (given (_) -> biRetVoid(e)))

// `bi_cmp_mag(a, na, b, nb)`: -1, 0, or 1 by magnitude.
let emitBiCmpMag (e: Bi) fn =
    (let a = biParam(fn)(0u32)
    in
        let na = biParam(fn)(1u32)
        in
            let b = biParam(fn)(2u32)
            in
                let nb = biParam(fn)(3u32)
                in
                    let entry = biBlk(e)(fn)("entry")
                    in
                        let lenNe = biBlk(e)(fn)("len_ne")
                        in
                            let eqLen = biBlk(e)(fn)("eq_len")
                            in
                                let loop = biBlk(e)(fn)("loop")
                                in
                                    let body = biBlk(e)(fn)("body")
                                    in
                                        let chkGt = biBlk(e)(fn)("chk_gt")
                                        in
                                            let retNeg = biBlk(e)(fn)("ret_neg")
                                            in
                                                let retPos = biBlk(e)(fn)("ret_pos")
                                                in
                                                    let retZero = biBlk(e)(fn)("ret_zero")
                                                    in
                                                        let iSlot =
                                                            Unit
                                                            |> (given (_) -> biAt(e)(entry))
                                                            |> (given (_) -> biSlot(e)("i"))
                                                        in
                                                            Unit
                                                            |> (given (_) ->
                                                                biCBr(e)(biCmp(e)(intPredicateEq)(na)(nb)("len_eq"))(eqLen)(lenNe))
                                                            |> (given (_) -> biAt(e)(lenNe))
                                                            |> (given (_) ->
                                                                biCBr(e)(biCmp(e)(intPredicateUlt)(na)(nb)("na_lt"))(retNeg)(retPos))
                                                            |> (given (_) -> biAt(e)(eqLen))
                                                            |> (given (_) -> biSt(e)(na)(iSlot))
                                                            |> (given (_) -> biBr(e)(loop))
                                                            |> (given (_) -> biAt(e)(loop))
                                                            |> (given (_) ->
                                                                biCBr(e)(biCmp(e)(intPredicateSge)(biLd(e)(iSlot)("i"))(biK(e)(1))("i_ge1"))(body)(retZero))
                                                            |> (given (_) -> biAt(e)(body))
                                                            |> (given (_) -> biLd(e)(iSlot)("iv"))
                                                            |> (given (iv) ->
                                                                let x = biLdW(e)(a)(iv)("x")
                                                                in
                                                                    let y = biLdW(e)(b)(iv)("y")
                                                                    in
                                                                        Unit
                                                                        |> (given (_) ->
                                                                            biSt(e)(biSub(e)(iv)(biK(e)(1))("idec"))(iSlot))
                                                                        |> (given (_) ->
                                                                            biCBr(e)(biCmp(e)(intPredicateUlt)(x)(y)("x_lt"))(retNeg)(chkGt))
                                                                        |> (given (_) -> biAt(e)(chkGt))
                                                                        |> (given (_) ->
                                                                            biCBr(e)(biCmp(e)(intPredicateUgt)(x)(y)("x_gt"))(retPos)(loop)))
                                                            |> (given (_) -> biAt(e)(retNeg))
                                                            |> (given (_) ->
                                                                -1
                                                                |> biK(e)
                                                                |> biRet(e))
                                                            |> (given (_) -> biAt(e)(retPos))
                                                            |> (given (_) ->
                                                                1
                                                                |> biK(e)
                                                                |> biRet(e))
                                                            |> (given (_) -> biAt(e)(retZero))
                                                            |> (given (_) ->
                                                                0
                                                                |> biK(e)
                                                                |> biRet(e)))

// `bi_add_mag(a, na, b, nb, out)`: the magnitude sum, answering its limb count.
let emitBiAddMag (e: Bi) fn =
    (let a = biParam(fn)(0u32)
    in
        let na = biParam(fn)(1u32)
        in
            let b = biParam(fn)(2u32)
            in
                let nb = biParam(fn)(3u32)
                in
                    let outP = biParam(fn)(4u32)
                    in
                        let entry = biBlk(e)(fn)("entry")
                        in
                            let loop = biBlk(e)(fn)("loop")
                            in
                                let body = biBlk(e)(fn)("body")
                                in
                                    let after = biBlk(e)(fn)("after")
                                    in
                                        let carryBlock = biBlk(e)(fn)("carry")
                                        in
                                            let ret = biBlk(e)(fn)("ret")
                                            in
                                                let _ = biAt(e)(entry)
                                                in
                                                    let nSlot = biSlot(e)("n")
                                                    in
                                                        let iSlot = biSlot(e)("i")
                                                        in
                                                            let carrySlot = biSlot(e)("carry")
                                                            in
                                                                Unit
                                                                |> (given (_) ->
                                                                    biSt(e)(biSel(e)(biCmp(e)(intPredicateSgt)(na)(nb)("na_gt"))(na)(nb)("nmax"))(nSlot))
                                                                |> (given (_) ->
                                                                    biSt(e)(biK(e)(1))(iSlot))
                                                                |> (given (_) ->
                                                                    biSt(e)(biK(e)(0))(carrySlot))
                                                                |> (given (_) -> biBr(e)(loop))
                                                                |> (given (_) -> biAt(e)(loop))
                                                                |> (given (_) ->
                                                                    biCBr(e)(biCmp(e)(intPredicateSle)(biLd(e)(iSlot)("i"))(biLd(e)(nSlot)("n"))("i_le_n"))(body)(after))
                                                                |> (given (_) -> biAt(e)(body))
                                                                |> (given (_) -> biLd(e)(iSlot)("iv"))
                                                                |> (given (i2) ->
                                                                    let x =
                                                                        biSel(e)(biCmp(e)(intPredicateSle)(i2)(na)("x_in"))(biLdW(e)(a)(i2)("ax"))(biK(e)(0))("x")
                                                                    in
                                                                        let y =
                                                                            biSel(e)(biCmp(e)(intPredicateSle)(i2)(nb)("y_in"))(biLdW(e)(b)(i2)("by"))(biK(e)(0))("y")
                                                                        in
                                                                            let s = biAdd(e)(x)(y)("s")
                                                                            in
                                                                                let c1 =
                                                                                    biZExt64(e)(biCmp(e)(intPredicateUlt)(s)(x)("c1b"))("c1")
                                                                                in
                                                                                    let s2 =
                                                                                        biAdd(e)(s)(biLd(e)(carrySlot)("carry"))("s2")
                                                                                    in
                                                                                        let c2 =
                                                                                            biZExt64(e)(biCmp(e)(intPredicateUlt)(s2)(s)("c2b"))("c2")
                                                                                        in
                                                                                            Unit
                                                                                            |> (given (_) -> biStW(e)(outP)(i2)(s2))
                                                                                            |> (given (_) ->
                                                                                                biSt(e)(biAdd(e)(c1)(c2)("nc"))(carrySlot))
                                                                                            |> (given (_) ->
                                                                                                biSt(e)(biAdd(e)(i2)(biK(e)(1))("inc"))(iSlot))
                                                                                            |> (given (_) -> biBr(e)(loop)))
                                                                |> (given (_) -> biAt(e)(after))
                                                                |> (given (_) ->
                                                                    biCBr(e)(biCmp(e)(intPredicateNe)(biLd(e)(carrySlot)("cf"))(biK(e)(0))("has_c"))(carryBlock)(ret))
                                                                |> (given (_) -> biAt(e)(carryBlock))
                                                                |> (given (_) ->
                                                                    biAdd(e)(biLd(e)(nSlot)("n2"))(biK(e)(1))("n_inc"))
                                                                |> (given (nInc) ->
                                                                    Unit
                                                                    |> (given (_) -> biSt(e)(nInc)(nSlot))
                                                                    |> (given (_) ->
                                                                        "cc"
                                                                        |> biLd(e)(carrySlot)
                                                                        |> biStW(e)(outP)(nInc)))
                                                                |> (given (_) -> biBr(e)(ret))
                                                                |> (given (_) -> biAt(e)(ret))
                                                                |> (given (_) ->
                                                                    "nret"
                                                                    |> biLd(e)(nSlot)
                                                                    |> biRet(e)))

// `bi_sub_mag(a, na, b, nb, out)`: the magnitude difference for `|a| >= |b|`, answering `na`.
let emitBiSubMag (e: Bi) fn =
    (let a = biParam(fn)(0u32)
    in
        let na = biParam(fn)(1u32)
        in
            let b = biParam(fn)(2u32)
            in
                let nb = biParam(fn)(3u32)
                in
                    let outP = biParam(fn)(4u32)
                    in
                        let entry = biBlk(e)(fn)("entry")
                        in
                            let loop = biBlk(e)(fn)("loop")
                            in
                                let body = biBlk(e)(fn)("body")
                                in
                                    let ret = biBlk(e)(fn)("ret")
                                    in
                                        let _ = biAt(e)(entry)
                                        in
                                            let iSlot = biSlot(e)("i")
                                            in
                                                let borrowSlot = biSlot(e)("borrow")
                                                in
                                                    Unit
                                                    |> (given (_) ->
                                                        biSt(e)(biK(e)(1))(iSlot))
                                                    |> (given (_) ->
                                                        biSt(e)(biK(e)(0))(borrowSlot))
                                                    |> (given (_) -> biBr(e)(loop))
                                                    |> (given (_) -> biAt(e)(loop))
                                                    |> (given (_) ->
                                                        biCBr(e)(biCmp(e)(intPredicateSle)(biLd(e)(iSlot)("i"))(na)("i_le"))(body)(ret))
                                                    |> (given (_) -> biAt(e)(body))
                                                    |> (given (_) -> biLd(e)(iSlot)("iv"))
                                                    |> (given (i2) ->
                                                        let x = biLdW(e)(a)(i2)("x")
                                                        in
                                                            let y =
                                                                biSel(e)(biCmp(e)(intPredicateSle)(i2)(nb)("y_in"))(biLdW(e)(b)(i2)("by"))(biK(e)(0))("y")
                                                            in
                                                                let d = biSub(e)(x)(y)("d")
                                                                in
                                                                    let b1 =
                                                                        biZExt64(e)(biCmp(e)(intPredicateUlt)(x)(y)("b1b"))("b1")
                                                                    in
                                                                        let borrow = biLd(e)(borrowSlot)("borrow")
                                                                        in
                                                                            let d2 = biSub(e)(d)(borrow)("d2")
                                                                            in
                                                                                let b2 =
                                                                                    biZExt64(e)(biCmp(e)(intPredicateUlt)(d)(borrow)("b2b"))("b2")
                                                                                in
                                                                                    Unit
                                                                                    |> (given (_) -> biStW(e)(outP)(i2)(d2))
                                                                                    |> (given (_) ->
                                                                                        biSt(e)(biAdd(e)(b1)(b2)("nb2"))(borrowSlot))
                                                                                    |> (given (_) ->
                                                                                        biSt(e)(biAdd(e)(i2)(biK(e)(1))("inc"))(iSlot))
                                                                                    |> (given (_) -> biBr(e)(loop)))
                                                    |> (given (_) -> biAt(e)(ret))
                                                    |> (given (_) -> biRet(e)(na)))

// `bignum_from_i64(n, out)`: the header and single limb of a machine integer; the magnitude of
// the most negative value comes out of the wrapping negation.
let emitBiFromI64 (e: Bi) fn =
    (let n = biParam(fn)(0u32)
    in
        let outP = biParam(fn)(1u32)
        in
            let entry = biBlk(e)(fn)("entry")
            in
                let zero = biBlk(e)(fn)("zero")
                in
                    let nonZero = biBlk(e)(fn)("nonzero")
                    in
                        let endBlock = biBlk(e)(fn)("end")
                        in
                            Unit
                            |> (given (_) -> biAt(e)(entry))
                            |> (given (_) ->
                                biCBr(e)(biCmp(e)(intPredicateEq)(n)(biK(e)(0))("is0"))(zero)(nonZero))
                            |> (given (_) -> biAt(e)(zero))
                            |> (given (_) ->
                                0
                                |> biK(e)
                                |> biStW(e)(outP)(biK(e)(0)))
                            |> (given (_) -> biBr(e)(endBlock))
                            |> (given (_) -> biAt(e)(nonZero))
                            |> (given (_) ->
                                biCmp(e)(intPredicateSlt)(n)(biK(e)(0))("is_neg"))
                            |> (given (isNeg) ->
                                Unit
                                |> (given (_) ->
                                    "hdr"
                                    |> biOr(e)(biShl(e)(biSel(e)(isNeg)(biK(e)(1))(biK(e)(0))("neg"))(biK(e)(32))("nsh"))(biK(e)(1))
                                    |> biStW(e)(outP)(biK(e)(0)))
                                |> (given (_) ->
                                    "mag"
                                    |> biSel(e)(isNeg)(biSub(e)(biK(e)(0))(n)("negn"))(n)
                                    |> biStW(e)(outP)(biK(e)(1))))
                            |> (given (_) -> biBr(e)(endBlock))
                            |> (given (_) -> biAt(e)(endBlock))
                            |> (given (_) -> biRetVoid(e)))

// `bignum_cmp(a, b)`: -1, 0, or 1 by sign then magnitude.
let emitBiCmp (e: Bi) (runtime: BigIntRuntime) fn =
    (let a = biParam(fn)(0u32)
    in
        let b = biParam(fn)(1u32)
        in
            let entry = biBlk(e)(fn)("entry")
            in
                let diffSign = biBlk(e)(fn)("diff_sign")
                in
                    let sameSign = biBlk(e)(fn)("same_sign")
                    in
                        let retNeg = biBlk(e)(fn)("ret_neg")
                        in
                            let retPos = biBlk(e)(fn)("ret_pos")
                            in
                                let _ = biAt(e)(entry)
                                in
                                    let na = biCount(e)(a)("na")
                                    in
                                        let nb = biCount(e)(b)("nb")
                                        in
                                            let sa =
                                                biSel(e)(biCmp(e)(intPredicateNe)(na)(biK(e)(0))("na_nz"))(biNeg(e)(a)("sa0"))(biK(e)(0))("sa")
                                            in
                                                let sb =
                                                    biSel(e)(biCmp(e)(intPredicateNe)(nb)(biK(e)(0))("nb_nz"))(biNeg(e)(b)("sb0"))(biK(e)(0))("sb")
                                                in
                                                    Unit
                                                    |> (given (_) ->
                                                        biCBr(e)(biCmp(e)(intPredicateEq)(sa)(sb)("sign_eq"))(sameSign)(diffSign))
                                                    |> (given (_) -> biAt(e)(diffSign))
                                                    |> (given (_) ->
                                                        biCBr(e)(biCmp(e)(intPredicateNe)(sa)(biK(e)(0))("a_neg"))(retNeg)(retPos))
                                                    |> (given (_) -> biAt(e)(sameSign))
                                                    |> (given (_) -> biCallCmpMag(e)(runtime)(a)(na)(b)(nb)("c"))
                                                    |> (given (c) ->
                                                        "res"
                                                        |> biSel(e)(biCmp(e)(intPredicateNe)(sa)(biK(e)(0))("both_neg"))(biSub(e)(biK(e)(0))(c)("negc"))(c)
                                                        |> biRet(e))
                                                    |> (given (_) -> biAt(e)(retNeg))
                                                    |> (given (_) ->
                                                        -1
                                                        |> biK(e)
                                                        |> biRet(e))
                                                    |> (given (_) -> biAt(e)(retPos))
                                                    |> (given (_) ->
                                                        1
                                                        |> biK(e)
                                                        |> biRet(e)))

// Copies `src[1..n]` into `out[1..n]` and normalizes `out` under `sign`; the builder ends at a
// fresh after block.
let emitBiCopyMag (e: Bi) (runtime: BigIntRuntime) fn src n sign outP (tag: Str) =
    (let pre = biBlk(e)(fn)(tag + "_pre")
    in
        let loop = biBlk(e)(fn)(tag + "_loop")
        in
            let body = biBlk(e)(fn)(tag + "_body")
            in
                let after = biBlk(e)(fn)(tag + "_after")
                in
                    Unit
                    |> (given (_) -> biBr(e)(pre))
                    |> (given (_) -> biAt(e)(pre))
                    |> (given (_) -> biSlot(e)(tag + "_i"))
                    |> (given (iSlot) ->
                        Unit
                        |> (given (_) ->
                            biSt(e)(biK(e)(1))(iSlot))
                        |> (given (_) -> biBr(e)(loop))
                        |> (given (_) -> biAt(e)(loop))
                        |> (given (_) ->
                            biCBr(e)(biCmp(e)(intPredicateSle)(biLd(e)(iSlot)("i"))(n)("i_le"))(body)(after))
                        |> (given (_) -> biAt(e)(body))
                        |> (given (_) -> biLd(e)(iSlot)("iv"))
                        |> (given (i2) ->
                            Unit
                            |> (given (_) ->
                                "cw"
                                |> biLdW(e)(src)(i2)
                                |> biStW(e)(outP)(i2))
                            |> (given (_) ->
                                biSt(e)(biAdd(e)(i2)(biK(e)(1))("inc"))(iSlot)))
                        |> (given (_) -> biBr(e)(loop)))
                    |> (given (_) -> biAt(e)(after))
                    |> (given (_) -> biCallNormalize(e)(runtime)(outP)(sign)(n)))

// The opposite-sign arm of `bignum_add`/`bignum_sub`: the smaller magnitude is subtracted from
// the larger under the larger's sign, equal magnitudes give zero.
let emitBiAddSubDiff (e: Bi) (runtime: BigIntRuntime) fn a b na nb sa sb outP diffSign endBlock =
    (let aBigger = biBlk(e)(fn)("a_bigger")
    in
        let subAB = biBlk(e)(fn)("sub_ab")
        in
            let subBA = biBlk(e)(fn)("sub_ba")
            in
                let eqZero = biBlk(e)(fn)("eq_zero")
                in
                    Unit
                    |> (given (_) -> biAt(e)(diffSign))
                    |> (given (_) -> biCallCmpMag(e)(runtime)(a)(na)(b)(nb)("c"))
                    |> (given (c) ->
                        Unit
                        |> (given (_) ->
                            biCBr(e)(biCmp(e)(intPredicateEq)(c)(biK(e)(0))("mag_eq"))(eqZero)(aBigger))
                        |> (given (_) -> biAt(e)(aBigger))
                        |> (given (_) ->
                            biCBr(e)(biCmp(e)(intPredicateSgt)(c)(biK(e)(0))("a_big"))(subAB)(subBA)))
                    |> (given (_) -> biAt(e)(subAB))
                    |> (given (_) -> biCallMag(e)(runtime)(runtime.subMagFn)(a)(na)(b)(nb)(outP)("sab"))
                    |> (given (s1) -> biCallNormalize(e)(runtime)(outP)(sa)(s1))
                    |> (given (_) -> biBr(e)(endBlock))
                    |> (given (_) -> biAt(e)(subBA))
                    |> (given (_) -> biCallMag(e)(runtime)(runtime.subMagFn)(b)(nb)(a)(na)(outP)("sba"))
                    |> (given (s2) -> biCallNormalize(e)(runtime)(outP)(sb)(s2))
                    |> (given (_) -> biBr(e)(endBlock))
                    |> (given (_) -> biAt(e)(eqZero))
                    |> (given (_) ->
                        0
                        |> biK(e)
                        |> biStW(e)(outP)(biK(e)(0)))
                    |> (given (_) -> biBr(e)(endBlock)))

// `bignum_add`/`bignum_sub(a, b, out)`: a subtraction flips `b`'s sign, a zero operand copies
// the other, like signs add magnitudes, unlike signs subtract them.
let emitBiAddSub (e: Bi) (runtime: BigIntRuntime) fn (isSub: Bool) =
    (let a = biParam(fn)(0u32)
    in
        let b = biParam(fn)(1u32)
        in
            let outP = biParam(fn)(2u32)
            in
                let entry = biBlk(e)(fn)("entry")
                in
                    let aZero = biBlk(e)(fn)("a_zero")
                    in
                        let chkBZero = biBlk(e)(fn)("chk_bzero")
                        in
                            let bZero = biBlk(e)(fn)("b_zero")
                            in
                                let mainBlock = biBlk(e)(fn)("main")
                                in
                                    let sameSign = biBlk(e)(fn)("same_sign")
                                    in
                                        let diffSign = biBlk(e)(fn)("diff_sign")
                                        in
                                            let endBlock = biBlk(e)(fn)("end")
                                            in
                                                let _ = biAt(e)(entry)
                                                in
                                                    let na = biCount(e)(a)("na")
                                                    in
                                                        let nb = biCount(e)(b)("nb")
                                                        in
                                                            let sa = biNeg(e)(a)("sa")
                                                            in
                                                                let sbRaw = biNeg(e)(b)("sb0")
                                                                in
                                                                    let sb =
                                                                        if isSub
                                                                        then
                                                                            biAnd(e)(biAdd(e)(sbRaw)(biK(e)(1))("flip"))(biK(e)(1))("sb")
                                                                        else sbRaw
                                                                    in
                                                                        Unit
                                                                        |> (given (_) ->
                                                                            biCBr(e)(biCmp(e)(intPredicateEq)(na)(biK(e)(0))("a_is0"))(aZero)(chkBZero))
                                                                        |> (given (_) -> biAt(e)(aZero))
                                                                        |> (given (_) -> emitBiCopyMag(e)(runtime)(fn)(b)(nb)(sb)(outP)("cpb"))
                                                                        |> (given (_) -> biBr(e)(endBlock))
                                                                        |> (given (_) -> biAt(e)(chkBZero))
                                                                        |> (given (_) ->
                                                                            biCBr(e)(biCmp(e)(intPredicateEq)(nb)(biK(e)(0))("b_is0"))(bZero)(mainBlock))
                                                                        |> (given (_) -> biAt(e)(bZero))
                                                                        |> (given (_) -> emitBiCopyMag(e)(runtime)(fn)(a)(na)(sa)(outP)("cpa"))
                                                                        |> (given (_) -> biBr(e)(endBlock))
                                                                        |> (given (_) -> biAt(e)(mainBlock))
                                                                        |> (given (_) ->
                                                                            biCBr(e)(biCmp(e)(intPredicateEq)(sa)(sb)("same"))(sameSign)(diffSign))
                                                                        |> (given (_) -> biAt(e)(sameSign))
                                                                        |> (given (_) -> biCallMag(e)(runtime)(runtime.addMagFn)(a)(na)(b)(nb)(outP)("add_n"))
                                                                        |> (given (addN) -> biCallNormalize(e)(runtime)(outP)(sa)(addN))
                                                                        |> (given (_) -> biBr(e)(endBlock))
                                                                        |> (given (_) -> emitBiAddSubDiff(e)(runtime)(fn)(a)(b)(na)(nb)(sa)(sb)(outP)(diffSign)(endBlock))
                                                                        |> (given (_) -> biAt(e)(endBlock))
                                                                        |> (given (_) -> biRetVoid(e)))

// The schoolbook loops of `bignum_mul`: for every limb of `a`, every limb of `b`, accumulating
// 128-bit partial products into `out` with a running carry.
let emitBiMulLoops (e: Bi) fn a b na nb outP iSlot jSlot carrySlot outerLoop outerBody innerLoop innerBody innerDone doneBlock =
    (let innerStep = biBlk(e)(fn)("inner_step")
    in
        Unit
        |> (given (_) -> biAt(e)(outerLoop))
        |> (given (_) ->
            biSt(e)(biK(e)(1))(iSlot))
        |> (given (_) -> biBr(e)(outerBody))
        |> (given (_) -> biAt(e)(outerBody))
        |> (given (_) ->
            biCBr(e)(biCmp(e)(intPredicateSle)(biLd(e)(iSlot)("i"))(na)("i_le"))(innerLoop)(doneBlock))
        |> (given (_) -> biAt(e)(innerLoop))
        |> (given (_) ->
            biSt(e)(biK(e)(1))(jSlot))
        |> (given (_) ->
            biSt(e)(biK(e)(0))(carrySlot))
        |> (given (_) -> biBr(e)(innerBody))
        |> (given (_) -> biAt(e)(innerBody))
        |> (given (_) -> biLd(e)(jSlot)("j"))
        |> (given (jv) ->
            Unit
            |> (given (_) ->
                biCBr(e)(biCmp(e)(intPredicateSle)(jv)(nb)("j_le"))(innerStep)(innerDone))
            |> (given (_) -> biAt(e)(innerStep))
            |> (given (_) -> biLd(e)(iSlot)("iv"))
            |> (given (iv) ->
                let idx =
                    biSub(e)(biAdd(e)(iv)(jv)("ipj"))(biK(e)(1))("idx")
                in
                    let p =
                        biAdd(e)(biAdd(e)(biMul(e)(biZExt128(e)(biLdW(e)(a)(iv)("ai"))("ai128"))(biZExt128(e)(biLdW(e)(b)(jv)("bj"))("bj128"))("prod"))(biZExt128(e)(biLdW(e)(outP)(idx)("cur"))("cur128"))("p1"))(biZExt128(e)(biLd(e)(carrySlot)("carry"))("carry128"))("p")
                    in
                        Unit
                        |> (given (_) ->
                            "plow"
                            |> biTrunc64(e)(p)
                            |> biStW(e)(outP)(idx))
                        |> (given (_) ->
                            biSt(e)(biTrunc64(e)(biLshr(e)(p)(biK128(e)(64))("phi"))("carry_next"))(carrySlot))
                        |> (given (_) ->
                            biSt(e)(biAdd(e)(jv)(biK(e)(1))("jinc"))(jSlot))
                        |> (given (_) -> biBr(e)(innerBody))))
        |> (given (_) -> biAt(e)(innerDone))
        |> (given (_) -> biLd(e)(iSlot)("ivd"))
        |> (given (ivd) ->
            Unit
            |> (given (_) ->
                "cend"
                |> biLd(e)(carrySlot)
                |> biStW(e)(outP)(biAdd(e)(ivd)(nb)("i_nb")))
            |> (given (_) ->
                biSt(e)(biAdd(e)(ivd)(biK(e)(1))("iinc"))(iSlot)))
        |> (given (_) -> biBr(e)(outerBody)))

// `bignum_mul(a, b, out)`: zero when either operand is, else the schoolbook product under the
// exclusive-or of the signs.
let emitBiMul (e: Bi) (runtime: BigIntRuntime) fn =
    (let a = biParam(fn)(0u32)
    in
        let b = biParam(fn)(1u32)
        in
            let outP = biParam(fn)(2u32)
            in
                let entry = biBlk(e)(fn)("entry")
                in
                    let zero = biBlk(e)(fn)("zero")
                    in
                        let initLoop = biBlk(e)(fn)("init_loop")
                        in
                            let initBody = biBlk(e)(fn)("init_body")
                            in
                                let outerLoop = biBlk(e)(fn)("outer")
                                in
                                    let outerBody = biBlk(e)(fn)("outer_body")
                                    in
                                        let innerLoop = biBlk(e)(fn)("inner")
                                        in
                                            let innerBody = biBlk(e)(fn)("inner_body")
                                            in
                                                let innerDone = biBlk(e)(fn)("inner_done")
                                                in
                                                    let doneBlock = biBlk(e)(fn)("done")
                                                    in
                                                        let endBlock = biBlk(e)(fn)("end")
                                                        in
                                                            let _ = biAt(e)(entry)
                                                            in
                                                                let na = biCount(e)(a)("na")
                                                                in
                                                                    let nb = biCount(e)(b)("nb")
                                                                    in
                                                                        let iSlot = biSlot(e)("i")
                                                                        in
                                                                            let jSlot = biSlot(e)("j")
                                                                            in
                                                                                let kSlot = biSlot(e)("k")
                                                                                in
                                                                                    let carrySlot = biSlot(e)("carry")
                                                                                    in
                                                                                        let total = biAdd(e)(na)(nb)("total")
                                                                                        in
                                                                                            let anyZero =
                                                                                                biOr(e)(biZExt64(e)(biCmp(e)(intPredicateEq)(na)(biK(e)(0))("na0"))("na0z"))(biZExt64(e)(biCmp(e)(intPredicateEq)(nb)(biK(e)(0))("nb0"))("nb0z"))("anyz")
                                                                                            in
                                                                                                Unit
                                                                                                |> (given (_) ->
                                                                                                    biSt(e)(biK(e)(1))(kSlot))
                                                                                                |> (given (_) ->
                                                                                                    biCBr(e)(biCmp(e)(intPredicateNe)(anyZero)(biK(e)(0))("is0"))(zero)(initLoop))
                                                                                                |> (given (_) -> biAt(e)(zero))
                                                                                                |> (given (_) ->
                                                                                                    0
                                                                                                    |> biK(e)
                                                                                                    |> biStW(e)(outP)(biK(e)(0)))
                                                                                                |> (given (_) -> biBr(e)(endBlock))
                                                                                                |> (given (_) -> biAt(e)(initLoop))
                                                                                                |> (given (_) ->
                                                                                                    biCBr(e)(biCmp(e)(intPredicateSle)(biLd(e)(kSlot)("k"))(total)("k_le"))(initBody)(outerLoop))
                                                                                                |> (given (_) -> biAt(e)(initBody))
                                                                                                |> (given (_) -> biLd(e)(kSlot)("kv"))
                                                                                                |> (given (kv) ->
                                                                                                    Unit
                                                                                                    |> (given (_) ->
                                                                                                        0
                                                                                                        |> biK(e)
                                                                                                        |> biStW(e)(outP)(kv))
                                                                                                    |> (given (_) ->
                                                                                                        biSt(e)(biAdd(e)(kv)(biK(e)(1))("kinc"))(kSlot)))
                                                                                                |> (given (_) -> biBr(e)(initLoop))
                                                                                                |> (given (_) -> emitBiMulLoops(e)(fn)(a)(b)(na)(nb)(outP)(iSlot)(jSlot)(carrySlot)(outerLoop)(outerBody)(innerLoop)(innerBody)(innerDone)(doneBlock))
                                                                                                |> (given (_) -> biAt(e)(doneBlock))
                                                                                                |> (given (_) ->
                                                                                                    biCallNormalize(e)(runtime)(outP)(biAnd(e)(biXor(e)(biNeg(e)(a)("san"))(biNeg(e)(b)("sbn"))("sx"))(biK(e)(1))("neg"))(total))
                                                                                                |> (given (_) -> biBr(e)(endBlock))
                                                                                                |> (given (_) -> biAt(e)(endBlock))
                                                                                                |> (given (_) -> biRetVoid(e)))

// The loop-mutable state of the long division.
type BiDivSlots =
    | divIdx: LLVMValueRef
    | divRn: LLVMValueRef
    | divJ: LLVMValueRef
    | divQhat: LLVMValueRef
    | divRhat: LLVMValueRef
    | divK: LLVMValueRef
    | divI: LLVMValueRef

// Zeroes `q[1..na]` (the quotient digits are stored as 32-bit halves, so the limb area starts
// cleared), then counts the 32-bit digits of both magnitudes: twice the limbs, one fewer when
// the top digit is zero; answers `(m, n)`.
let emitBiDivModCountDigits (e: Bi) fn a b q na nb (slots: BiDivSlots) shortDiv longDiv =
    (let qInitLoop = biBlk(e)(fn)("qinit_loop")
    in
        let qInitBody = biBlk(e)(fn)("qinit_body")
        in
            let countDigits = biBlk(e)(fn)("count_digits")
            in
                Unit
                |> (given (_) ->
                    biSt(e)(biK(e)(1))(slots.divIdx))
                |> (given (_) -> biBr(e)(qInitLoop))
                |> (given (_) -> biAt(e)(qInitLoop))
                |> (given (_) ->
                    biCBr(e)(biCmp(e)(intPredicateSle)(biLd(e)(slots.divIdx)("k"))(na)("k_le"))(qInitBody)(countDigits))
                |> (given (_) -> biAt(e)(qInitBody))
                |> (given (_) -> biLd(e)(slots.divIdx)("kv"))
                |> (given (kv) ->
                    Unit
                    |> (given (_) ->
                        0
                        |> biK(e)
                        |> biStW(e)(q)(kv))
                    |> (given (_) ->
                        biSt(e)(biAdd(e)(kv)(biK(e)(1))("kinc"))(slots.divIdx)))
                |> (given (_) -> biBr(e)(qInitLoop))
                |> (given (_) -> biAt(e)(countDigits))
                |> (given (_) ->
                    let m0 =
                        biMul(e)(na)(biK(e)(2))("m0")
                    in
                        let m =
                            biSel(e)(biCmp(e)(intPredicateEq)(biLdDig(e)(a)(biSub(e)(m0)(biK(e)(1))("m0_1"))(2)("m_top"))(biK(e)(0))("mtz"))(biSub(e)(m0)(biK(e)(1))("m_dec"))(m0)("m")
                        in
                            let n0 =
                                biMul(e)(nb)(biK(e)(2))("n0")
                            in
                                let n =
                                    biSel(e)(biCmp(e)(intPredicateEq)(biLdDig(e)(b)(biSub(e)(n0)(biK(e)(1))("n0_1"))(2)("n_top"))(biK(e)(0))("ntz"))(biSub(e)(n0)(biK(e)(1))("n_dec"))(n0)("n")
                                in
                                    Unit
                                    |> (given (_) ->
                                        biCBr(e)(biCmp(e)(intPredicateEq)(n)(biK(e)(1))("n_is_1"))(shortDiv)(longDiv))
                                    |> (given (_) -> (m, n))))

// Short division by a single 32-bit digit: one native divide per dividend digit, the
// remainder left in `r`'s first limb.
let emitBiDivModShort (e: Bi) fn a b q r m (slots: BiDivSlots) shortDiv finish =
    (let sdLoop = biBlk(e)(fn)("sd_loop")
    in
        let sdBody = biBlk(e)(fn)("sd_body")
        in
            let sdDone = biBlk(e)(fn)("sd_done")
            in
                Unit
                |> (given (_) -> biAt(e)(shortDiv))
                |> (given (_) ->
                    (biLdDig(e)(b)(biK(e)(0))(2)("v0"), biSlot(e)("sd_rem")))
                |> (given (divisor) ->
                    match divisor with
                        | (v0, remSlot) ->
                            Unit
                            |> (given (_) ->
                                biSt(e)(biK(e)(0))(remSlot))
                            |> (given (_) ->
                                biSt(e)(biSub(e)(m)(biK(e)(1))("sd_j0"))(slots.divIdx))
                            |> (given (_) -> biBr(e)(sdLoop))
                            |> (given (_) -> biAt(e)(sdLoop))
                            |> (given (_) ->
                                biCBr(e)(biCmp(e)(intPredicateSge)(biLd(e)(slots.divIdx)("sd_j"))(biK(e)(0))("sd_more"))(sdBody)(sdDone))
                            |> (given (_) -> biAt(e)(sdBody))
                            |> (given (_) -> biLd(e)(slots.divIdx)("sd_jv"))
                            |> (given (j) ->
                                let cur =
                                    biOr(e)(biShl(e)(biLd(e)(remSlot)("sd_r"))(biK(e)(32))("sd_rs"))(biLdDig(e)(a)(j)(2)("sd_u"))("sd_cur")
                                in
                                    Unit
                                    |> (given (_) ->
                                        "sd_qd"
                                        |> biUDiv(e)(cur)(v0)
                                        |> biStDig(e)(q)(j)(2))
                                    |> (given (_) ->
                                        biSt(e)(biURem(e)(cur)(v0)("sd_rr"))(remSlot))
                                    |> (given (_) ->
                                        biSt(e)(biSub(e)(j)(biK(e)(1))("sd_jd"))(slots.divIdx)))
                            |> (given (_) -> biBr(e)(sdLoop))
                            |> (given (_) -> biAt(e)(sdDone))
                            |> (given (_) -> biLd(e)(remSlot)("sd_remf"))
                            |> (given (remV) ->
                                Unit
                                |> (given (_) ->
                                    biStW(e)(r)(biK(e)(1))(remV))
                                |> (given (_) ->
                                    biSt(e)(biSel(e)(biCmp(e)(intPredicateEq)(remV)(biK(e)(0))("sd_rz"))(biK(e)(0))(biK(e)(1))("sd_rn"))(slots.divRn))))
                |> (given (_) -> biBr(e)(finish)))

// The normalizing shift: the count of leading zero bits of the divisor's top digit, so the
// shifted top digit has its high bit set; answers `(s, 32 - s)`.
let emitBiDivModLongNlz (e: Bi) fn b n longDiv =
    (let sSlot =
        Unit
        |> (given (_) -> biAt(e)(longDiv))
        |> (given (_) -> biSlot(e)("d_s"))
    in
        let tSlot = biSlot(e)("d_t")
        in
            let nlzLoop = biBlk(e)(fn)("nlz_loop")
            in
                let nlzBody = biBlk(e)(fn)("nlz_body")
                in
                    let nlzDone = biBlk(e)(fn)("nlz_done")
                    in
                        Unit
                        |> (given (_) ->
                            biSt(e)(biK(e)(0))(sSlot))
                        |> (given (_) ->
                            biSt(e)(biLdDig(e)(b)(biSub(e)(n)(biK(e)(1))("vt_i"))(2)("vt"))(tSlot))
                        |> (given (_) -> biBr(e)(nlzLoop))
                        |> (given (_) -> biAt(e)(nlzLoop))
                        |> (given (_) ->
                            biCBr(e)(biCmp(e)(intPredicateUlt)(biLd(e)(tSlot)("t"))(biK(e)(2147483648))("t_lo"))(nlzBody)(nlzDone))
                        |> (given (_) -> biAt(e)(nlzBody))
                        |> (given (_) ->
                            biSt(e)(biShl(e)(biLd(e)(tSlot)("t2"))(biK(e)(1))("t_shl"))(tSlot))
                        |> (given (_) ->
                            biSt(e)(biAdd(e)(biLd(e)(sSlot)("s2"))(biK(e)(1))("s_inc"))(sSlot))
                        |> (given (_) -> biBr(e)(nlzLoop))
                        |> (given (_) -> biAt(e)(nlzDone))
                        |> (given (_) -> biLd(e)(sSlot)("s"))
                        |> (given (s) ->
                            (s, biSub(e)(biK(e)(32))(s)("rs"))))

// Builds the normalized divisor at scratch digits `[0..n-1]` and the normalized dividend at
// `[n..n+m]`, each digit the shifted pair of source digits.
let emitBiDivModLongNorm (e: Bi) fn a b scratch m n s rs (slots: BiDivSlots) =
    (let vnLoop = biBlk(e)(fn)("vn_loop")
    in
        let vnBody = biBlk(e)(fn)("vn_body")
        in
            let vnDone = biBlk(e)(fn)("vn_done")
            in
                let unLoop = biBlk(e)(fn)("un_loop")
                in
                    let unBody = biBlk(e)(fn)("un_body")
                    in
                        let unDone = biBlk(e)(fn)("un_done")
                        in
                            Unit
                            |> (given (_) ->
                                biSt(e)(biSub(e)(n)(biK(e)(1))("vn_i0"))(slots.divIdx))
                            |> (given (_) -> biBr(e)(vnLoop))
                            |> (given (_) -> biAt(e)(vnLoop))
                            |> (given (_) ->
                                biCBr(e)(biCmp(e)(intPredicateSge)(biLd(e)(slots.divIdx)("vn_i"))(biK(e)(1))("vn_more"))(vnBody)(vnDone))
                            |> (given (_) -> biAt(e)(vnBody))
                            |> (given (_) -> biLd(e)(slots.divIdx)("vn_iv"))
                            |> (given (vi) ->
                                Unit
                                |> (given (_) ->
                                    "vn_val"
                                    |> biOr(e)(biShl(e)(biLdDig(e)(b)(vi)(2)("vn_hi"))(s)("vn_hs"))(biLshr(e)(biLdDig(e)(b)(biSub(e)(vi)(biK(e)(1))("vn_i1"))(2)("vn_lo"))(rs)("vn_ls"))
                                    |> biStDig(e)(scratch)(vi)(0))
                                |> (given (_) ->
                                    biSt(e)(biSub(e)(vi)(biK(e)(1))("vn_id"))(slots.divIdx)))
                            |> (given (_) -> biBr(e)(vnLoop))
                            |> (given (_) -> biAt(e)(vnDone))
                            |> (given (_) ->
                                "vn_b0s"
                                |> biShl(e)(biLdDig(e)(b)(biK(e)(0))(2)("vn_b0"))(s)
                                |> biStDig(e)(scratch)(biK(e)(0))(0))
                            |> (given (_) ->
                                "un_top"
                                |> biLshr(e)(biLdDig(e)(a)(biSub(e)(m)(biK(e)(1))("un_m1"))(2)("un_top_src"))(rs)
                                |> biStDig(e)(scratch)(biAdd(e)(n)(m)("un_top_i"))(0))
                            |> (given (_) ->
                                biSt(e)(biSub(e)(m)(biK(e)(1))("un_i0"))(slots.divIdx))
                            |> (given (_) -> biBr(e)(unLoop))
                            |> (given (_) -> biAt(e)(unLoop))
                            |> (given (_) ->
                                biCBr(e)(biCmp(e)(intPredicateSge)(biLd(e)(slots.divIdx)("un_i"))(biK(e)(1))("un_more"))(unBody)(unDone))
                            |> (given (_) -> biAt(e)(unBody))
                            |> (given (_) -> biLd(e)(slots.divIdx)("un_iv"))
                            |> (given (ui) ->
                                Unit
                                |> (given (_) ->
                                    "un_val"
                                    |> biOr(e)(biShl(e)(biLdDig(e)(a)(ui)(2)("un_hi"))(s)("un_hs"))(biLshr(e)(biLdDig(e)(a)(biSub(e)(ui)(biK(e)(1))("un_i1"))(2)("un_lo"))(rs)("un_ls"))
                                    |> biStDig(e)(scratch)(biAdd(e)(n)(ui)("un_di"))(0))
                                |> (given (_) ->
                                    biSt(e)(biSub(e)(ui)(biK(e)(1))("un_id"))(slots.divIdx)))
                            |> (given (_) -> biBr(e)(unLoop))
                            |> (given (_) -> biAt(e)(unDone))
                            |> (given (_) ->
                                "un_a0s"
                                |> biShl(e)(biLdDig(e)(a)(biK(e)(0))(2)("un_a0"))(s)
                                |> biStDig(e)(scratch)(n)(0)))

// The blocks of the quotient-digit loop.
type BiDivBlocks =
    | divJLoop: LLVMBasicBlockRef
    | divJBody: LLVMBasicBlockRef
    | divCorrCheck: LLVMBasicBlockRef
    | divCorrMul: LLVMBasicBlockRef
    | divCorrDec: LLVMBasicBlockRef
    | divMulSub: LLVMBasicBlockRef
    | divMsLoop: LLVMBasicBlockRef
    | divMsBody: LLVMBasicBlockRef
    | divMsDone: LLVMBasicBlockRef
    | divAddBack: LLVMBasicBlockRef
    | divAbLoop: LLVMBasicBlockRef
    | divAbBody: LLVMBasicBlockRef
    | divAbDone: LLVMBasicBlockRef
    | divJNext: LLVMBasicBlockRef
    | divDenorm: LLVMBasicBlockRef

let createBiDivBlocks (e: Bi) fn =
    BiDivBlocks(
        divJLoop = biBlk(e)(fn)("j_loop"),
        divJBody = biBlk(e)(fn)("j_body"),
        divCorrCheck = biBlk(e)(fn)("corr_check"),
        divCorrMul = biBlk(e)(fn)("corr_mul"),
        divCorrDec = biBlk(e)(fn)("corr_dec"),
        divMulSub = biBlk(e)(fn)("mul_sub"),
        divMsLoop = biBlk(e)(fn)("ms_loop"),
        divMsBody = biBlk(e)(fn)("ms_body"),
        divMsDone = biBlk(e)(fn)("ms_done"),
        divAddBack = biBlk(e)(fn)("add_back"),
        divAbLoop = biBlk(e)(fn)("ab_loop"),
        divAbBody = biBlk(e)(fn)("ab_body"),
        divAbDone = biBlk(e)(fn)("ab_done"),
        divJNext = biBlk(e)(fn)("j_next"),
        divDenorm = biBlk(e)(fn)("denorm")
    )

// The quotient-digit loop's head: `j` runs from `m - n` down to 0, and each digit starts from
// the estimate `qhat = (un[j+n] * B + un[j+n-1]) / vn[n-1]` with its remainder `rhat`. Answers
// the divisor's top two normalized digits.
let emitBiDivModLongMain (e: Bi) scratch m n (slots: BiDivSlots) (blocks: BiDivBlocks) =
    (let vtn =
        biLdDig(e)(scratch)(biSub(e)(n)(biK(e)(1))("vtn_i"))(0)("vtn")
    in
        let vtn2 =
            biLdDig(e)(scratch)(biSub(e)(n)(biK(e)(2))("vtn2_i"))(0)("vtn2")
        in
            Unit
            |> (given (_) ->
                biSt(e)(biSub(e)(m)(n)("j0"))(slots.divJ))
            |> (given (_) -> biBr(e)(blocks.divJLoop))
            |> (given (_) -> biAt(e)(blocks.divJLoop))
            |> (given (_) ->
                biCBr(e)(biCmp(e)(intPredicateSge)(biLd(e)(slots.divJ)("j"))(biK(e)(0))("j_more"))(blocks.divJBody)(blocks.divDenorm))
            |> (given (_) -> biAt(e)(blocks.divJBody))
            |> (given (_) -> biLd(e)(slots.divJ)("jv"))
            |> (given (jv) ->
                let num =
                    biOr(e)(biShl(e)(biLdDig(e)(scratch)(biAdd(e)(n)(biAdd(e)(jv)(n)("jn"))("jn_i"))(0)("num_hi"))(biK(e)(32))("num_hs"))(biLdDig(e)(scratch)(biAdd(e)(n)(biSub(e)(biAdd(e)(jv)(n)("jn2"))(biK(e)(1))("jn_1"))("jn1_i"))(0)("num_lo"))("num")
                in
                    Unit
                    |> (given (_) ->
                        biSt(e)(biUDiv(e)(num)(vtn)("qhat0"))(slots.divQhat))
                    |> (given (_) ->
                        biSt(e)(biURem(e)(num)(vtn)("rhat0"))(slots.divRhat)))
            |> (given (_) -> biBr(e)(blocks.divCorrCheck))
            |> (given (_) -> (vtn, vtn2)))

// Corrects the estimate down, at most twice: while `qhat` reaches the base or
// `qhat * vn[n-2]` overshoots `rhat * B + un[j+n-2]`; the product is only formed once `qhat`
// is below the base so it cannot overflow.
let emitBiDivModLongCorr (e: Bi) scratch n vtn vtn2 (slots: BiDivSlots) (blocks: BiDivBlocks) =
    (let big = biK(e)(4294967296)
    in
        Unit
        |> (given (_) -> biAt(e)(blocks.divCorrCheck))
        |> (given (_) ->
            biCBr(e)(biCmp(e)(intPredicateUge)(biLd(e)(slots.divQhat)("qh_c"))(big)("qh_big"))(blocks.divCorrDec)(blocks.divCorrMul))
        |> (given (_) -> biAt(e)(blocks.divCorrMul))
        |> (given (_) ->
            let prod =
                biMul(e)(biLd(e)(slots.divQhat)("qh_m"))(vtn2)("corr_prod")
            in
                let rhs =
                    biOr(e)(biShl(e)(biLd(e)(slots.divRhat)("rh_m"))(biK(e)(32))("rh_s"))(biLdDig(e)(scratch)(biAdd(e)(n)(biSub(e)(biAdd(e)(biLd(e)(slots.divJ)("j_m"))(n)("jn3"))(biK(e)(2))("jn_2"))("jn2_i"))(0)("un_jn2"))("corr_rhs")
                in
                    biCBr(e)(biCmp(e)(intPredicateUgt)(prod)(rhs)("overshoot"))(blocks.divCorrDec)(blocks.divMulSub))
        |> (given (_) -> biAt(e)(blocks.divCorrDec))
        |> (given (_) ->
            biSt(e)(biSub(e)(biLd(e)(slots.divQhat)("qh_d"))(biK(e)(1))("qh_dec"))(slots.divQhat))
        |> (given (_) ->
            biAdd(e)(biLd(e)(slots.divRhat)("rh_d"))(vtn)("rh_inc"))
        |> (given (rhNew) ->
            Unit
            |> (given (_) -> biSt(e)(rhNew)(slots.divRhat))
            |> (given (_) ->
                biCBr(e)(biCmp(e)(intPredicateUlt)(rhNew)(big)("rh_small"))(blocks.divCorrCheck)(blocks.divMulSub))))

// Multiplies the digit estimate into the divisor and subtracts it from `un[j..j+n]`, tracking
// a signed borrow; a negative top digit means the estimate was one too large.
let emitBiDivModLongMulSub (e: Bi) q scratch n (slots: BiDivSlots) (blocks: BiDivBlocks) =
    Unit
    |> (given (_) -> biAt(e)(blocks.divMulSub))
    |> (given (_) ->
        biSt(e)(biK(e)(0))(slots.divK))
    |> (given (_) ->
        biSt(e)(biK(e)(0))(slots.divI))
    |> (given (_) -> biBr(e)(blocks.divMsLoop))
    |> (given (_) -> biAt(e)(blocks.divMsLoop))
    |> (given (_) ->
        biCBr(e)(biCmp(e)(intPredicateSlt)(biLd(e)(slots.divI)("ms_i"))(n)("ms_more"))(blocks.divMsBody)(blocks.divMsDone))
    |> (given (_) -> biAt(e)(blocks.divMsBody))
    |> (given (_) -> biLd(e)(slots.divI)("ms_iv"))
    |> (given (msI) ->
        let p =
            biMul(e)(biLd(e)(slots.divQhat)("ms_qh"))(biLdDig(e)(scratch)(msI)(0)("ms_vn"))("ms_p")
        in
            let unIj =
                biAdd(e)(n)(biAdd(e)(msI)(biLd(e)(slots.divJ)("ms_j"))("ms_ij"))("ms_ij_i")
            in
                let t =
                    biSub(e)(biSub(e)(biLdDig(e)(scratch)(unIj)(0)("ms_un"))(biLd(e)(slots.divK)("ms_k"))("ms_t1"))(biAnd(e)(p)(biK(e)(4294967295))("ms_pl"))("ms_t")
                in
                    Unit
                    |> (given (_) -> biStDig(e)(scratch)(unIj)(0)(t))
                    |> (given (_) ->
                        biSt(e)(biSub(e)(biLshr(e)(p)(biK(e)(32))("ms_ph"))(biAshr(e)(t)(biK(e)(32))("ms_ts"))("ms_k2"))(slots.divK))
                    |> (given (_) ->
                        biSt(e)(biAdd(e)(msI)(biK(e)(1))("ms_inc"))(slots.divI)))
    |> (given (_) -> biBr(e)(blocks.divMsLoop))
    |> (given (_) -> biAt(e)(blocks.divMsDone))
    |> (given (_) -> biLd(e)(slots.divJ)("msd_j"))
    |> (given (jvd) ->
        let unJn =
            biAdd(e)(n)(biAdd(e)(jvd)(n)("msd_jn"))("msd_jn_i")
        in
            let t2 =
                biSub(e)(biLdDig(e)(scratch)(unJn)(0)("msd_un"))(biLd(e)(slots.divK)("msd_k"))("msd_t2")
            in
                Unit
                |> (given (_) -> biStDig(e)(scratch)(unJn)(0)(t2))
                |> (given (_) ->
                    "msd_qh"
                    |> biLd(e)(slots.divQhat)
                    |> biStDig(e)(q)(jvd)(2))
                |> (given (_) ->
                    biCBr(e)(biCmp(e)(intPredicateSlt)(t2)(biK(e)(0))("underflow"))(blocks.divAddBack)(blocks.divJNext)))

// The rare add-back when the estimate was one too large: the quotient digit is decremented
// and the divisor added back into `un[j..j+n]`; then the digit loop advances.
let emitBiDivModLongAddBack (e: Bi) q scratch n (slots: BiDivSlots) (blocks: BiDivBlocks) =
    Unit
    |> (given (_) -> biAt(e)(blocks.divAddBack))
    |> (given (_) ->
        "ab_dec"
        |> biSub(e)(biLd(e)(slots.divQhat)("ab_qh"))(biK(e)(1))
        |> biStDig(e)(q)(biLd(e)(slots.divJ)("ab_j"))(2))
    |> (given (_) ->
        biSt(e)(biK(e)(0))(slots.divK))
    |> (given (_) ->
        biSt(e)(biK(e)(0))(slots.divI))
    |> (given (_) -> biBr(e)(blocks.divAbLoop))
    |> (given (_) -> biAt(e)(blocks.divAbLoop))
    |> (given (_) ->
        biCBr(e)(biCmp(e)(intPredicateSlt)(biLd(e)(slots.divI)("ab_i"))(n)("ab_more"))(blocks.divAbBody)(blocks.divAbDone))
    |> (given (_) -> biAt(e)(blocks.divAbBody))
    |> (given (_) -> biLd(e)(slots.divI)("ab_iv"))
    |> (given (abI) ->
        let abIj =
            biAdd(e)(n)(biAdd(e)(abI)(biLd(e)(slots.divJ)("ab_j2"))("ab_ij"))("ab_ij_i")
        in
            let t3 =
                biAdd(e)(biAdd(e)(biLdDig(e)(scratch)(abIj)(0)("ab_un"))(biLdDig(e)(scratch)(abI)(0)("ab_vn"))("ab_s1"))(biLd(e)(slots.divK)("ab_k"))("ab_t3")
            in
                Unit
                |> (given (_) -> biStDig(e)(scratch)(abIj)(0)(t3))
                |> (given (_) ->
                    biSt(e)(biLshr(e)(t3)(biK(e)(32))("ab_carry"))(slots.divK))
                |> (given (_) ->
                    biSt(e)(biAdd(e)(abI)(biK(e)(1))("ab_inc"))(slots.divI)))
    |> (given (_) -> biBr(e)(blocks.divAbLoop))
    |> (given (_) -> biAt(e)(blocks.divAbDone))
    |> (given (_) ->
        biAdd(e)(n)(biAdd(e)(biLd(e)(slots.divJ)("ab_j3"))(n)("ab_jn"))("ab_jn_i"))
    |> (given (abJn) ->
        "ab_top2"
        |> biAdd(e)(biLdDig(e)(scratch)(abJn)(0)("ab_top"))(biLd(e)(slots.divK)("ab_kf"))
        |> biStDig(e)(scratch)(abJn)(0))
    |> (given (_) -> biBr(e)(blocks.divJNext))
    |> (given (_) -> biAt(e)(blocks.divJNext))
    |> (given (_) ->
        biSt(e)(biSub(e)(biLd(e)(slots.divJ)("j_d"))(biK(e)(1))("j_dec"))(slots.divJ))
    |> (given (_) -> biBr(e)(blocks.divJLoop))

// The remainder: `un[0..n-1]` shifted back by `s` into `r`'s digits, the unwritten top half
// of an odd digit count zeroed, and the remainder's limb count recorded.
let emitBiDivModLongDenorm (e: Bi) fn r scratch n s rs (slots: BiDivSlots) denorm finish =
    (let dnLoop = biBlk(e)(fn)("dn_loop")
    in
        let dnBody = biBlk(e)(fn)("dn_body")
        in
            let dnDone = biBlk(e)(fn)("dn_done")
            in
                let padBlock = biBlk(e)(fn)("dn_pad")
                in
                    let rnBlock = biBlk(e)(fn)("dn_rn")
                    in
                        Unit
                        |> (given (_) -> biAt(e)(denorm))
                        |> (given (_) ->
                            biSt(e)(biK(e)(0))(slots.divIdx))
                        |> (given (_) -> biBr(e)(dnLoop))
                        |> (given (_) -> biAt(e)(dnLoop))
                        |> (given (_) ->
                            biCBr(e)(biCmp(e)(intPredicateSlt)(biLd(e)(slots.divIdx)("dn_i"))(n)("dn_more"))(dnBody)(dnDone))
                        |> (given (_) -> biAt(e)(dnBody))
                        |> (given (_) -> biLd(e)(slots.divIdx)("dn_iv"))
                        |> (given (di) ->
                            Unit
                            |> (given (_) ->
                                "dn_val"
                                |> biOr(e)(biLshr(e)(biLdDig(e)(scratch)(biAdd(e)(n)(di)("dn_di"))(0)("dn_lo"))(s)("dn_lsh"))(biShl(e)(biLdDig(e)(scratch)(biAdd(e)(n)(biAdd(e)(di)(biK(e)(1))("dn_i1"))("dn_di1"))(0)("dn_hi"))(rs)("dn_hsh"))
                                |> biStDig(e)(r)(di)(2))
                            |> (given (_) ->
                                biSt(e)(biAdd(e)(di)(biK(e)(1))("dn_inc"))(slots.divIdx)))
                        |> (given (_) -> biBr(e)(dnLoop))
                        |> (given (_) -> biAt(e)(dnDone))
                        |> (given (_) ->
                            biCBr(e)(biCmp(e)(intPredicateNe)(biAnd(e)(n)(biK(e)(1))("n_odd"))(biK(e)(0))("is_odd"))(padBlock)(rnBlock))
                        |> (given (_) -> biAt(e)(padBlock))
                        |> (given (_) ->
                            0
                            |> biK(e)
                            |> biStDig(e)(r)(n)(2))
                        |> (given (_) -> biBr(e)(rnBlock))
                        |> (given (_) -> biAt(e)(rnBlock))
                        |> (given (_) ->
                            biSt(e)(biLshr(e)(biAdd(e)(n)(biK(e)(1))("n_1"))(biK(e)(1))("rn_limbs"))(slots.divRn))
                        |> (given (_) -> biBr(e)(finish)))

// Knuth's Algorithm D proper for a divisor of two or more digits.
let emitBiDivModLong (e: Bi) fn a b q r scratch m n (slots: BiDivSlots) longDiv finish =
    match emitBiDivModLongNlz(e)(fn)(b)(n)(longDiv) with
        | (s, rs) ->
            let blocks =
                Unit
                |> (given (_) -> emitBiDivModLongNorm(e)(fn)(a)(b)(scratch)(m)(n)(s)(rs)(slots))
                |> (given (_) -> createBiDivBlocks(e)(fn))
            in
                match emitBiDivModLongMain(e)(scratch)(m)(n)(slots)(blocks) with
                    | (vtn, vtn2) ->
                        Unit
                        |> (given (_) -> emitBiDivModLongCorr(e)(scratch)(n)(vtn)(vtn2)(slots)(blocks))
                        |> (given (_) -> emitBiDivModLongMulSub(e)(q)(scratch)(n)(slots)(blocks))
                        |> (given (_) -> emitBiDivModLongAddBack(e)(q)(scratch)(n)(slots)(blocks))
                        |> (given (_) -> emitBiDivModLongDenorm(e)(fn)(r)(scratch)(n)(s)(rs)(slots)(blocks.divDenorm)(finish))

// The non-trivial division: the quotient limbs cleared, the digit counts taken, the short or
// long division run, then `q` normalized under the exclusive-or of the signs and `r` under the
// dividend's sign.
let emitBiDivModProceed (e: Bi) (runtime: BigIntRuntime) fn a b q r scratch na nb =
    (let slots =
        BiDivSlots(
            divIdx = biSlot(e)("idx"),
            divRn = biSlot(e)("rn"),
            divJ = biSlot(e)("d_j"),
            divQhat = biSlot(e)("d_qhat"),
            divRhat = biSlot(e)("d_rhat"),
            divK = biSlot(e)("d_k"),
            divI = biSlot(e)("d_i")
        )
    in
        let shortDiv = biBlk(e)(fn)("short_div")
        in
            let longDiv = biBlk(e)(fn)("long_div")
            in
                let finish = biBlk(e)(fn)("finish")
                in
                    match emitBiDivModCountDigits(e)(fn)(a)(b)(q)(na)(nb)(slots)(shortDiv)(longDiv) with
                        | (m, n) ->
                            Unit
                            |> (given (_) -> emitBiDivModShort(e)(fn)(a)(b)(q)(r)(m)(slots)(shortDiv)(finish))
                            |> (given (_) -> emitBiDivModLong(e)(fn)(a)(b)(q)(r)(scratch)(m)(n)(slots)(longDiv)(finish))
                            |> (given (_) -> biAt(e)(finish))
                            |> (given (_) ->
                                biCallNormalize(e)(runtime)(q)(biAnd(e)(biXor(e)(biNeg(e)(a)("san"))(biNeg(e)(b)("sbn"))("qx"))(biK(e)(1))("qneg"))(na))
                            |> (given (_) ->
                                "rnf"
                                |> biLd(e)(slots.divRn)
                                |> biCallNormalize(e)(runtime)(r)(biNeg(e)(a)("ran")))
                            |> (given (_) -> biRetVoid(e)))

// `bignum_divmod(a, b, q, r, scratch)`, truncated toward zero: a zero divisor, a zero dividend,
// or a dividend smaller than the divisor gives quotient zero (the remainder the dividend
// itself in the last case); otherwise the quotient limbs are cleared, the digit counts taken,
// and the short or long division run, before `q` is normalized under the exclusive-or of the
// signs and `r` under the dividend's sign.
let emitBiDivMod (e: Bi) (runtime: BigIntRuntime) fn =
    (let a = biParam(fn)(0u32)
    in
        let b = biParam(fn)(1u32)
        in
            let q = biParam(fn)(2u32)
            in
                let r = biParam(fn)(3u32)
                in
                    let scratch = biParam(fn)(4u32)
                    in
                        let entry = biBlk(e)(fn)("entry")
                        in
                            let trivial = biBlk(e)(fn)("trivial")
                            in
                                let copyR = biBlk(e)(fn)("copy_r")
                                in
                                    let zeroR = biBlk(e)(fn)("zero_r")
                                    in
                                        let proceed = biBlk(e)(fn)("proceed")
                                        in
                                            let _ = biAt(e)(entry)
                                            in
                                                let na = biCount(e)(a)("na")
                                                in
                                                    let nb = biCount(e)(b)("nb")
                                                    in
                                                        let degenerate =
                                                            biOr(e)(biZExt64(e)(biCmp(e)(intPredicateEq)(nb)(biK(e)(0))("nb0"))("z1"))(biZExt64(e)(biCmp(e)(intPredicateEq)(na)(biK(e)(0))("na0"))("z2"))("degen")
                                                        in
                                                            let aSmaller =
                                                                biZExt64(e)(biCmp(e)(intPredicateSlt)(biCallCmpMag(e)(runtime)(a)(na)(b)(nb)("cmp"))(biK(e)(0))("a_lt"))("als")
                                                            in
                                                                Unit
                                                                |> (given (_) ->
                                                                    biCBr(e)(biCmp(e)(intPredicateNe)(biOr(e)(degenerate)(aSmaller)("triv"))(biK(e)(0))("is_triv"))(trivial)(proceed))
                                                                |> (given (_) -> biAt(e)(trivial))
                                                                |> (given (_) ->
                                                                    0
                                                                    |> biK(e)
                                                                    |> biStW(e)(q)(biK(e)(0)))
                                                                |> (given (_) ->
                                                                    biCBr(e)(biCmp(e)(intPredicateNe)(biAnd(e)(biZExt64(e)(biCmp(e)(intPredicateEq)(degenerate)(biK(e)(0))("not_degen"))("nd"))(aSmaller)("rem_is_a"))(biK(e)(0))("rema"))(copyR)(zeroR))
                                                                |> (given (_) -> biAt(e)(copyR))
                                                                |> (given (_) ->
                                                                    emitBiCopyMag(e)(runtime)(fn)(a)(na)(biNeg(e)(a)("ra_sign"))(r)("cpr"))
                                                                |> (given (_) -> biRetVoid(e))
                                                                |> (given (_) -> biAt(e)(zeroR))
                                                                |> (given (_) ->
                                                                    0
                                                                    |> biK(e)
                                                                    |> biStW(e)(r)(biK(e)(0)))
                                                                |> (given (_) -> biRetVoid(e))
                                                                |> (given (_) -> biAt(e)(proceed))
                                                                |> (given (_) -> emitBiDivModProceed(e)(runtime)(fn)(a)(b)(q)(r)(scratch)(na)(nb)))

// One limb of the decimal loop's division by ten: the high and low 32-bit halves in turn,
// each dividend the carried remainder above the half.
let emitBiToDecimalLimbDiv (e: Bi) scratch remSlot liSlot limbBody limbLoop =
    Unit
    |> (given (_) -> biAt(e)(limbBody))
    |> (given (_) -> biLd(e)(liSlot)("liv"))
    |> (given (li) ->
        let limb = biLdW(e)(scratch)(li)("limb")
        in
            let curH =
                biOr(e)(biShl(e)(biLd(e)(remSlot)("rem0"))(biK(e)(32))("rhs"))(biLshr(e)(limb)(biK(e)(32))("hi"))("curH")
            in
                let qH =
                    biUDiv(e)(curH)(biK(e)(10))("qH")
                in
                    let curL =
                        biOr(e)(biShl(e)(biSub(e)(curH)(biMul(e)(qH)(biK(e)(10))("qh10"))("remH"))(biK(e)(32))("rls"))(biAnd(e)(limb)(biK(e)(4294967295))("lo"))("curL")
                    in
                        let qL =
                            biUDiv(e)(curL)(biK(e)(10))("qL")
                        in
                            Unit
                            |> (given (_) ->
                                "qcombine"
                                |> biOr(e)(biShl(e)(qH)(biK(e)(32))("qhs"))(qL)
                                |> biStW(e)(scratch)(li))
                            |> (given (_) ->
                                biSt(e)(biSub(e)(curL)(biMul(e)(qL)(biK(e)(10))("ql10"))("remL"))(remSlot))
                            |> (given (_) ->
                                biSt(e)(biSub(e)(li)(biK(e)(1))("lidec"))(liSlot)))
    |> (given (_) -> biBr(e)(limbLoop))

// The digit loop of `bignum_to_decimal`: the scratch magnitude is divided by ten until it is
// zero, each remainder appended as an ASCII digit, least significant first.
let emitBiToDecimalLoop (e: Bi) fn scratch snSlot lenSlot na bufPtr exitBlock =
    (let pre = biBlk(e)(fn)("dl_pre")
    in
        let loop = biBlk(e)(fn)("dl_loop")
        in
            let body = biBlk(e)(fn)("dl_body")
            in
                let limbLoop = biBlk(e)(fn)("dl_limb")
                in
                    let limbBody = biBlk(e)(fn)("dl_limb_body")
                    in
                        let emitBlock = biBlk(e)(fn)("dl_emit")
                        in
                            Unit
                            |> (given (_) -> biBr(e)(pre))
                            |> (given (_) -> biAt(e)(pre))
                            |> (given (_) -> biSt(e)(na)(snSlot))
                            |> (given (_) ->
                                biSt(e)(biK(e)(0))(lenSlot))
                            |> (given (_) -> (biSlot(e)("rem"), biSlot(e)("li")))
                            |> (given (loopSlots) ->
                                match loopSlots with
                                    | (remSlot, liSlot) ->
                                        Unit
                                        |> (given (_) -> biBr(e)(loop))
                                        |> (given (_) -> biAt(e)(loop))
                                        |> (given (_) ->
                                            biCBr(e)(biCmp(e)(intPredicateSgt)(biLd(e)(snSlot)("sn"))(biK(e)(0))("sn_pos"))(body)(exitBlock))
                                        |> (given (_) -> biAt(e)(body))
                                        |> (given (_) ->
                                            biSt(e)(biK(e)(0))(remSlot))
                                        |> (given (_) ->
                                            biSt(e)(biLd(e)(snSlot)("sn0"))(liSlot))
                                        |> (given (_) -> biBr(e)(limbLoop))
                                        |> (given (_) -> biAt(e)(limbLoop))
                                        |> (given (_) ->
                                            biCBr(e)(biCmp(e)(intPredicateSge)(biLd(e)(liSlot)("li"))(biK(e)(1))("li_ge1"))(limbBody)(emitBlock))
                                        |> (given (_) -> emitBiToDecimalLimbDiv(e)(scratch)(remSlot)(liSlot)(limbBody)(limbLoop))
                                        |> (given (_) -> biAt(e)(emitBlock))
                                        |> (given (_) -> emitBiStripZeros(e)(fn)(scratch)(snSlot)("dlstrip"))
                                        |> (given (_) -> biLd(e)(lenSlot)("len"))
                                        |> (given (len) ->
                                            Unit
                                            |> (given (_) ->
                                                biStByte(e)(bufPtr)(len)(biAdd(e)(biLd(e)(remSlot)("remf"))(biK(e)(48))("digit"))("digp"))
                                            |> (given (_) ->
                                                biSt(e)(biAdd(e)(len)(biK(e)(1))("leninc"))(lenSlot)))
                                        |> (given (_) -> biBr(e)(loop))))

// Reverses `buf[0..len-1]` in place; the builder ends at a fresh done block.
let emitBiReverse (e: Bi) fn bufPtr len =
    (let pre = biBlk(e)(fn)("rev_pre")
    in
        let loop = biBlk(e)(fn)("rev_loop")
        in
            let body = biBlk(e)(fn)("rev_body")
            in
                let doneBlock = biBlk(e)(fn)("rev_done")
                in
                    Unit
                    |> (given (_) -> biBr(e)(pre))
                    |> (given (_) -> biAt(e)(pre))
                    |> (given (_) -> (biSlot(e)("ri"), biSlot(e)("rj")))
                    |> (given (revSlots) ->
                        match revSlots with
                            | (iSlot, jSlot) ->
                                Unit
                                |> (given (_) ->
                                    biSt(e)(biK(e)(0))(iSlot))
                                |> (given (_) ->
                                    biSt(e)(biSub(e)(len)(biK(e)(1))("len_1"))(jSlot))
                                |> (given (_) -> biBr(e)(loop))
                                |> (given (_) -> biAt(e)(loop))
                                |> (given (_) ->
                                    biCBr(e)(biCmp(e)(intPredicateSlt)(biLd(e)(iSlot)("i"))(biLd(e)(jSlot)("j"))("i_lt_j"))(body)(doneBlock))
                                |> (given (_) -> biAt(e)(body))
                                |> (given (_) -> (biLd(e)(iSlot)("iv"), biLd(e)(jSlot)("jv")))
                                |> (given (indexes) ->
                                    match indexes with
                                        | (i, j) ->
                                            let ci = biLdByte(e)(bufPtr)(i)("ci")
                                            in
                                                let cj = biLdByte(e)(bufPtr)(j)("cj")
                                                in
                                                    Unit
                                                    |> (given (_) -> biStByte(e)(bufPtr)(i)(cj)("pi"))
                                                    |> (given (_) -> biStByte(e)(bufPtr)(j)(ci)("pj"))
                                                    |> (given (_) ->
                                                        biSt(e)(biAdd(e)(i)(biK(e)(1))("iinc"))(iSlot))
                                                    |> (given (_) ->
                                                        biSt(e)(biSub(e)(j)(biK(e)(1))("jdec"))(jSlot)))
                                |> (given (_) -> biBr(e)(loop)))
                    |> (given (_) -> biAt(e)(doneBlock)))

// `bignum_to_decimal(a, scratch, out)`: writes `{ i64 len, ascii bytes }` into `out`; the
// magnitude is copied into `scratch` and divided down, a leading `-` appended for a negative
// value, and the digits reversed into reading order.
let emitBiToDecimal (e: Bi) fn =
    (let a = biParam(fn)(0u32)
    in
        let scratch = biParam(fn)(1u32)
        in
            let outP = biParam(fn)(2u32)
            in
                let entry = biBlk(e)(fn)("entry")
                in
                    let zero = biBlk(e)(fn)("zero")
                    in
                        let nonZero = biBlk(e)(fn)("nonzero")
                        in
                            let copyLoop = biBlk(e)(fn)("copy_loop")
                            in
                                let copyBody = biBlk(e)(fn)("copy_body")
                                in
                                    let digitLoop = biBlk(e)(fn)("digit_loop")
                                    in
                                        let emitNeg = biBlk(e)(fn)("emit_neg")
                                        in
                                            let doNeg = biBlk(e)(fn)("do_neg")
                                            in
                                                let afterNeg = biBlk(e)(fn)("after_neg")
                                                in
                                                    let _ = biAt(e)(entry)
                                                    in
                                                        let bufPtr =
                                                            biBytePtr(e)(outP)(biK(e)(8))("buf")
                                                        in
                                                            let na = biCount(e)(a)("na")
                                                            in
                                                                let iSlot = biSlot(e)("i")
                                                                in
                                                                    let snSlot = biSlot(e)("sn")
                                                                    in
                                                                        let lenSlot = biSlot(e)("len")
                                                                        in
                                                                            Unit
                                                                            |> (given (_) ->
                                                                                biCBr(e)(biCmp(e)(intPredicateEq)(na)(biK(e)(0))("is0"))(zero)(nonZero))
                                                                            |> (given (_) -> biAt(e)(zero))
                                                                            |> (given (_) ->
                                                                                biStByte(e)(bufPtr)(biK(e)(0))(biK(e)(48))("z0"))
                                                                            |> (given (_) ->
                                                                                1
                                                                                |> biK(e)
                                                                                |> biStW(e)(outP)(biK(e)(0)))
                                                                            |> (given (_) -> biRetVoid(e))
                                                                            |> (given (_) -> biAt(e)(nonZero))
                                                                            |> (given (_) ->
                                                                                biSt(e)(biK(e)(1))(iSlot))
                                                                            |> (given (_) -> biBr(e)(copyLoop))
                                                                            |> (given (_) -> biAt(e)(copyLoop))
                                                                            |> (given (_) ->
                                                                                biCBr(e)(biCmp(e)(intPredicateSle)(biLd(e)(iSlot)("i"))(na)("i_le"))(copyBody)(digitLoop))
                                                                            |> (given (_) -> biAt(e)(copyBody))
                                                                            |> (given (_) -> biLd(e)(iSlot)("ci"))
                                                                            |> (given (ci) ->
                                                                                Unit
                                                                                |> (given (_) ->
                                                                                    "cw"
                                                                                    |> biLdW(e)(a)(ci)
                                                                                    |> biStW(e)(scratch)(ci))
                                                                                |> (given (_) ->
                                                                                    biSt(e)(biAdd(e)(ci)(biK(e)(1))("inc"))(iSlot)))
                                                                            |> (given (_) -> biBr(e)(copyLoop))
                                                                            |> (given (_) -> biAt(e)(digitLoop))
                                                                            |> (given (_) -> emitBiToDecimalLoop(e)(fn)(scratch)(snSlot)(lenSlot)(na)(bufPtr)(emitNeg))
                                                                            |> (given (_) -> biAt(e)(emitNeg))
                                                                            |> (given (_) ->
                                                                                biCBr(e)(biCmp(e)(intPredicateNe)(biNeg(e)(a)("neg"))(biK(e)(0))("is_neg"))(doNeg)(afterNeg))
                                                                            |> (given (_) -> biAt(e)(doNeg))
                                                                            |> (given (_) -> biLd(e)(lenSlot)("lenN"))
                                                                            |> (given (lenN) ->
                                                                                Unit
                                                                                |> (given (_) ->
                                                                                    biStByte(e)(bufPtr)(lenN)(biK(e)(45))("negp"))
                                                                                |> (given (_) ->
                                                                                    biSt(e)(biAdd(e)(lenN)(biK(e)(1))("lenN1"))(lenSlot)))
                                                                            |> (given (_) -> biBr(e)(afterNeg))
                                                                            |> (given (_) -> biAt(e)(afterNeg))
                                                                            |> (given (_) ->
                                                                                "finalLen"
                                                                                |> biLd(e)(lenSlot)
                                                                                |> emitBiReverse(e)(fn)(bufPtr))
                                                                            |> (given (_) ->
                                                                                "outLen"
                                                                                |> biLd(e)(lenSlot)
                                                                                |> biStW(e)(outP)(biK(e)(0)))
                                                                            |> (given (_) -> biRetVoid(e)))

// The slots of the decimal parser.
type BiParseSlots =
    | parseI: LLVMValueRef
    | parseN: LLVMValueRef
    | parseNeg: LLVMValueRef
    | parseCarry: LLVMValueRef
    | parseJ: LLVMValueRef

// The scan of `bignum_from_decimal`: an optional leading `-`, then every byte checked to be
// a decimal digit; a digit starts the multiply-add with itself as the carry.
let emitBiFromDecimalScan (e: Bi) str len (s: BiParseSlots) notEmpty minus startLoop loop body digitOk mulLoop finish invalid =
    Unit
    |> (given (_) -> biAt(e)(notEmpty))
    |> (given (_) ->
        biCBr(e)(biCmp(e)(intPredicateEq)(biLdByte(e)(str)(biK(e)(0))("b0"))(biK(e)(45))("is_minus"))(minus)(startLoop))
    |> (given (_) -> biAt(e)(minus))
    |> (given (_) ->
        biSt(e)(biK(e)(1))(s.parseNeg))
    |> (given (_) ->
        biSt(e)(biK(e)(1))(s.parseI))
    |> (given (_) ->
        biCBr(e)(biCmp(e)(intPredicateEq)(len)(biK(e)(1))("only_minus"))(invalid)(startLoop))
    |> (given (_) -> biAt(e)(startLoop))
    |> (given (_) -> biBr(e)(loop))
    |> (given (_) -> biAt(e)(loop))
    |> (given (_) ->
        biCBr(e)(biCmp(e)(intPredicateSlt)(biLd(e)(s.parseI)("i"))(len)("i_lt_len"))(body)(finish))
    |> (given (_) -> biAt(e)(body))
    |> (given (_) ->
        biLdByte(e)(str)(biLd(e)(s.parseI)("iv"))("bch"))
    |> (given (bch) ->
        Unit
        |> (given (_) ->
            biCBr(e)(biOr(e)(biCmp(e)(intPredicateSlt)(bch)(biK(e)(48))("too_low"))(biCmp(e)(intPredicateSgt)(bch)(biK(e)(57))("too_high"))("bad"))(invalid)(digitOk))
        |> (given (_) -> biAt(e)(digitOk))
        |> (given (_) ->
            biSt(e)(biSub(e)(bch)(biK(e)(48))("digit"))(s.parseCarry))
        |> (given (_) ->
            biSt(e)(biK(e)(1))(s.parseJ)))
    |> (given (_) -> biBr(e)(mulLoop))

// The multiply-add of `bignum_from_decimal`: `out = out * 10 + carry` limb by limb in place,
// a final carry growing the magnitude by one limb, then the scan advances.
let emitBiFromDecimalMul (e: Bi) outP (s: BiParseSlots) mulLoop mulBody appendCarry grow advance loop =
    Unit
    |> (given (_) -> biAt(e)(mulLoop))
    |> (given (_) ->
        biCBr(e)(biCmp(e)(intPredicateSle)(biLd(e)(s.parseJ)("j"))(biLd(e)(s.parseN)("n"))("j_le_n"))(mulBody)(appendCarry))
    |> (given (_) -> biAt(e)(mulBody))
    |> (given (_) -> biLd(e)(s.parseJ)("jv"))
    |> (given (jv) ->
        let p =
            biAdd(e)(biMul(e)(biZExt128(e)(biLdW(e)(outP)(jv)("limb"))("l128"))(biK128(e)(10))("l10"))(biZExt128(e)(biLd(e)(s.parseCarry)("c"))("c128"))("p")
        in
            Unit
            |> (given (_) ->
                "plow"
                |> biTrunc64(e)(p)
                |> biStW(e)(outP)(jv))
            |> (given (_) ->
                biSt(e)(biTrunc64(e)(biLshr(e)(p)(biK128(e)(64))("phi"))("chi"))(s.parseCarry))
            |> (given (_) ->
                biSt(e)(biAdd(e)(jv)(biK(e)(1))("jinc"))(s.parseJ)))
    |> (given (_) -> biBr(e)(mulLoop))
    |> (given (_) -> biAt(e)(appendCarry))
    |> (given (_) ->
        biCBr(e)(biCmp(e)(intPredicateNe)(biLd(e)(s.parseCarry)("cf"))(biK(e)(0))("has_carry"))(grow)(advance))
    |> (given (_) -> biAt(e)(grow))
    |> (given (_) ->
        biAdd(e)(biLd(e)(s.parseN)("n2"))(biK(e)(1))("n_inc"))
    |> (given (nn) ->
        Unit
        |> (given (_) -> biSt(e)(nn)(s.parseN))
        |> (given (_) ->
            "cc"
            |> biLd(e)(s.parseCarry)
            |> biStW(e)(outP)(nn)))
    |> (given (_) -> biBr(e)(advance))
    |> (given (_) -> biAt(e)(advance))
    |> (given (_) ->
        biSt(e)(biAdd(e)(biLd(e)(s.parseI)("id"))(biK(e)(1))("i_inc"))(s.parseI))
    |> (given (_) -> biBr(e)(loop))

// `bignum_from_decimal(str, len, out)`: 1 on success, 0 on empty or non-digit input; Horner's
// rule over the decimal bytes, in place, `out` sized to `len + 2` words by the caller.
let emitBiFromDecimal (e: Bi) (runtime: BigIntRuntime) fn =
    (let str = biParam(fn)(0u32)
    in
        let len = biParam(fn)(1u32)
        in
            let outP = biParam(fn)(2u32)
            in
                let entry = biBlk(e)(fn)("entry")
                in
                    let notEmpty = biBlk(e)(fn)("not_empty")
                    in
                        let minus = biBlk(e)(fn)("minus")
                        in
                            let startLoop = biBlk(e)(fn)("start_loop")
                            in
                                let loop = biBlk(e)(fn)("loop")
                                in
                                    let body = biBlk(e)(fn)("body")
                                    in
                                        let digitOk = biBlk(e)(fn)("digit_ok")
                                        in
                                            let mulLoop = biBlk(e)(fn)("mul_loop")
                                            in
                                                let mulBody = biBlk(e)(fn)("mul_body")
                                                in
                                                    let appendCarry = biBlk(e)(fn)("append_carry")
                                                    in
                                                        let grow = biBlk(e)(fn)("grow")
                                                        in
                                                            let advance = biBlk(e)(fn)("advance")
                                                            in
                                                                let finish = biBlk(e)(fn)("finish")
                                                                in
                                                                    let invalid = biBlk(e)(fn)("invalid")
                                                                    in
                                                                        let _ = biAt(e)(entry)
                                                                        in
                                                                            let s =
                                                                                BiParseSlots(
                                                                                    parseI = biSlot(e)("i"),
                                                                                    parseN = biSlot(e)("n"),
                                                                                    parseNeg = biSlot(e)("neg"),
                                                                                    parseCarry = biSlot(e)("carry"),
                                                                                    parseJ = biSlot(e)("j")
                                                                                )
                                                                            in
                                                                                Unit
                                                                                |> (given (_) ->
                                                                                    biSt(e)(biK(e)(0))(s.parseI))
                                                                                |> (given (_) ->
                                                                                    biSt(e)(biK(e)(0))(s.parseN))
                                                                                |> (given (_) ->
                                                                                    biSt(e)(biK(e)(0))(s.parseNeg))
                                                                                |> (given (_) ->
                                                                                    biCBr(e)(biCmp(e)(intPredicateEq)(len)(biK(e)(0))("empty"))(invalid)(notEmpty))
                                                                                |> (given (_) -> emitBiFromDecimalScan(e)(str)(len)(s)(notEmpty)(minus)(startLoop)(loop)(body)(digitOk)(mulLoop)(finish)(invalid))
                                                                                |> (given (_) -> emitBiFromDecimalMul(e)(outP)(s)(mulLoop)(mulBody)(appendCarry)(grow)(advance)(loop))
                                                                                |> (given (_) -> biAt(e)(finish))
                                                                                |> (given (_) ->
                                                                                    "nf"
                                                                                    |> biLd(e)(s.parseN)
                                                                                    |> biCallNormalize(e)(runtime)(outP)(biLd(e)(s.parseNeg)("neg")))
                                                                                |> (given (_) ->
                                                                                    1
                                                                                    |> biK(e)
                                                                                    |> biRet(e))
                                                                                |> (given (_) -> biAt(e)(invalid))
                                                                                |> (given (_) ->
                                                                                    0
                                                                                    |> biK(e)
                                                                                    |> biRet(e)))

// Declares the helper functions in `module_` with internal linkage and emits their bodies
// with `builder` (repositioned by the caller afterwards).
let defineBigIntRuntime module_ context builder i64 i8 ptrType =
    (let e =
        Bi(
            biContext = context,
            biBuilder = builder,
            biI8 = i8,
            biI32 = int32Type(context),
            biI64 = i64,
            biI128 = intType(context)(128u32),
            biPtr = ptrType
        )
    in
        let normalizeType =
            functionType(voidType(context))([ptrType, i64, i64])(3u32)(false)
        in
            let cmpMagType = functionType(i64)([ptrType, i64, ptrType, i64])(4u32)(false)
            in
                let magType = functionType(i64)([ptrType, i64, ptrType, i64, ptrType])(5u32)(false)
                in
                    let fromI64Type =
                        functionType(voidType(context))([i64, ptrType])(2u32)(false)
                    in
                        let cmpType = functionType(i64)([ptrType, ptrType])(2u32)(false)
                        in
                            let arithType =
                                functionType(voidType(context))([ptrType, ptrType, ptrType])(3u32)(false)
                            in
                                let divModType =
                                    functionType(voidType(context))([ptrType, ptrType, ptrType, ptrType, ptrType])(5u32)(false)
                                in
                                    let fromDecimalType = functionType(i64)([ptrType, i64, ptrType])(3u32)(false)
                                    in
                                        let runtime =
                                            BigIntRuntime(
                                                normalizeFn = addInternalFunction(module_)("bi_normalize")(normalizeType),
                                                normalizeType = normalizeType,
                                                cmpMagFn = addInternalFunction(module_)("bi_cmp_mag")(cmpMagType),
                                                cmpMagType = cmpMagType,
                                                addMagFn = addInternalFunction(module_)("bi_add_mag")(magType),
                                                subMagFn = addInternalFunction(module_)("bi_sub_mag")(magType),
                                                magType = magType,
                                                fromI64Fn = addInternalFunction(module_)("bignum_from_i64")(fromI64Type),
                                                fromI64Type = fromI64Type,
                                                cmpFn = addInternalFunction(module_)("bignum_cmp")(cmpType),
                                                cmpType = cmpType,
                                                addFn = addInternalFunction(module_)("bignum_add")(arithType),
                                                subFn = addInternalFunction(module_)("bignum_sub")(arithType),
                                                mulFn = addInternalFunction(module_)("bignum_mul")(arithType),
                                                arithType = arithType,
                                                divModFn = addInternalFunction(module_)("bignum_divmod")(divModType),
                                                divModType = divModType,
                                                toDecimalFn = addInternalFunction(module_)("bignum_to_decimal")(arithType),
                                                toDecimalType = arithType,
                                                fromDecimalFn = addInternalFunction(module_)("bignum_from_decimal")(fromDecimalType),
                                                fromDecimalType = fromDecimalType
                                            )
                                        in
                                            Unit
                                            |> (given (_) -> emitBiNormalize(e)(runtime.normalizeFn))
                                            |> (given (_) -> emitBiCmpMag(e)(runtime.cmpMagFn))
                                            |> (given (_) -> emitBiAddMag(e)(runtime.addMagFn))
                                            |> (given (_) -> emitBiSubMag(e)(runtime.subMagFn))
                                            |> (given (_) -> emitBiFromI64(e)(runtime.fromI64Fn))
                                            |> (given (_) -> emitBiCmp(e)(runtime)(runtime.cmpFn))
                                            |> (given (_) -> emitBiAddSub(e)(runtime)(runtime.addFn)(false))
                                            |> (given (_) -> emitBiAddSub(e)(runtime)(runtime.subFn)(true))
                                            |> (given (_) -> emitBiMul(e)(runtime)(runtime.mulFn))
                                            |> (given (_) -> emitBiDivMod(e)(runtime)(runtime.divModFn))
                                            |> (given (_) -> emitBiToDecimal(e)(runtime.toDecimalFn))
                                            |> (given (_) -> emitBiFromDecimal(e)(runtime)(runtime.fromDecimalFn))
                                            |> (given (_) -> runtime))

// The runtime a BigInt instruction needs; a module whose IR carries none has none defined.
let bigIntRuntimeOf (runtime: Maybe(BigIntRuntime)) =
    match runtime with
        | Some(defined) -> defined
        | None -> Ashes.IO.panic("IrCodegen: a BigInt instruction reached a module without the BigInt runtime")

// The call sites in the user's function: the limb count of a value, buffers sized from it
// and placed where the instruction asks (a reference-counted cell when runtime-managed, an
// arena value otherwise), and the helper called with the buffers as pointers.
let bigIntPtr builder ptrType address name = buildIntToPtr(builder)(address)(ptrType)(name)

let bigIntLimbCount builder i64 ptrType address name =
    buildAnd(builder)(buildLoad(builder)(i64)(bigIntPtr(builder)(ptrType)(address)(name + "_hdr_ptr"))(name + "_hdr"))(constInt(i64)(4294967295u64)(false))(name + "_limbs")

let bigIntBytesForWords builder i64 words =
    buildMul(builder)(words)(constInt(i64)(8u64)(false))("bigint_bytes")

let bigIntAddConst builder i64 value (amount: u64) =
    buildAdd(builder)(value)(constInt(i64)(amount)(false))("bigint_words")

// A result buffer of `sizeBytes`, as an address word.
let emitBigIntResultBuffer context function_ builder i64 i8 ptrType (arena: ArenaRuntime) mallocFn mallocType (managed: Bool) sizeBytes name =
    if managed
    then
        buildPtrToInt(builder)(emitRcAllocPayloadPtrDynamic(builder)(i64)(i8)(mallocFn)(mallocType)(sizeBytes)(name))(i64)(name + "_addr")
    else emitArenaValueAllocDynamic(context)(function_)(builder)(i64)(i8)(ptrType)(arena)(sizeBytes)(name)

// A scratch buffer of `sizeBytes` the helper works in, released after the call when
// runtime-managed (a plain arena block otherwise, reclaimed with the enclosing bracket).
let emitBigIntScratchBuffer context function_ builder i64 i8 ptrType (arena: ArenaRuntime) mallocFn mallocType (managed: Bool) sizeBytes name =
    if managed
    then
        buildPtrToInt(builder)(emitRcAllocPayloadPtrDynamic(builder)(i64)(i8)(mallocFn)(mallocType)(sizeBytes)(name))(i64)(name + "_addr")
    else
        emitArenaAllocDynamic(context)(function_)(builder)(i64)(arena)(alignArenaSizeDynamic(builder)(i64)(sizeBytes)(name + "_aligned"))(name)

let emitBigIntScratchRelease context function_ builder i64 i8 ptrType freeFn freeType (managed: Bool) address =
    if managed
    then emitRuntimeRcDrop(context)(function_)(i64)(i8)(ptrType)(builder)(freeFn)(freeType)(address)
    else Unit

// `BigIntFromInt`: a header and one limb.
let emitBigIntFromInt context function_ builder i64 i8 ptrType (arena: ArenaRuntime) (runtime: BigIntRuntime) mallocFn mallocType (managed: Bool) value =
    (let outAddress =
        emitBigIntResultBuffer(context)(function_)(builder)(i64)(i8)(ptrType)(arena)(mallocFn)(mallocType)(managed)(constInt(i64)(16u64)(false))("bigint_from_int")
    in
        Unit
        |> (given (_) -> buildCall(builder)(runtime.fromI64Type)(runtime.fromI64Fn)([value, bigIntPtr(builder)(ptrType)(outAddress)("bigint_from_out")])(2u32)(""))
        |> (given (_) -> outAddress))

// Addition, subtraction, and multiplication: the result fits `la + lb + 3` words (header,
// magnitude, and slack).
let emitBigIntArith context function_ builder i64 i8 ptrType (arena: ArenaRuntime) (runtime: BigIntRuntime) mallocFn mallocType (managed: Bool) fn left right =
    (let words =
        bigIntAddConst(builder)(i64)(buildAdd(builder)(bigIntLimbCount(builder)(i64)(ptrType)(left)("bigint_l"))(bigIntLimbCount(builder)(i64)(ptrType)(right)("bigint_r"))("bigint_la_lb"))(3u64)
    in
        let outAddress =
            emitBigIntResultBuffer(context)(function_)(builder)(i64)(i8)(ptrType)(arena)(mallocFn)(mallocType)(managed)(bigIntBytesForWords(builder)(i64)(words))("bigint_arith")
        in
            Unit
            |> (given (_) -> buildCall(builder)(runtime.arithType)(fn)([bigIntPtr(builder)(ptrType)(left)("bigint_a"), bigIntPtr(builder)(ptrType)(right)("bigint_b"), bigIntPtr(builder)(ptrType)(outAddress)("bigint_out")])(3u32)(""))
            |> (given (_) -> outAddress))

// Division and modulo: the quotient fits `la + 2` words, the remainder `lb + 2`, and the
// Algorithm D scratch (the normalized divisor and working dividend as 32-bit digits)
// `la + lb + 4`; the buffer not returned and the scratch are released when runtime-managed.
let emitBigIntDivMod context function_ builder i64 i8 ptrType (arena: ArenaRuntime) (runtime: BigIntRuntime) mallocFn mallocType freeFn freeType (managed: Bool) (returnQuotient: Bool) left right =
    (let la = bigIntLimbCount(builder)(i64)(ptrType)(left)("bigint_dl")
    in
        let lb = bigIntLimbCount(builder)(i64)(ptrType)(right)("bigint_dr")
        in
            let qAddress =
                emitBigIntResultBuffer(context)(function_)(builder)(i64)(i8)(ptrType)(arena)(mallocFn)(mallocType)(managed)(2u64
                |> bigIntAddConst(builder)(i64)(la)
                |> bigIntBytesForWords(builder)(i64))("bigint_quotient")
            in
                let rAddress =
                    emitBigIntResultBuffer(context)(function_)(builder)(i64)(i8)(ptrType)(arena)(mallocFn)(mallocType)(managed)(2u64
                    |> bigIntAddConst(builder)(i64)(lb)
                    |> bigIntBytesForWords(builder)(i64))("bigint_remainder")
                in
                    let scratchAddress =
                        emitBigIntScratchBuffer(context)(function_)(builder)(i64)(i8)(ptrType)(arena)(mallocFn)(mallocType)(managed)(4u64
                        |> bigIntAddConst(builder)(i64)(buildAdd(builder)(la)(lb)("bigint_dsw"))
                        |> bigIntBytesForWords(builder)(i64))("bigint_div_scratch")
                    in
                        Unit
                        |> (given (_) -> buildCall(builder)(runtime.divModType)(runtime.divModFn)([bigIntPtr(builder)(ptrType)(left)("bigint_da"), bigIntPtr(builder)(ptrType)(right)("bigint_db"), bigIntPtr(builder)(ptrType)(qAddress)("bigint_q"), bigIntPtr(builder)(ptrType)(rAddress)("bigint_r"), bigIntPtr(builder)(ptrType)(scratchAddress)("bigint_dscratch")])(5u32)(""))
                        |> (given (_) ->
                            emitBigIntScratchRelease(context)(function_)(builder)(i64)(i8)(ptrType)(freeFn)(freeType)(managed)(if returnQuotient
                            then rAddress
                            else qAddress))
                        |> (given (_) -> emitBigIntScratchRelease(context)(function_)(builder)(i64)(i8)(ptrType)(freeFn)(freeType)(managed)(scratchAddress))
                        |> (given (_) ->
                            if returnQuotient
                            then qAddress
                            else rAddress))

// `BigIntBinary` by its operation name.
let emitBigIntBinary context function_ builder i64 i8 ptrType (arena: ArenaRuntime) (runtime: BigIntRuntime) mallocFn mallocType freeFn freeType (managed: Bool) (operation: Str) left right =
    match operation with
        | "add" -> emitBigIntArith(context)(function_)(builder)(i64)(i8)(ptrType)(arena)(runtime)(mallocFn)(mallocType)(managed)(runtime.addFn)(left)(right)
        | "sub" -> emitBigIntArith(context)(function_)(builder)(i64)(i8)(ptrType)(arena)(runtime)(mallocFn)(mallocType)(managed)(runtime.subFn)(left)(right)
        | "mul" -> emitBigIntArith(context)(function_)(builder)(i64)(i8)(ptrType)(arena)(runtime)(mallocFn)(mallocType)(managed)(runtime.mulFn)(left)(right)
        | "div" -> emitBigIntDivMod(context)(function_)(builder)(i64)(i8)(ptrType)(arena)(runtime)(mallocFn)(mallocType)(freeFn)(freeType)(managed)(true)(left)(right)
        | "mod" -> emitBigIntDivMod(context)(function_)(builder)(i64)(i8)(ptrType)(arena)(runtime)(mallocFn)(mallocType)(freeFn)(freeType)(managed)(false)(left)(right)
        | other -> Ashes.IO.panic("emitBigIntBinary: unknown BigInt operation " + other)

// `BigIntCompare`: -1, 0, or 1.
let emitBigIntCompare builder ptrType (runtime: BigIntRuntime) left right = buildCall(builder)(runtime.cmpType)(runtime.cmpFn)([bigIntPtr(builder)(ptrType)(left)("bigint_ca"), bigIntPtr(builder)(ptrType)(right)("bigint_cb")])(2u32)("bigint_cmp")

// `BigIntToString`: the decimal text, at most `la * 20` digits and a sign, so `la * 3 + 4`
// words cover the length word and the bytes; the `la + 2` word scratch holds the magnitude
// being divided down.
let emitBigIntToString context function_ builder i64 i8 ptrType (arena: ArenaRuntime) (runtime: BigIntRuntime) mallocFn mallocType freeFn freeType (managed: Bool) value =
    (let la = bigIntLimbCount(builder)(i64)(ptrType)(value)("bigint_ts")
    in
        let scratchAddress =
            emitBigIntScratchBuffer(context)(function_)(builder)(i64)(i8)(ptrType)(arena)(mallocFn)(mallocType)(managed)(2u64
            |> bigIntAddConst(builder)(i64)(la)
            |> bigIntBytesForWords(builder)(i64))("bigint_text_scratch")
        in
            let outAddress =
                emitBigIntResultBuffer(context)(function_)(builder)(i64)(i8)(ptrType)(arena)(mallocFn)(mallocType)(managed)(4u64
                |> bigIntAddConst(builder)(i64)(buildMul(builder)(la)(constInt(i64)(3u64)(false))("bigint_ts_w"))
                |> bigIntBytesForWords(builder)(i64))("bigint_text")
            in
                Unit
                |> (given (_) -> buildCall(builder)(runtime.toDecimalType)(runtime.toDecimalFn)([bigIntPtr(builder)(ptrType)(value)("bigint_tsa"), bigIntPtr(builder)(ptrType)(scratchAddress)("bigint_scratch"), bigIntPtr(builder)(ptrType)(outAddress)("bigint_tsout")])(3u32)(""))
                |> (given (_) -> emitBigIntScratchRelease(context)(function_)(builder)(i64)(i8)(ptrType)(freeFn)(freeType)(managed)(scratchAddress))
                |> (given (_) -> outAddress))

// `BigIntToInt`: `Ok(value)` when the magnitude fits a machine integer (one limb at most
// `2^63 - 1`, or `2^63` for a negative value), else `Error("BigInt does not fit in Int")`.
let emitBigIntToInt context function_ builder i64 i8 ptrType mallocFn mallocType memcpyFn memcpyType bigAddress =
    (let resultSlot = buildEntryAlloca(builder)(i64)("bi_toint_result")
    in
        let valueSlot = buildEntryAlloca(builder)(i64)("bi_toint_value")
        in
            let header =
                buildLoad(builder)(i64)(bigIntPtr(builder)(ptrType)(bigAddress)("bi_toint_hdr_ptr"))("bi_toint_hdr")
            in
                let limbCount =
                    buildAnd(builder)(header)(constInt(i64)(4294967295u64)(false))("bi_toint_lc")
                in
                    let neg =
                        buildAnd(builder)(buildLShr(builder)(header)(constInt(i64)(32u64)(false))("bi_toint_sh"))(constInt(i64)(1u64)(false))("bi_toint_neg")
                    in
                        let zeroBlock = appendBasicBlock(context)(function_)("bi_toint_zero")
                        in
                            let checkOne = appendBasicBlock(context)(function_)("bi_toint_check_one")
                            in
                                let oneBlock = appendBasicBlock(context)(function_)("bi_toint_one")
                                in
                                    let negBlock = appendBasicBlock(context)(function_)("bi_toint_neg_case")
                                    in
                                        let posBlock = appendBasicBlock(context)(function_)("bi_toint_pos_case")
                                        in
                                            let okBlock = appendBasicBlock(context)(function_)("bi_toint_ok")
                                            in
                                                let errBlock = appendBasicBlock(context)(function_)("bi_toint_err")
                                                in
                                                    let doneBlock = appendBasicBlock(context)(function_)("bi_toint_done")
                                                    in
                                                        Unit
                                                        |> (given (_) ->
                                                            buildCondBr(builder)(buildICmp(builder)(intPredicateEq)(limbCount)(constInt(i64)(0u64)(false))("bi_toint_is0"))(zeroBlock)(checkOne))
                                                        |> (given (_) -> positionBuilderAtEnd(builder)(zeroBlock))
                                                        |> (given (_) ->
                                                            buildStore(builder)(constInt(i64)(0u64)(false))(valueSlot))
                                                        |> (given (_) -> buildBr(builder)(okBlock))
                                                        |> (given (_) -> positionBuilderAtEnd(builder)(checkOne))
                                                        |> (given (_) ->
                                                            buildCondBr(builder)(buildICmp(builder)(intPredicateEq)(limbCount)(constInt(i64)(1u64)(false))("bi_toint_is1"))(oneBlock)(errBlock))
                                                        |> (given (_) -> positionBuilderAtEnd(builder)(oneBlock))
                                                        |> (given (_) ->
                                                            buildLoad(builder)(i64)(memOffsetPtr(builder)(i64)(i8)(ptrType)(bigAddress)(8)("bi_toint_limb_ptr"))("bi_toint_limb"))
                                                        |> (given (limb) ->
                                                            Unit
                                                            |> (given (_) ->
                                                                buildCondBr(builder)(buildICmp(builder)(intPredicateNe)(neg)(constInt(i64)(0u64)(false))("bi_toint_isneg"))(negBlock)(posBlock))
                                                            |> (given (_) -> positionBuilderAtEnd(builder)(posBlock))
                                                            |> (given (_) -> buildStore(builder)(limb)(valueSlot))
                                                            |> (given (_) ->
                                                                buildCondBr(builder)(buildICmp(builder)(intPredicateUle)(limb)(constInt(i64)(9223372036854775807u64)(false))("bi_toint_posok"))(okBlock)(errBlock))
                                                            |> (given (_) -> positionBuilderAtEnd(builder)(negBlock))
                                                            |> (given (_) ->
                                                                buildStore(builder)(buildSub(builder)(constInt(i64)(0u64)(false))(limb)("bi_toint_negval"))(valueSlot))
                                                            |> (given (_) ->
                                                                buildCondBr(builder)(buildICmp(builder)(intPredicateUle)(limb)(constInt(i64)(9223372036854775808u64)(false))("bi_toint_negok"))(okBlock)(errBlock)))
                                                        |> (given (_) -> positionBuilderAtEnd(builder)(okBlock))
                                                        |> (given (_) ->
                                                            buildStore(builder)(emitResultAdt(builder)(i64)(i8)(ptrType)(mallocFn)(mallocType)(0)(buildLoad(builder)(i64)(valueSlot)("bi_toint_v"))("bi_toint_okv"))(resultSlot))
                                                        |> (given (_) -> buildBr(builder)(doneBlock))
                                                        |> (given (_) -> positionBuilderAtEnd(builder)(errBlock))
                                                        |> (given (_) ->
                                                            buildStore(builder)(emitResultAdt(builder)(i64)(i8)(ptrType)(mallocFn)(mallocType)(1)(emitAsciiHeapString(builder)(i64)(i8)(ptrType)(mallocFn)(mallocType)(memcpyFn)(memcpyType)([66, 105, 103, 73, 110, 116, 32, 100, 111, 101, 115, 32, 110, 111, 116, 32, 102, 105, 116, 32, 105, 110, 32, 73, 110, 116])("bi_toint_msg"))("bi_toint_errv"))(resultSlot))
                                                        |> (given (_) -> buildBr(builder)(doneBlock))
                                                        |> (given (_) -> positionBuilderAtEnd(builder)(doneBlock))
                                                        |> (given (_) -> buildLoad(builder)(i64)(resultSlot)("bi_toint_final")))

// `BigIntFromString`: `Ok(value)` for a decimal string (an optional leading `-`), else
// `Error("invalid decimal integer")`; the magnitude fits `len + 2` words.
let emitBigIntFromString context function_ builder i64 i8 ptrType (arena: ArenaRuntime) (runtime: BigIntRuntime) mallocFn mallocType freeFn freeType memcpyFn memcpyType (managed: Bool) stringRef =
    match emitStringParts(builder)(i64)(ptrType)(stringRef)("bi_parse") with
        | (len, bytesAddr) ->
            let outAddress =
                emitBigIntResultBuffer(context)(function_)(builder)(i64)(i8)(ptrType)(arena)(mallocFn)(mallocType)(managed)(buildMul(builder)(buildAdd(builder)(len)(constInt(i64)(2u64)(false))("bi_parse_words"))(constInt(i64)(8u64)(false))("bi_parse_sz"))("bigint_parse")
            in
                let success = buildCall(builder)(runtime.fromDecimalType)(runtime.fromDecimalFn)([bigIntPtr(builder)(ptrType)(bytesAddr)("bi_parse_bytes"), len, bigIntPtr(builder)(ptrType)(outAddress)("bi_parse_out")])(3u32)("bi_parse_ok")
                in
                    let resultSlot = buildEntryAlloca(builder)(i64)("bi_parse_result")
                    in
                        let okBlock = appendBasicBlock(context)(function_)("bi_parse_ok_blk")
                        in
                            let errBlock = appendBasicBlock(context)(function_)("bi_parse_err_blk")
                            in
                                let doneBlock = appendBasicBlock(context)(function_)("bi_parse_done")
                                in
                                    Unit
                                    |> (given (_) ->
                                        buildCondBr(builder)(buildICmp(builder)(intPredicateNe)(success)(constInt(i64)(0u64)(false))("bi_parse_succeeded"))(okBlock)(errBlock))
                                    |> (given (_) -> positionBuilderAtEnd(builder)(okBlock))
                                    |> (given (_) ->
                                        buildStore(builder)(emitResultAdt(builder)(i64)(i8)(ptrType)(mallocFn)(mallocType)(0)(outAddress)("bi_parse_okv"))(resultSlot))
                                    |> (given (_) -> buildBr(builder)(doneBlock))
                                    |> (given (_) -> positionBuilderAtEnd(builder)(errBlock))
                                    |> (given (_) -> emitBigIntScratchRelease(context)(function_)(builder)(i64)(i8)(ptrType)(freeFn)(freeType)(managed)(outAddress))
                                    |> (given (_) ->
                                        buildStore(builder)(emitResultAdt(builder)(i64)(i8)(ptrType)(mallocFn)(mallocType)(1)(emitAsciiHeapString(builder)(i64)(i8)(ptrType)(mallocFn)(mallocType)(memcpyFn)(memcpyType)([105, 110, 118, 97, 108, 105, 100, 32, 100, 101, 99, 105, 109, 97, 108, 32, 105, 110, 116, 101, 103, 101, 114])("bi_parse_msg"))("bi_parse_errv"))(resultSlot))
                                    |> (given (_) -> buildBr(builder)(doneBlock))
                                    |> (given (_) -> positionBuilderAtEnd(builder)(doneBlock))
                                    |> (given (_) -> buildLoad(builder)(i64)(resultSlot)("bi_parse_final"))
