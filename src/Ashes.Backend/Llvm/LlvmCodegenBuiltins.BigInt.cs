using Ashes.Backend.Llvm.Interop;

namespace Ashes.Backend.Llvm;

internal static partial class LlvmCodegen
{
    // ── Ashes.BigInt ─────────────────────────────────────────────────────────
    // BigInt values are heap pointers to { i64 header = (negFlag<<32)|limbCount, i64 limb[...] },
    // sign-magnitude, base 2^64, little-endian, normalized (zero = header 0, no limbs).
    //
    // The arithmetic is emitted as LLVM-IR runtime helper functions (EmitBigIntRuntimeHelpers) —
    // like the freestanding memcmp/strlen helpers — rather than a linked library. The helpers are
    // allocation-free: this codegen reads the operand limb counts, pre-sizes generous result buffers
    // in the arena (EmitAllocDynamic), and passes them in.

    private const string BigIntFromI64 = "bignum_from_i64";
    private const string BigIntCmp = "bignum_cmp";
    private const string BigIntAddFn = "bignum_add";
    private const string BigIntSubFn = "bignum_sub";
    private const string BigIntMulFn = "bignum_mul";
    private const string BigIntDivModFn = "bignum_divmod";
    private const string BigIntToDecimal = "bignum_to_decimal";

    // ── Call sites (in the current user function): size buffers, then call a helper ──────────────

    private static LlvmValueHandle BigIntAsPtr(LlvmCodegenState state, LlvmValueHandle address, string name)
        => LlvmApi.BuildIntToPtr(state.Target.Builder, address, state.I8Ptr, name);

    private static LlvmValueHandle BigIntLimbCount(LlvmCodegenState state, LlvmValueHandle address, string name)
    {
        LlvmValueHandle header = LoadMemory(state, address, 0, name + "_hdr");
        return LlvmApi.BuildAnd(state.Target.Builder, header, LlvmApi.ConstInt(state.I64, 0xFFFFFFFF, 0), name + "_limbs");
    }

    private static LlvmValueHandle BigIntBytesForWords(LlvmCodegenState state, LlvmValueHandle words)
        => LlvmApi.BuildMul(state.Target.Builder, words, LlvmApi.ConstInt(state.I64, 8, 0), "bigint_bytes");

    private static LlvmValueHandle BigIntAddConst(LlvmCodegenState state, LlvmValueHandle value, ulong amount)
        => LlvmApi.BuildAdd(state.Target.Builder, value, LlvmApi.ConstInt(state.I64, amount, 0), "bigint_words");

    private static LlvmValueHandle EmitBigIntFromInt(LlvmCodegenState state, LlvmValueHandle value)
    {
        LlvmTypeHandle voidType = LlvmApi.VoidTypeInContext(state.Target.Context);
        LlvmTypeHandle fnType = LlvmApi.FunctionType(voidType, [state.I64, state.I8Ptr]);
        LlvmValueHandle fn = LlvmApi.GetNamedFunction(state.Target.Module, BigIntFromI64);
        LlvmValueHandle outAddress = EmitAlloc(state, 16); // header + one limb
        LlvmApi.BuildCall2(state.Target.Builder, fnType, fn, [value, BigIntAsPtr(state, outAddress, "bigint_from_out")], "");
        return outAddress;
    }

    private static LlvmValueHandle EmitBigIntArith(LlvmCodegenState state, LlvmValueHandle left, LlvmValueHandle right, string fnName)
    {
        LlvmValueHandle la = BigIntLimbCount(state, left, "bigint_l");
        LlvmValueHandle lb = BigIntLimbCount(state, right, "bigint_r");
        // add/sub/mul all fit in la + lb + 3 words (header + magnitude + slack).
        LlvmValueHandle words = BigIntAddConst(state, LlvmApi.BuildAdd(state.Target.Builder, la, lb, "bigint_la_lb"), 3);
        LlvmValueHandle outAddress = EmitAllocDynamic(state, BigIntBytesForWords(state, words));

        LlvmTypeHandle voidType = LlvmApi.VoidTypeInContext(state.Target.Context);
        LlvmTypeHandle fnType = LlvmApi.FunctionType(voidType, [state.I8Ptr, state.I8Ptr, state.I8Ptr]);
        LlvmValueHandle fn = LlvmApi.GetNamedFunction(state.Target.Module, fnName);
        LlvmApi.BuildCall2(state.Target.Builder, fnType, fn,
            [BigIntAsPtr(state, left, "bigint_a"), BigIntAsPtr(state, right, "bigint_b"), BigIntAsPtr(state, outAddress, "bigint_out")], "");
        return outAddress;
    }

    private static LlvmValueHandle EmitBigIntDivMod(LlvmCodegenState state, LlvmValueHandle left, LlvmValueHandle right, bool returnQuotient)
    {
        LlvmValueHandle la = BigIntLimbCount(state, left, "bigint_dl");
        LlvmValueHandle lb = BigIntLimbCount(state, right, "bigint_dr");
        LlvmValueHandle qAddress = EmitAllocDynamic(state, BigIntBytesForWords(state, BigIntAddConst(state, la, 2)));
        LlvmValueHandle rAddress = EmitAllocDynamic(state, BigIntBytesForWords(state, BigIntAddConst(state, lb, 2)));
        // Algorithm D scratch: normalized divisor (2*lb digits) + working dividend (2*la + 1 digits),
        // as 32-bit digits — la + lb + 4 words covers both with slack.
        LlvmValueHandle laLb = LlvmApi.BuildAdd(state.Target.Builder, la, lb, "bigint_dsw");
        LlvmValueHandle scratchAddress = EmitAllocDynamic(state, BigIntBytesForWords(state, BigIntAddConst(state, laLb, 4)));

        LlvmTypeHandle voidType = LlvmApi.VoidTypeInContext(state.Target.Context);
        LlvmTypeHandle fnType = LlvmApi.FunctionType(voidType, [state.I8Ptr, state.I8Ptr, state.I8Ptr, state.I8Ptr, state.I8Ptr]);
        LlvmValueHandle fn = LlvmApi.GetNamedFunction(state.Target.Module, BigIntDivModFn);
        LlvmApi.BuildCall2(state.Target.Builder, fnType, fn,
            [BigIntAsPtr(state, left, "bigint_da"), BigIntAsPtr(state, right, "bigint_db"),
             BigIntAsPtr(state, qAddress, "bigint_q"), BigIntAsPtr(state, rAddress, "bigint_r"),
             BigIntAsPtr(state, scratchAddress, "bigint_dscratch")], "");
        return returnQuotient ? qAddress : rAddress;
    }

    private static LlvmValueHandle EmitBigIntBinary(LlvmCodegenState state, LlvmValueHandle left, LlvmValueHandle right, string op) => op switch
    {
        "add" => EmitBigIntArith(state, left, right, BigIntAddFn),
        "sub" => EmitBigIntArith(state, left, right, BigIntSubFn),
        "mul" => EmitBigIntArith(state, left, right, BigIntMulFn),
        "div" => EmitBigIntDivMod(state, left, right, returnQuotient: true),
        "mod" => EmitBigIntDivMod(state, left, right, returnQuotient: false),
        _ => throw new InvalidOperationException($"Unknown BigInt binary op '{op}'.")
    };

    private static LlvmValueHandle EmitBigIntCompare(LlvmCodegenState state, LlvmValueHandle left, LlvmValueHandle right)
    {
        LlvmTypeHandle fnType = LlvmApi.FunctionType(state.I64, [state.I8Ptr, state.I8Ptr]);
        LlvmValueHandle fn = LlvmApi.GetNamedFunction(state.Target.Module, BigIntCmp);
        return LlvmApi.BuildCall2(state.Target.Builder, fnType, fn,
            [BigIntAsPtr(state, left, "bigint_ca"), BigIntAsPtr(state, right, "bigint_cb")], "bigint_cmp");
    }

    private static LlvmValueHandle EmitBigIntToString(LlvmCodegenState state, LlvmValueHandle value)
    {
        LlvmValueHandle la = BigIntLimbCount(state, value, "bigint_ts");
        LlvmValueHandle scratchAddress = EmitAllocDynamic(state, BigIntBytesForWords(state, BigIntAddConst(state, la, 2)));
        // Decimal string: <= la*20 digits + sign; string word count la*3 + 4 covers len word + bytes.
        LlvmValueHandle outWords = BigIntAddConst(state, LlvmApi.BuildMul(state.Target.Builder, la, LlvmApi.ConstInt(state.I64, 3, 0), "bigint_ts_w"), 4);
        LlvmValueHandle outAddress = EmitAllocDynamic(state, BigIntBytesForWords(state, outWords));

        LlvmTypeHandle voidType = LlvmApi.VoidTypeInContext(state.Target.Context);
        LlvmTypeHandle fnType = LlvmApi.FunctionType(voidType, [state.I8Ptr, state.I8Ptr, state.I8Ptr]);
        LlvmValueHandle fn = LlvmApi.GetNamedFunction(state.Target.Module, BigIntToDecimal);
        LlvmApi.BuildCall2(state.Target.Builder, fnType, fn,
            [BigIntAsPtr(state, value, "bigint_tsa"), BigIntAsPtr(state, scratchAddress, "bigint_scratch"), BigIntAsPtr(state, outAddress, "bigint_tsout")], "");
        return outAddress;
    }

    // BigInt -> Result(Str, Int): Ok(value) when it fits an i64, else Err.
    private static LlvmValueHandle EmitBigIntToInt(LlvmCodegenState state, LlvmValueHandle bigAddr)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle resultSlot = LlvmApi.BuildAlloca(builder, state.I64, "bi_toint_result");
        LlvmValueHandle valueSlot = LlvmApi.BuildAlloca(builder, state.I64, "bi_toint_value");
        LlvmValueHandle header = LoadMemory(state, bigAddr, 0, "bi_toint_hdr");
        LlvmValueHandle limbCount = LlvmApi.BuildAnd(builder, header, LlvmApi.ConstInt(state.I64, 0xFFFFFFFF, 0), "bi_toint_lc");
        LlvmValueHandle neg = LlvmApi.BuildAnd(builder, LlvmApi.BuildLShr(builder, header, LlvmApi.ConstInt(state.I64, 32, 0), "bi_toint_sh"), LlvmApi.ConstInt(state.I64, 1, 0), "bi_toint_neg");

        var ctx = state.Target.Context;
        var fn = state.Function;
        var zeroBlk = LlvmApi.AppendBasicBlockInContext(ctx, fn, "bi_toint_zero");
        var checkOne = LlvmApi.AppendBasicBlockInContext(ctx, fn, "bi_toint_check_one");
        var oneBlk = LlvmApi.AppendBasicBlockInContext(ctx, fn, "bi_toint_one");
        var negBlk = LlvmApi.AppendBasicBlockInContext(ctx, fn, "bi_toint_neg_case");
        var posBlk = LlvmApi.AppendBasicBlockInContext(ctx, fn, "bi_toint_pos_case");
        var okBlk = LlvmApi.AppendBasicBlockInContext(ctx, fn, "bi_toint_ok");
        var errBlk = LlvmApi.AppendBasicBlockInContext(ctx, fn, "bi_toint_err");
        var doneBlk = LlvmApi.AppendBasicBlockInContext(ctx, fn, "bi_toint_done");

        LlvmApi.BuildCondBr(builder, LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, limbCount, LlvmApi.ConstInt(state.I64, 0, 0), "bi_toint_is0"), zeroBlk, checkOne);

        LlvmApi.PositionBuilderAtEnd(builder, zeroBlk);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), valueSlot);
        LlvmApi.BuildBr(builder, okBlk);

        LlvmApi.PositionBuilderAtEnd(builder, checkOne);
        LlvmApi.BuildCondBr(builder, LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, limbCount, LlvmApi.ConstInt(state.I64, 1, 0), "bi_toint_is1"), oneBlk, errBlk);

        LlvmApi.PositionBuilderAtEnd(builder, oneBlk);
        LlvmValueHandle limb = LoadMemory(state, bigAddr, 8, "bi_toint_limb");
        LlvmApi.BuildCondBr(builder, LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, neg, LlvmApi.ConstInt(state.I64, 0, 0), "bi_toint_isneg"), negBlk, posBlk);

        // positive: fits when magnitude <= INT64_MAX
        LlvmApi.PositionBuilderAtEnd(builder, posBlk);
        LlvmApi.BuildStore(builder, limb, valueSlot);
        LlvmApi.BuildCondBr(builder, LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ule, limb, LlvmApi.ConstInt(state.I64, (ulong)long.MaxValue, 0), "bi_toint_posok"), okBlk, errBlk);

        // negative: fits when magnitude <= 2^63 (INT64_MIN); value = -magnitude
        LlvmApi.PositionBuilderAtEnd(builder, negBlk);
        LlvmApi.BuildStore(builder, LlvmApi.BuildSub(builder, LlvmApi.ConstInt(state.I64, 0, 0), limb, "bi_toint_negval"), valueSlot);
        LlvmApi.BuildCondBr(builder, LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ule, limb, LlvmApi.ConstInt(state.I64, 1UL << 63, 0), "bi_toint_negok"), okBlk, errBlk);

        LlvmApi.PositionBuilderAtEnd(builder, okBlk);
        LlvmApi.BuildStore(builder, EmitResultOk(state, LlvmApi.BuildLoad2(builder, state.I64, valueSlot, "bi_toint_v")), resultSlot);
        LlvmApi.BuildBr(builder, doneBlk);

        LlvmApi.PositionBuilderAtEnd(builder, errBlk);
        LlvmApi.BuildStore(builder, EmitResultError(state, EmitHeapStringLiteral(state, "BigInt does not fit in Int")), resultSlot);
        LlvmApi.BuildBr(builder, doneBlk);

        LlvmApi.PositionBuilderAtEnd(builder, doneBlk);
        return LlvmApi.BuildLoad2(builder, state.I64, resultSlot, "bi_toint_final");
    }

    // Str -> Result(Str, BigInt): parse decimal; Err on empty / non-digit input.
    private static LlvmValueHandle EmitBigIntFromString(LlvmCodegenState state, LlvmValueHandle strRef)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle len = LoadStringLength(state, strRef, "bi_parse_len");
        LlvmValueHandle bytesPtr = GetStringBytesPointer(state, strRef, "bi_parse_bytes");
        // result magnitude fits in len + 2 words (header + limbs + slack).
        LlvmValueHandle words = LlvmApi.BuildAdd(builder, len, LlvmApi.ConstInt(state.I64, 2, 0), "bi_parse_words");
        LlvmValueHandle outAddr = EmitAllocDynamic(state, LlvmApi.BuildMul(builder, words, LlvmApi.ConstInt(state.I64, 8, 0), "bi_parse_sz"));

        LlvmTypeHandle fnType = LlvmApi.FunctionType(state.I64, [state.I8Ptr, state.I64, state.I8Ptr]);
        LlvmValueHandle fn = LlvmApi.GetNamedFunction(state.Target.Module, "bignum_from_decimal");
        LlvmValueHandle success = LlvmApi.BuildCall2(builder, fnType, fn, [bytesPtr, len, BigIntAsPtr(state, outAddr, "bi_parse_out")], "bi_parse_ok");

        LlvmValueHandle resultSlot = LlvmApi.BuildAlloca(builder, state.I64, "bi_parse_result");
        var ctx = state.Target.Context;
        var okBlk = LlvmApi.AppendBasicBlockInContext(ctx, state.Function, "bi_parse_ok_blk");
        var errBlk = LlvmApi.AppendBasicBlockInContext(ctx, state.Function, "bi_parse_err_blk");
        var doneBlk = LlvmApi.AppendBasicBlockInContext(ctx, state.Function, "bi_parse_done");
        LlvmApi.BuildCondBr(builder, LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, success, LlvmApi.ConstInt(state.I64, 0, 0), "bi_parse_succeeded"), okBlk, errBlk);

        LlvmApi.PositionBuilderAtEnd(builder, okBlk);
        LlvmApi.BuildStore(builder, EmitResultOk(state, outAddr), resultSlot);
        LlvmApi.BuildBr(builder, doneBlk);

        LlvmApi.PositionBuilderAtEnd(builder, errBlk);
        LlvmApi.BuildStore(builder, EmitResultError(state, EmitHeapStringLiteral(state, "invalid decimal integer")), resultSlot);
        LlvmApi.BuildBr(builder, doneBlk);

        LlvmApi.PositionBuilderAtEnd(builder, doneBlk);
        return LlvmApi.BuildLoad2(builder, state.I64, resultSlot, "bi_parse_final");
    }

    // ── Runtime helper emission (once per program that uses BigInt) ──────────────────────────────
    // A tiny builder toolkit (Bi) keeps the ports readable. Every helper is a direct translation of
    // the BigInt semantics. Loop-mutable state lives in entry-block
    // allocas; blocks follow the memcmp/strlen preheader→cond→body→exit idiom (no phi nodes).

    private sealed class Bi(LlvmTargetContext target)
    {
        internal readonly LlvmTargetContext T = target;
        internal readonly LlvmBuilderHandle B = target.Builder;
        internal readonly LlvmContextHandle Ctx = target.Context;
        internal readonly LlvmTypeHandle I8 = LlvmApi.Int8TypeInContext(target.Context);
        internal readonly LlvmTypeHandle I32 = LlvmApi.Int32TypeInContext(target.Context);
        internal readonly LlvmTypeHandle I64 = LlvmApi.Int64TypeInContext(target.Context);
        internal readonly LlvmTypeHandle I128 = LlvmApi.IntTypeInContext(target.Context, 128);
        internal readonly LlvmTypeHandle Ptr = LlvmApi.PointerTypeInContext(target.Context, 0);
        internal readonly LlvmTypeHandle Void = LlvmApi.VoidTypeInContext(target.Context);

        internal LlvmValueHandle K(long v) => LlvmApi.ConstInt(I64, unchecked((ulong)v), 0);
        internal LlvmValueHandle K128(long v) => LlvmApi.ConstInt(I128, unchecked((ulong)v), 0);
        internal LlvmBasicBlockHandle Blk(LlvmValueHandle fn, string n) => LlvmApi.AppendBasicBlockInContext(Ctx, fn, n);
        internal void At(LlvmBasicBlockHandle bb) => LlvmApi.PositionBuilderAtEnd(B, bb);
        internal LlvmValueHandle Slot(string n) => LlvmApi.BuildAlloca(B, I64, n);
        internal LlvmValueHandle Ld(LlvmValueHandle slot, string n) => LlvmApi.BuildLoad2(B, I64, slot, n);
        internal void St(LlvmValueHandle val, LlvmValueHandle slot) => LlvmApi.BuildStore(B, val, slot);
        internal LlvmValueHandle Add(LlvmValueHandle a, LlvmValueHandle b, string n) => LlvmApi.BuildAdd(B, a, b, n);
        internal LlvmValueHandle Sub(LlvmValueHandle a, LlvmValueHandle b, string n) => LlvmApi.BuildSub(B, a, b, n);
        internal LlvmValueHandle Mul(LlvmValueHandle a, LlvmValueHandle b, string n) => LlvmApi.BuildMul(B, a, b, n);
        internal LlvmValueHandle UDiv(LlvmValueHandle a, LlvmValueHandle b, string n) => LlvmApi.BuildUDiv(B, a, b, n);
        internal LlvmValueHandle And(LlvmValueHandle a, LlvmValueHandle b, string n) => LlvmApi.BuildAnd(B, a, b, n);
        internal LlvmValueHandle Or(LlvmValueHandle a, LlvmValueHandle b, string n) => LlvmApi.BuildOr(B, a, b, n);
        internal LlvmValueHandle Shl(LlvmValueHandle a, LlvmValueHandle b, string n) => LlvmApi.BuildShl(B, a, b, n);
        internal LlvmValueHandle Lshr(LlvmValueHandle a, LlvmValueHandle b, string n) => LlvmApi.BuildLShr(B, a, b, n);
        internal LlvmValueHandle Cmp(LlvmIntPredicate p, LlvmValueHandle a, LlvmValueHandle b, string n) => LlvmApi.BuildICmp(B, p, a, b, n);
        internal LlvmValueHandle Sel(LlvmValueHandle c, LlvmValueHandle t, LlvmValueHandle f, string n) => LlvmApi.BuildSelect(B, c, t, f, n);
        internal LlvmValueHandle ZExt64(LlvmValueHandle v, string n) => LlvmApi.BuildZExt(B, v, I64, n);
        internal LlvmValueHandle ZExt128(LlvmValueHandle v, string n) => LlvmApi.BuildZExt(B, v, I128, n);
        internal LlvmValueHandle Trunc64(LlvmValueHandle v, string n) => LlvmApi.BuildTrunc(B, v, I64, n);
        internal LlvmValueHandle Trunc8(LlvmValueHandle v, string n) => LlvmApi.BuildTrunc(B, v, I8, n);
        internal void Br(LlvmBasicBlockHandle d) => LlvmApi.BuildBr(B, d);
        internal void CBr(LlvmValueHandle c, LlvmBasicBlockHandle t, LlvmBasicBlockHandle f) => LlvmApi.BuildCondBr(B, c, t, f);
        internal void Ret(LlvmValueHandle v) => LlvmApi.BuildRet(B, v);
        internal void RetVoid() => LlvmApi.BuildRetVoid(B);

        internal LlvmValueHandle Param(LlvmValueHandle fn, uint i) => LlvmApi.GetParam(fn, i);
        internal LlvmValueHandle WordPtr(LlvmValueHandle basePtr, LlvmValueHandle idx, string n) => LlvmApi.BuildGEP2(B, I64, basePtr, [idx], n);
        internal LlvmValueHandle LdW(LlvmValueHandle basePtr, LlvmValueHandle idx, string n) => LlvmApi.BuildLoad2(B, I64, WordPtr(basePtr, idx, n + "_p"), n);
        internal void StW(LlvmValueHandle basePtr, LlvmValueHandle idx, LlvmValueHandle val) => LlvmApi.BuildStore(B, val, WordPtr(basePtr, idx, "wp"));
        internal LlvmValueHandle Count(LlvmValueHandle p, string n) => And(LdW(p, K(0), n + "_h"), K(0xFFFFFFFF), n);
        internal LlvmValueHandle Neg(LlvmValueHandle p, string n) => And(Lshr(LdW(p, K(0), n + "_h2"), K(32), n + "_s"), K(1), n);
        internal LlvmValueHandle Ashr(LlvmValueHandle a, LlvmValueHandle b, string n) => LlvmApi.BuildAShr(B, a, b, n);
        internal LlvmValueHandle URem(LlvmValueHandle a, LlvmValueHandle b, string n) => LlvmApi.BuildURem(B, a, b, n);

        // 32-bit digit view of a magnitude: base-2^32 digits, little-endian (digit 0 = the low half of
        // limb 1). For a bignum pointer the digits start at i32 index 2 (past the 8-byte header);
        // for a raw scratch buffer they start at index 0 — the caller passes the bias.
        internal LlvmValueHandle DigPtr(LlvmValueHandle basePtr, LlvmValueHandle idx, long bias, string n)
            => LlvmApi.BuildGEP2(B, I32, basePtr, [Add(idx, K(bias), n + "_i")], n);
        internal LlvmValueHandle LdDig(LlvmValueHandle basePtr, LlvmValueHandle idx, long bias, string n)
            => ZExt64(LlvmApi.BuildLoad2(B, I32, DigPtr(basePtr, idx, bias, n + "_p"), n + "_32"), n);
        internal void StDig(LlvmValueHandle basePtr, LlvmValueHandle idx, long bias, LlvmValueHandle val64)
            => LlvmApi.BuildStore(B, LlvmApi.BuildTrunc(B, val64, I32, "dig_tr"), DigPtr(basePtr, idx, bias, "dig_dst"));

        internal LlvmValueHandle Declare(string name, LlvmTypeHandle ret, LlvmTypeHandle[] ps)
        {
            LlvmValueHandle fn = LlvmApi.AddFunction(T.Module, name, LlvmApi.FunctionType(ret, ps));
            LlvmApi.SetLinkage(fn, LlvmLinkage.Internal);
            return fn;
        }

        internal LlvmValueHandle Get(string name) => LlvmApi.GetNamedFunction(T.Module, name);
    }

    private static void EmitBigIntRuntimeHelpers(LlvmTargetContext target)
    {
        var e = new Bi(target);
        EmitBiNormalize(e);
        EmitBiCmpMag(e);
        EmitBiAddMag(e);
        EmitBiSubMag(e);
        EmitBiFromI64(e);
        EmitBiCmp(e);
        EmitBiAddSub(e, BigIntAddFn, isSub: false);
        EmitBiAddSub(e, BigIntSubFn, isSub: true);
        EmitBiMul(e);
        EmitBiDivMod(e);
        EmitBiToDecimal(e);
        EmitBiFromDecimal(e);
    }

    // bignum_from_decimal(str, len, out) -> i64 (1 = success, 0 = invalid). Horner: out = out*10 + digit
    // per decimal byte, in place; leading '-' sets the sign. out sized >= len + 2 words by the caller.
    private static void EmitBiFromDecimal(Bi e)
    {
        var fn = e.Declare("bignum_from_decimal", e.I64, [e.Ptr, e.I64, e.Ptr]);
        LlvmValueHandle str = e.Param(fn, 0), len = e.Param(fn, 1), outP = e.Param(fn, 2);
        var entry = e.Blk(fn, "entry");
        var notEmpty = e.Blk(fn, "not_empty");
        var minusBlk = e.Blk(fn, "minus");
        var startLoop = e.Blk(fn, "start_loop");
        var loop = e.Blk(fn, "loop");
        var body = e.Blk(fn, "body");
        var digitOk = e.Blk(fn, "digit_ok");
        var mulLoop = e.Blk(fn, "mul_loop");
        var mulBody = e.Blk(fn, "mul_body");
        var appendCarry = e.Blk(fn, "append_carry");
        var growBlk = e.Blk(fn, "grow");
        var advance = e.Blk(fn, "advance");
        var finish = e.Blk(fn, "finish");
        var invalid = e.Blk(fn, "invalid");

        e.At(entry);
        var iSlot = e.Slot("i");
        var nSlot = e.Slot("n");
        var negSlot = e.Slot("neg");
        var carrySlot = e.Slot("carry");
        var jSlot = e.Slot("j");
        e.St(e.K(0), iSlot);
        e.St(e.K(0), nSlot);
        e.St(e.K(0), negSlot);
        e.CBr(e.Cmp(LlvmIntPredicate.Eq, len, e.K(0), "empty"), invalid, notEmpty);

        e.At(notEmpty);
        var b0 = LlvmApi.BuildLoad2(e.B, e.I8, LlvmApi.BuildGEP2(e.B, e.I8, str, [e.K(0)], "b0p"), "b0");
        var b0w = LlvmApi.BuildZExt(e.B, b0, e.I64, "b0w");
        e.CBr(e.Cmp(LlvmIntPredicate.Eq, b0w, e.K('-'), "is_minus"), minusBlk, startLoop);

        e.At(minusBlk);
        e.St(e.K(1), negSlot);
        e.St(e.K(1), iSlot);
        e.CBr(e.Cmp(LlvmIntPredicate.Eq, len, e.K(1), "only_minus"), invalid, startLoop);

        e.At(startLoop);
        e.Br(loop);
        e.At(loop);
        e.CBr(e.Cmp(LlvmIntPredicate.Slt, e.Ld(iSlot, "i"), len, "i_lt_len"), body, finish);

        e.At(body);
        var iv = e.Ld(iSlot, "iv");
        var bch = LlvmApi.BuildZExt(e.B, LlvmApi.BuildLoad2(e.B, e.I8, LlvmApi.BuildGEP2(e.B, e.I8, str, [iv], "bp"), "b"), e.I64, "bch");
        var tooLow = e.Cmp(LlvmIntPredicate.Slt, bch, e.K('0'), "too_low");
        var tooHigh = e.Cmp(LlvmIntPredicate.Sgt, bch, e.K('9'), "too_high");
        e.CBr(e.Or(tooLow, tooHigh, "bad"), invalid, digitOk);

        e.At(digitOk);
        var digit = e.Sub(bch, e.K('0'), "digit");
        // out = out*10 + digit (in place); carry starts at digit
        e.St(digit, carrySlot);
        e.St(e.K(1), jSlot);
        e.Br(mulLoop);
        e.At(mulLoop);
        e.CBr(e.Cmp(LlvmIntPredicate.Sle, e.Ld(jSlot, "j"), e.Ld(nSlot, "n"), "j_le_n"), mulBody, appendCarry);
        e.At(mulBody);
        var jv = e.Ld(jSlot, "jv");
        var limb = e.LdW(outP, jv, "limb");
        var p = LlvmApi.BuildAdd(e.B, e.Mul(e.ZExt128(limb, "l128"), e.K128(10), "l10"), e.ZExt128(e.Ld(carrySlot, "c"), "c128"), "p");
        e.StW(outP, jv, e.Trunc64(p, "plow"));
        e.St(e.Trunc64(LlvmApi.BuildLShr(e.B, p, e.K128(64), "phi"), "chi"), carrySlot);
        e.St(e.Add(jv, e.K(1), "jinc"), jSlot);
        e.Br(mulLoop);
        e.At(appendCarry);
        e.CBr(e.Cmp(LlvmIntPredicate.Ne, e.Ld(carrySlot, "cf"), e.K(0), "has_carry"), growBlk, advance);
        e.At(growBlk);
        var nn = e.Add(e.Ld(nSlot, "n2"), e.K(1), "n_inc");
        e.St(nn, nSlot);
        e.StW(outP, nn, e.Ld(carrySlot, "cc"));
        e.Br(advance);
        e.At(advance);
        e.St(e.Add(e.Ld(iSlot, "id"), e.K(1), "i_inc"), iSlot);
        e.Br(loop);

        e.At(finish);
        var normTy = LlvmApi.FunctionType(e.Void, [e.Ptr, e.I64, e.I64]);
        LlvmApi.BuildCall2(e.B, normTy, e.Get("bi_normalize"), [outP, e.Ld(negSlot, "neg"), e.Ld(nSlot, "nf")], "");
        e.Ret(e.K(1));

        e.At(invalid);
        e.Ret(e.K(0));
    }

    // Emit a while-loop that decrements n while (n > 0 && p[n] == 0), leaving the final n in nSlot.
    // Builder must be positioned at a block that has already stored the initial n into nSlot; on
    // return the builder is positioned at a fresh 'done' block.
    private static void EmitBiStripZeros(Bi e, LlvmValueHandle fn, LlvmValueHandle p, LlvmValueHandle nSlot, string tag)
    {
        var cond = e.Blk(fn, tag + "_cond");
        var check = e.Blk(fn, tag + "_check");
        var dec = e.Blk(fn, tag + "_dec");
        var done = e.Blk(fn, tag + "_done");
        e.Br(cond);
        e.At(cond);
        e.CBr(e.Cmp(LlvmIntPredicate.Sgt, e.Ld(nSlot, "n"), e.K(0), "n_pos"), check, done);
        e.At(check);
        var top = e.LdW(p, e.Ld(nSlot, "ni"), "top");
        e.CBr(e.Cmp(LlvmIntPredicate.Eq, top, e.K(0), "top0"), dec, done);
        e.At(dec);
        e.St(e.Sub(e.Ld(nSlot, "nd"), e.K(1), "ndec"), nSlot);
        e.Br(cond);
        e.At(done);
    }

    // normalize(out, neg, n): strip leading zeros then set out[0] = (neg<<32)|n (or 0 if zero).
    private static void EmitBiNormalize(Bi e)
    {
        var fn = e.Declare("bi_normalize", e.Void, [e.Ptr, e.I64, e.I64]);
        LlvmValueHandle outP = e.Param(fn, 0), neg = e.Param(fn, 1), n0 = e.Param(fn, 2);
        var entry = e.Blk(fn, "entry");
        e.At(entry);
        var nSlot = e.Slot("n");
        e.St(n0, nSlot);
        EmitBiStripZeros(e, fn, outP, nSlot, "strip");
        var setZero = e.Blk(fn, "set_zero");
        var setHdr = e.Blk(fn, "set_hdr");
        var end = e.Blk(fn, "end");
        var nf = e.Ld(nSlot, "nf");
        e.CBr(e.Cmp(LlvmIntPredicate.Eq, nf, e.K(0), "is0"), setZero, setHdr);
        e.At(setZero);
        e.StW(outP, e.K(0), e.K(0));
        e.Br(end);
        e.At(setHdr);
        e.StW(outP, e.K(0), e.Or(e.Shl(neg, e.K(32), "nsh"), e.Ld(nSlot, "nn"), "hdr"));
        e.Br(end);
        e.At(end);
        e.RetVoid();
    }

    // cmp_mag(a, na, b, nb) -> i64 (-1/0/1)
    private static void EmitBiCmpMag(Bi e)
    {
        var fn = e.Declare("bi_cmp_mag", e.I64, [e.Ptr, e.I64, e.Ptr, e.I64]);
        LlvmValueHandle a = e.Param(fn, 0), na = e.Param(fn, 1), b = e.Param(fn, 2), nb = e.Param(fn, 3);
        var entry = e.Blk(fn, "entry");
        var lenNe = e.Blk(fn, "len_ne");
        var eqLen = e.Blk(fn, "eq_len");
        var loop = e.Blk(fn, "loop");
        var body = e.Blk(fn, "body");
        var chkGt = e.Blk(fn, "chk_gt");
        var retNeg = e.Blk(fn, "ret_neg");
        var retPos = e.Blk(fn, "ret_pos");
        var retZero = e.Blk(fn, "ret_zero");

        e.At(entry);
        e.CBr(e.Cmp(LlvmIntPredicate.Eq, na, nb, "len_eq"), eqLen, lenNe);
        e.At(lenNe);
        e.CBr(e.Cmp(LlvmIntPredicate.Ult, na, nb, "na_lt"), retNeg, retPos);
        e.At(eqLen);
        var iSlot = e.Slot("i");
        e.St(na, iSlot);
        e.Br(loop);
        e.At(loop);
        e.CBr(e.Cmp(LlvmIntPredicate.Sge, e.Ld(iSlot, "i"), e.K(1), "i_ge1"), body, retZero);
        e.At(body);
        var iv = e.Ld(iSlot, "iv");
        var x = e.LdW(a, iv, "x");
        var y = e.LdW(b, iv, "y");
        e.St(e.Sub(iv, e.K(1), "idec"), iSlot);
        e.CBr(e.Cmp(LlvmIntPredicate.Ult, x, y, "x_lt"), retNeg, chkGt);
        e.At(chkGt);
        e.CBr(e.Cmp(LlvmIntPredicate.Ugt, x, y, "x_gt"), retPos, loop);
        e.At(retNeg);
        e.Ret(e.K(-1));
        e.At(retPos);
        e.Ret(e.K(1));
        e.At(retZero);
        e.Ret(e.K(0));
    }

    // add_mag(a, na, b, nb, out) -> i64 limb count
    private static void EmitBiAddMag(Bi e)
    {
        var fn = e.Declare("bi_add_mag", e.I64, [e.Ptr, e.I64, e.Ptr, e.I64, e.Ptr]);
        LlvmValueHandle a = e.Param(fn, 0), na = e.Param(fn, 1), b = e.Param(fn, 2), nb = e.Param(fn, 3), outP = e.Param(fn, 4);
        var entry = e.Blk(fn, "entry");
        var loop = e.Blk(fn, "loop");
        var body = e.Blk(fn, "body");
        var after = e.Blk(fn, "after");
        var carryBlk = e.Blk(fn, "carry");
        var ret = e.Blk(fn, "ret");

        e.At(entry);
        var nSlot = e.Slot("n");
        var iSlot = e.Slot("i");
        var carrySlot = e.Slot("carry");
        e.St(e.Sel(e.Cmp(LlvmIntPredicate.Sgt, na, nb, "na_gt"), na, nb, "nmax"), nSlot);
        e.St(e.K(1), iSlot);
        e.St(e.K(0), carrySlot);
        e.Br(loop);
        e.At(loop);
        e.CBr(e.Cmp(LlvmIntPredicate.Sle, e.Ld(iSlot, "i"), e.Ld(nSlot, "n"), "i_le_n"), body, after);
        e.At(body);
        var i2 = e.Ld(iSlot, "iv");
        var x = e.Sel(e.Cmp(LlvmIntPredicate.Sle, i2, na, "x_in"), e.LdW(a, i2, "ax"), e.K(0), "x");
        var y = e.Sel(e.Cmp(LlvmIntPredicate.Sle, i2, nb, "y_in"), e.LdW(b, i2, "by"), e.K(0), "y");
        var s = e.Add(x, y, "s");
        var c1 = e.ZExt64(e.Cmp(LlvmIntPredicate.Ult, s, x, "c1b"), "c1");
        var carry = e.Ld(carrySlot, "carry");
        var s2 = e.Add(s, carry, "s2");
        var c2 = e.ZExt64(e.Cmp(LlvmIntPredicate.Ult, s2, s, "c2b"), "c2");
        e.StW(outP, i2, s2);
        e.St(e.Add(c1, c2, "nc"), carrySlot);
        e.St(e.Add(i2, e.K(1), "inc"), iSlot);
        e.Br(loop);
        e.At(after);
        e.CBr(e.Cmp(LlvmIntPredicate.Ne, e.Ld(carrySlot, "cf"), e.K(0), "has_c"), carryBlk, ret);
        e.At(carryBlk);
        var nInc = e.Add(e.Ld(nSlot, "n2"), e.K(1), "n_inc");
        e.St(nInc, nSlot);
        e.StW(outP, nInc, e.Ld(carrySlot, "cc"));
        e.Br(ret);
        e.At(ret);
        e.Ret(e.Ld(nSlot, "nret"));
    }

    // sub_mag(a, na, b, nb, out) -> i64 (returns na); requires |a| >= |b|.
    private static void EmitBiSubMag(Bi e)
    {
        var fn = e.Declare("bi_sub_mag", e.I64, [e.Ptr, e.I64, e.Ptr, e.I64, e.Ptr]);
        LlvmValueHandle a = e.Param(fn, 0), na = e.Param(fn, 1), b = e.Param(fn, 2), nb = e.Param(fn, 3), outP = e.Param(fn, 4);
        var entry = e.Blk(fn, "entry");
        var loop = e.Blk(fn, "loop");
        var body = e.Blk(fn, "body");
        var ret = e.Blk(fn, "ret");

        e.At(entry);
        var iSlot = e.Slot("i");
        var borrowSlot = e.Slot("borrow");
        e.St(e.K(1), iSlot);
        e.St(e.K(0), borrowSlot);
        e.Br(loop);
        e.At(loop);
        e.CBr(e.Cmp(LlvmIntPredicate.Sle, e.Ld(iSlot, "i"), na, "i_le"), body, ret);
        e.At(body);
        var i2 = e.Ld(iSlot, "iv");
        var x = e.LdW(a, i2, "x");
        var y = e.Sel(e.Cmp(LlvmIntPredicate.Sle, i2, nb, "y_in"), e.LdW(b, i2, "by"), e.K(0), "y");
        var d = e.Sub(x, y, "d");
        var b1 = e.ZExt64(e.Cmp(LlvmIntPredicate.Ult, x, y, "b1b"), "b1");
        var borrow = e.Ld(borrowSlot, "borrow");
        var d2 = e.Sub(d, borrow, "d2");
        var b2 = e.ZExt64(e.Cmp(LlvmIntPredicate.Ult, d, borrow, "b2b"), "b2");
        e.StW(outP, i2, d2);
        e.St(e.Add(b1, b2, "nb2"), borrowSlot);
        e.St(e.Add(i2, e.K(1), "inc"), iSlot);
        e.Br(loop);
        e.At(ret);
        e.Ret(na);
    }

    private static void EmitBiFromI64(Bi e)
    {
        var fn = e.Declare(BigIntFromI64, e.Void, [e.I64, e.Ptr]);
        LlvmValueHandle n = e.Param(fn, 0), outP = e.Param(fn, 1);
        var entry = e.Blk(fn, "entry");
        var zero = e.Blk(fn, "zero");
        var nonZero = e.Blk(fn, "nonzero");
        var end = e.Blk(fn, "end");
        e.At(entry);
        e.CBr(e.Cmp(LlvmIntPredicate.Eq, n, e.K(0), "is0"), zero, nonZero);
        e.At(zero);
        e.StW(outP, e.K(0), e.K(0));
        e.Br(end);
        e.At(nonZero);
        var isNeg = e.Cmp(LlvmIntPredicate.Slt, n, e.K(0), "is_neg");
        var negI = e.Sel(isNeg, e.K(1), e.K(0), "neg");
        var mag = e.Sel(isNeg, e.Sub(e.K(0), n, "negn"), n, "mag"); // |n| via wrapping 0-n (INT64_MIN safe)
        e.StW(outP, e.K(0), e.Or(e.Shl(negI, e.K(32), "nsh"), e.K(1), "hdr"));
        e.StW(outP, e.K(1), mag);
        e.Br(end);
        e.At(end);
        e.RetVoid();
    }

    private static void EmitBiCmp(Bi e)
    {
        var fn = e.Declare(BigIntCmp, e.I64, [e.Ptr, e.Ptr]);
        LlvmValueHandle a = e.Param(fn, 0), b = e.Param(fn, 1);
        var entry = e.Blk(fn, "entry");
        var diffSign = e.Blk(fn, "diff_sign");
        var sameSign = e.Blk(fn, "same_sign");
        var retNeg = e.Blk(fn, "ret_neg");
        var retPos = e.Blk(fn, "ret_pos");
        e.At(entry);
        var na = e.Count(a, "na");
        var nb = e.Count(b, "nb");
        var sa = e.Sel(e.Cmp(LlvmIntPredicate.Ne, na, e.K(0), "na_nz"), e.Neg(a, "sa0"), e.K(0), "sa");
        var sb = e.Sel(e.Cmp(LlvmIntPredicate.Ne, nb, e.K(0), "nb_nz"), e.Neg(b, "sb0"), e.K(0), "sb");
        e.CBr(e.Cmp(LlvmIntPredicate.Eq, sa, sb, "sign_eq"), sameSign, diffSign);
        e.At(diffSign);
        e.CBr(e.Cmp(LlvmIntPredicate.Ne, sa, e.K(0), "a_neg"), retNeg, retPos);
        e.At(sameSign);
        var cmpTy = LlvmApi.FunctionType(e.I64, [e.Ptr, e.I64, e.Ptr, e.I64]);
        var c = LlvmApi.BuildCall2(e.B, cmpTy, e.Get("bi_cmp_mag"), [a, na, b, nb], "c");
        var res = e.Sel(e.Cmp(LlvmIntPredicate.Ne, sa, e.K(0), "both_neg"), e.Sub(e.K(0), c, "negc"), c, "res");
        e.Ret(res);
        e.At(retNeg);
        e.Ret(e.K(-1));
        e.At(retPos);
        e.Ret(e.K(1));
    }

    // Copy src[1..n] into out[1..n] then normalize(out, sign, n). Builder must be positioned at the
    // calling block; on return it is positioned at a fresh 'after' block.
    private static void EmitBiCopyMag(Bi e, LlvmValueHandle fn, LlvmValueHandle src, LlvmValueHandle n, LlvmValueHandle sign, LlvmValueHandle outP, string tag)
    {
        var pre = e.Blk(fn, tag + "_pre");
        var loop = e.Blk(fn, tag + "_loop");
        var body = e.Blk(fn, tag + "_body");
        var after = e.Blk(fn, tag + "_after");
        e.Br(pre);
        e.At(pre);
        var iSlot = e.Slot(tag + "_i");
        e.St(e.K(1), iSlot);
        e.Br(loop);
        e.At(loop);
        e.CBr(e.Cmp(LlvmIntPredicate.Sle, e.Ld(iSlot, "i"), n, "i_le"), body, after);
        e.At(body);
        var i2 = e.Ld(iSlot, "iv");
        e.StW(outP, i2, e.LdW(src, i2, "cw"));
        e.St(e.Add(i2, e.K(1), "inc"), iSlot);
        e.Br(loop);
        e.At(after);
        var normTy = LlvmApi.FunctionType(e.Void, [e.Ptr, e.I64, e.I64]);
        LlvmApi.BuildCall2(e.B, normTy, e.Get("bi_normalize"), [outP, sign, n], "");
    }

    // add (isSub=false) / sub (isSub=true): out = a ± b (signed).
    private static void EmitBiAddSub(Bi e, string name, bool isSub)
    {
        var fn = e.Declare(name, e.Void, [e.Ptr, e.Ptr, e.Ptr]);
        LlvmValueHandle a = e.Param(fn, 0), b = e.Param(fn, 1), outP = e.Param(fn, 2);
        var entry = e.Blk(fn, "entry");
        var aZero = e.Blk(fn, "a_zero");
        var chkBZero = e.Blk(fn, "chk_bzero");
        var bZero = e.Blk(fn, "b_zero");
        var main = e.Blk(fn, "main");
        var sameSign = e.Blk(fn, "same_sign");
        var diffSign = e.Blk(fn, "diff_sign");
        var aBigger = e.Blk(fn, "a_bigger");
        var subAB = e.Blk(fn, "sub_ab");
        var subBA = e.Blk(fn, "sub_ba");
        var eqZero = e.Blk(fn, "eq_zero");
        var end = e.Blk(fn, "end");

        var magTy = LlvmApi.FunctionType(e.I64, [e.Ptr, e.I64, e.Ptr, e.I64, e.Ptr]);
        var cmpTy = LlvmApi.FunctionType(e.I64, [e.Ptr, e.I64, e.Ptr, e.I64]);
        var normTy = LlvmApi.FunctionType(e.Void, [e.Ptr, e.I64, e.I64]);

        e.At(entry);
        var na = e.Count(a, "na");
        var nb = e.Count(b, "nb");
        var sa = e.Neg(a, "sa");
        var sbRaw = e.Neg(b, "sb0");
        var sb = isSub ? e.And(e.Add(sbRaw, e.K(1), "flip"), e.K(1), "sb") : sbRaw;
        e.CBr(e.Cmp(LlvmIntPredicate.Eq, na, e.K(0), "a_is0"), aZero, chkBZero);

        e.At(aZero);
        EmitBiCopyMag(e, fn, b, nb, sb, outP, "cpb"); // out = ±b
        e.Br(end);

        e.At(chkBZero);
        e.CBr(e.Cmp(LlvmIntPredicate.Eq, nb, e.K(0), "b_is0"), bZero, main);
        e.At(bZero);
        EmitBiCopyMag(e, fn, a, na, sa, outP, "cpa"); // out = a
        e.Br(end);

        e.At(main);
        e.CBr(e.Cmp(LlvmIntPredicate.Eq, sa, sb, "same"), sameSign, diffSign);

        e.At(sameSign);
        var addN = LlvmApi.BuildCall2(e.B, magTy, e.Get("bi_add_mag"), [a, na, b, nb, outP], "add_n");
        LlvmApi.BuildCall2(e.B, normTy, e.Get("bi_normalize"), [outP, sa, addN], "");
        e.Br(end);

        e.At(diffSign);
        var c = LlvmApi.BuildCall2(e.B, cmpTy, e.Get("bi_cmp_mag"), [a, na, b, nb], "c");
        e.CBr(e.Cmp(LlvmIntPredicate.Eq, c, e.K(0), "mag_eq"), eqZero, aBigger);
        e.At(aBigger);
        e.CBr(e.Cmp(LlvmIntPredicate.Sgt, c, e.K(0), "a_big"), subAB, subBA);

        e.At(subAB);
        var s1 = LlvmApi.BuildCall2(e.B, magTy, e.Get("bi_sub_mag"), [a, na, b, nb, outP], "sab");
        LlvmApi.BuildCall2(e.B, normTy, e.Get("bi_normalize"), [outP, sa, s1], "");
        e.Br(end);

        e.At(subBA);
        var s2 = LlvmApi.BuildCall2(e.B, magTy, e.Get("bi_sub_mag"), [b, nb, a, na, outP], "sba");
        LlvmApi.BuildCall2(e.B, normTy, e.Get("bi_normalize"), [outP, sb, s2], "");
        e.Br(end);

        e.At(eqZero);
        e.StW(outP, e.K(0), e.K(0));
        e.Br(end);

        e.At(end);
        e.RetVoid();
    }

    // mul: out = a*b (schoolbook, i128 partial products).
    private static void EmitBiMul(Bi e)
    {
        var fn = e.Declare(BigIntMulFn, e.Void, [e.Ptr, e.Ptr, e.Ptr]);
        LlvmValueHandle a = e.Param(fn, 0), b = e.Param(fn, 1), outP = e.Param(fn, 2);
        var entry = e.Blk(fn, "entry");
        var zero = e.Blk(fn, "zero");
        var initLoop = e.Blk(fn, "init_loop");
        var initBody = e.Blk(fn, "init_body");
        var outerLoop = e.Blk(fn, "outer");
        var outerBody = e.Blk(fn, "outer_body");
        var innerLoop = e.Blk(fn, "inner");
        var innerBody = e.Blk(fn, "inner_body");
        var innerDone = e.Blk(fn, "inner_done");
        var done = e.Blk(fn, "done");
        var end = e.Blk(fn, "end");

        var normTy = LlvmApi.FunctionType(e.Void, [e.Ptr, e.I64, e.I64]);

        e.At(entry);
        var na = e.Count(a, "na");
        var nb = e.Count(b, "nb");
        var iSlot = e.Slot("i");
        var jSlot = e.Slot("j");
        var kSlot = e.Slot("k");
        var carrySlot = e.Slot("carry");
        var total = e.Add(na, nb, "total");
        var anyZero = e.Or(e.ZExt64(e.Cmp(LlvmIntPredicate.Eq, na, e.K(0), "na0"), "na0z"),
                           e.ZExt64(e.Cmp(LlvmIntPredicate.Eq, nb, e.K(0), "nb0"), "nb0z"), "anyz");
        e.St(e.K(1), kSlot); // init out-zeroing index (entry dominates initLoop)
        e.CBr(e.Cmp(LlvmIntPredicate.Ne, anyZero, e.K(0), "is0"), zero, initLoop);

        e.At(zero);
        e.StW(outP, e.K(0), e.K(0));
        e.Br(end);

        // init out[1..total] = 0
        e.At(initLoop);
        e.CBr(e.Cmp(LlvmIntPredicate.Sle, e.Ld(kSlot, "k"), total, "k_le"), initBody, outerLoop);
        e.At(initBody);
        var kv = e.Ld(kSlot, "kv");
        e.StW(outP, kv, e.K(0));
        e.St(e.Add(kv, e.K(1), "kinc"), kSlot);
        e.Br(initLoop);

        // outer i = 1..na
        e.At(outerLoop);
        e.St(e.K(1), iSlot);
        e.Br(outerBody);
        e.At(outerBody);
        e.CBr(e.Cmp(LlvmIntPredicate.Sle, e.Ld(iSlot, "i"), na, "i_le"), innerLoop, done);

        // inner j = 1..nb
        e.At(innerLoop);
        e.St(e.K(1), jSlot);
        e.St(e.K(0), carrySlot);
        e.Br(innerBody);
        e.At(innerBody);
        var jv = e.Ld(jSlot, "j");
        var proceed = e.Cmp(LlvmIntPredicate.Sle, jv, nb, "j_le");
        var innerStep = e.Blk(fn, "inner_step");
        e.CBr(proceed, innerStep, innerDone);
        e.At(innerStep);
        var iv = e.Ld(iSlot, "iv");
        var ai = e.LdW(a, iv, "ai");
        var bj = e.LdW(b, jv, "bj");
        var idx = e.Sub(e.Add(iv, jv, "ipj"), e.K(1), "idx"); // i+j-1
        var cur = e.LdW(outP, idx, "cur");
        var carry = e.Ld(carrySlot, "carry");
        var p = LlvmApi.BuildAdd(e.B,
            LlvmApi.BuildAdd(e.B, e.Mul(e.ZExt128(ai, "ai128"), e.ZExt128(bj, "bj128"), "prod"), e.ZExt128(cur, "cur128"), "p1"),
            e.ZExt128(carry, "carry128"), "p");
        e.StW(outP, idx, e.Trunc64(p, "plow"));
        e.St(e.Trunc64(LlvmApi.BuildLShr(e.B, p, e.K128(64), "phi"), "carry_next"), carrySlot);
        e.St(e.Add(jv, e.K(1), "jinc"), jSlot);
        e.Br(innerBody);

        e.At(innerDone);
        var ivd = e.Ld(iSlot, "ivd");
        e.StW(outP, e.Add(ivd, nb, "i_nb"), e.Ld(carrySlot, "cend"));
        e.St(e.Add(ivd, e.K(1), "iinc"), iSlot);
        e.Br(outerBody);

        e.At(done);
        var neg = e.And(LlvmApi.BuildXor(e.B, e.Neg(a, "san"), e.Neg(b, "sbn"), "sx"), e.K(1), "neg");
        LlvmApi.BuildCall2(e.B, normTy, e.Get("bi_normalize"), [outP, neg, total], "");
        e.Br(end);

        e.At(end);
        e.RetVoid();
    }

    // divmod(a, b, q, r, scratch): Knuth Algorithm D in base 2^32 (the Hacker's Delight divmnu64
    // formulation), truncated toward zero. Digits are 32-bit halves of the 64-bit limbs, so every
    // intermediate (two-digit dividends, digit products, signed borrows) fits native i64 arithmetic —
    // no i128 division, hence no __udivti3 libcall in the freestanding binary. Replaces the previous
    // bit-by-bit binary long division: the outer loop runs once per QUOTIENT DIGIT instead of once
    // per DIVIDEND BIT (~32x fewer iterations at the same O(n) inner cost). scratch holds the
    // normalized divisor and working dividend: vn digits at [0..n-1], un digits at [n..n+m]
    // (la + lb + 4 words, allocated by the caller).
    private static void EmitBiDivMod(Bi e)
    {
        var fn = e.Declare(BigIntDivModFn, e.Void, [e.Ptr, e.Ptr, e.Ptr, e.Ptr, e.Ptr]);
        LlvmValueHandle a = e.Param(fn, 0), b = e.Param(fn, 1), q = e.Param(fn, 2), r = e.Param(fn, 3), scratch = e.Param(fn, 4);
        var entry = e.Blk(fn, "entry");
        var trivial = e.Blk(fn, "trivial");
        var copyR = e.Blk(fn, "copy_r");
        var zeroR = e.Blk(fn, "zero_r");
        var proceed = e.Blk(fn, "proceed");

        var cmpTy = LlvmApi.FunctionType(e.I64, [e.Ptr, e.I64, e.Ptr, e.I64]);
        var normTy = LlvmApi.FunctionType(e.Void, [e.Ptr, e.I64, e.I64]);
        var big = e.K(0x100000000); // the base, 2^32

        e.At(entry);
        var na = e.Count(a, "na");
        var nb = e.Count(b, "nb");
        // trivial (q=0) when nb==0 or na==0 or |a|<|b|
        var nbZero = e.Cmp(LlvmIntPredicate.Eq, nb, e.K(0), "nb0");
        var naZero = e.Cmp(LlvmIntPredicate.Eq, na, e.K(0), "na0");
        var degenerate = e.Or(e.ZExt64(nbZero, "z1"), e.ZExt64(naZero, "z2"), "degen");
        var cmp = LlvmApi.BuildCall2(e.B, cmpTy, e.Get("bi_cmp_mag"), [a, na, b, nb], "cmp");
        var aSmaller = e.ZExt64(e.Cmp(LlvmIntPredicate.Slt, cmp, e.K(0), "a_lt"), "als");
        var isTrivial = e.Or(degenerate, aSmaller, "triv");
        e.CBr(e.Cmp(LlvmIntPredicate.Ne, isTrivial, e.K(0), "is_triv"), trivial, proceed);

        e.At(trivial);
        e.StW(q, e.K(0), e.K(0)); // quotient 0
        // remainder = a when |a|<|b| and non-degenerate, else 0
        var remIsA = e.And(e.ZExt64(e.Cmp(LlvmIntPredicate.Eq, degenerate, e.K(0), "not_degen"), "nd"), aSmaller, "rem_is_a");
        e.CBr(e.Cmp(LlvmIntPredicate.Ne, remIsA, e.K(0), "rema"), copyR, zeroR);
        e.At(copyR);
        EmitBiCopyMag(e, fn, a, na, e.Neg(a, "ra_sign"), r, "cpr");
        e.RetVoid();
        e.At(zeroR);
        e.StW(r, e.K(0), e.K(0));
        e.RetVoid();

        e.At(proceed);
        var qInitLoop = e.Blk(fn, "qinit_loop");
        var qInitBody = e.Blk(fn, "qinit_body");
        var countDigits = e.Blk(fn, "count_digits");
        var shortDiv = e.Blk(fn, "short_div");
        var longDiv = e.Blk(fn, "long_div");
        var finish = e.Blk(fn, "finish");

        var idxSlot = e.Slot("idx");
        var rnSlot = e.Slot("rn");

        // q limbs = 0 for [1..na] — quotient digits are written as 32-bit stores below, so the
        // whole limb area must start zeroed.
        e.St(e.K(1), idxSlot);
        e.Br(qInitLoop);
        e.At(qInitLoop);
        e.CBr(e.Cmp(LlvmIntPredicate.Sle, e.Ld(idxSlot, "k"), na, "k_le"), qInitBody, countDigits);
        e.At(qInitBody);
        var kv = e.Ld(idxSlot, "kv");
        e.StW(q, kv, e.K(0));
        e.St(e.Add(kv, e.K(1), "kinc"), idxSlot);
        e.Br(qInitLoop);

        // Digit counts: m/n = 2*limbs, minus one when the top 32-bit digit is zero (Algorithm D
        // requires a non-zero top divisor digit; |a| >= |b| here, so m >= n >= 1).
        e.At(countDigits);
        var m0 = e.Mul(na, e.K(2), "m0");
        var mTop = e.LdDig(a, e.Sub(m0, e.K(1), "m0_1"), 2, "m_top");
        var m = e.Sel(e.Cmp(LlvmIntPredicate.Eq, mTop, e.K(0), "mtz"), e.Sub(m0, e.K(1), "m_dec"), m0, "m");
        var n0 = e.Mul(nb, e.K(2), "n0");
        var nTop = e.LdDig(b, e.Sub(n0, e.K(1), "n0_1"), 2, "n_top");
        var n = e.Sel(e.Cmp(LlvmIntPredicate.Eq, nTop, e.K(0), "ntz"), e.Sub(n0, e.K(1), "n_dec"), n0, "n");
        e.CBr(e.Cmp(LlvmIntPredicate.Eq, n, e.K(1), "n_is_1"), shortDiv, longDiv);

        // ── Short division: single-digit divisor, one native 64/32 divide per dividend digit ──
        {
            e.At(shortDiv);
            var sdLoop = e.Blk(fn, "sd_loop");
            var sdBody = e.Blk(fn, "sd_body");
            var sdDone = e.Blk(fn, "sd_done");
            var v0 = e.LdDig(b, e.K(0), 2, "v0");
            var remSlot = e.Slot("sd_rem");
            e.St(e.K(0), remSlot);
            e.St(e.Sub(m, e.K(1), "sd_j0"), idxSlot);
            e.Br(sdLoop);
            e.At(sdLoop);
            e.CBr(e.Cmp(LlvmIntPredicate.Sge, e.Ld(idxSlot, "sd_j"), e.K(0), "sd_more"), sdBody, sdDone);
            e.At(sdBody);
            var j = e.Ld(idxSlot, "sd_jv");
            var cur = e.Or(e.Shl(e.Ld(remSlot, "sd_r"), e.K(32), "sd_rs"), e.LdDig(a, j, 2, "sd_u"), "sd_cur");
            e.StDig(q, j, 2, e.UDiv(cur, v0, "sd_qd"));
            e.St(e.URem(cur, v0, "sd_rr"), remSlot);
            e.St(e.Sub(j, e.K(1), "sd_jd"), idxSlot);
            e.Br(sdLoop);
            e.At(sdDone);
            var remV = e.Ld(remSlot, "sd_remf");
            e.StW(r, e.K(1), remV);
            e.St(e.Sel(e.Cmp(LlvmIntPredicate.Eq, remV, e.K(0), "sd_rz"), e.K(0), e.K(1), "sd_rn"), rnSlot);
            e.Br(finish);
        }

        // ── Algorithm D proper (n >= 2) ──────────────────────────────────────────────────────
        {
            e.At(longDiv);
            // s = nlz32(vn top digit): shift so the divisor's top digit has its high bit set.
            var sSlot = e.Slot("d_s");
            var tSlot = e.Slot("d_t");
            var nlzLoop = e.Blk(fn, "nlz_loop");
            var nlzBody = e.Blk(fn, "nlz_body");
            var nlzDone = e.Blk(fn, "nlz_done");
            e.St(e.K(0), sSlot);
            e.St(e.LdDig(b, e.Sub(n, e.K(1), "vt_i"), 2, "vt"), tSlot);
            e.Br(nlzLoop);
            e.At(nlzLoop);
            e.CBr(e.Cmp(LlvmIntPredicate.Ult, e.Ld(tSlot, "t"), e.K(0x80000000), "t_lo"), nlzBody, nlzDone);
            e.At(nlzBody);
            e.St(e.Shl(e.Ld(tSlot, "t2"), e.K(1), "t_shl"), tSlot);
            e.St(e.Add(e.Ld(sSlot, "s2"), e.K(1), "s_inc"), sSlot);
            e.Br(nlzLoop);
            e.At(nlzDone);
            var s = e.Ld(sSlot, "s");
            var rs = e.Sub(e.K(32), s, "rs"); // right-shift amount; a zext'd digit >> 32 is 0, so s == 0 is uniform

            // vn (normalized divisor) at scratch digits [0..n-1].
            var vnLoop = e.Blk(fn, "vn_loop");
            var vnBody = e.Blk(fn, "vn_body");
            var vnDone = e.Blk(fn, "vn_done");
            e.St(e.Sub(n, e.K(1), "vn_i0"), idxSlot);
            e.Br(vnLoop);
            e.At(vnLoop);
            e.CBr(e.Cmp(LlvmIntPredicate.Sge, e.Ld(idxSlot, "vn_i"), e.K(1), "vn_more"), vnBody, vnDone);
            e.At(vnBody);
            var vi = e.Ld(idxSlot, "vn_iv");
            var vnVal = e.Or(
                e.Shl(e.LdDig(b, vi, 2, "vn_hi"), s, "vn_hs"),
                e.Lshr(e.LdDig(b, e.Sub(vi, e.K(1), "vn_i1"), 2, "vn_lo"), rs, "vn_ls"), "vn_val");
            e.StDig(scratch, vi, 0, vnVal);
            e.St(e.Sub(vi, e.K(1), "vn_id"), idxSlot);
            e.Br(vnLoop);
            e.At(vnDone);
            e.StDig(scratch, e.K(0), 0, e.Shl(e.LdDig(b, e.K(0), 2, "vn_b0"), s, "vn_b0s"));

            // un (normalized dividend, m+1 digits) at scratch digits [n..n+m].
            var unLoop = e.Blk(fn, "un_loop");
            var unBody = e.Blk(fn, "un_body");
            var unDone = e.Blk(fn, "un_done");
            e.StDig(scratch, e.Add(n, m, "un_top_i"), 0, e.Lshr(e.LdDig(a, e.Sub(m, e.K(1), "un_m1"), 2, "un_top_src"), rs, "un_top"));
            e.St(e.Sub(m, e.K(1), "un_i0"), idxSlot);
            e.Br(unLoop);
            e.At(unLoop);
            e.CBr(e.Cmp(LlvmIntPredicate.Sge, e.Ld(idxSlot, "un_i"), e.K(1), "un_more"), unBody, unDone);
            e.At(unBody);
            var ui = e.Ld(idxSlot, "un_iv");
            var unVal = e.Or(
                e.Shl(e.LdDig(a, ui, 2, "un_hi"), s, "un_hs"),
                e.Lshr(e.LdDig(a, e.Sub(ui, e.K(1), "un_i1"), 2, "un_lo"), rs, "un_ls"), "un_val");
            e.StDig(scratch, e.Add(n, ui, "un_di"), 0, unVal);
            e.St(e.Sub(ui, e.K(1), "un_id"), idxSlot);
            e.Br(unLoop);
            e.At(unDone);
            e.StDig(scratch, n, 0, e.Shl(e.LdDig(a, e.K(0), 2, "un_a0"), s, "un_a0s"));

            // Main loop: one quotient digit per iteration, j = m-n down to 0.
            var jSlot = e.Slot("d_j");
            var qhatSlot = e.Slot("d_qhat");
            var rhatSlot = e.Slot("d_rhat");
            var kSlot = e.Slot("d_k");
            var iSlot = e.Slot("d_i");
            var jLoop = e.Blk(fn, "j_loop");
            var jBody = e.Blk(fn, "j_body");
            var corrCheck = e.Blk(fn, "corr_check");
            var corrMul = e.Blk(fn, "corr_mul");
            var corrDec = e.Blk(fn, "corr_dec");
            var mulSub = e.Blk(fn, "mul_sub");
            var msLoop = e.Blk(fn, "ms_loop");
            var msBody = e.Blk(fn, "ms_body");
            var msDone = e.Blk(fn, "ms_done");
            var addBack = e.Blk(fn, "add_back");
            var abLoop = e.Blk(fn, "ab_loop");
            var abBody = e.Blk(fn, "ab_body");
            var abDone = e.Blk(fn, "ab_done");
            var jNext = e.Blk(fn, "j_next");
            var denorm = e.Blk(fn, "denorm");

            var vtn = e.LdDig(scratch, e.Sub(n, e.K(1), "vtn_i"), 0, "vtn");
            var vtn2 = e.LdDig(scratch, e.Sub(n, e.K(2), "vtn2_i"), 0, "vtn2");

            e.St(e.Sub(m, n, "j0"), jSlot);
            e.Br(jLoop);
            e.At(jLoop);
            e.CBr(e.Cmp(LlvmIntPredicate.Sge, e.Ld(jSlot, "j"), e.K(0), "j_more"), jBody, denorm);

            e.At(jBody);
            var jv = e.Ld(jSlot, "jv");
            // qhat = (un[j+n]*B + un[j+n-1]) / vn[n-1]; rhat = the remainder. Both dividend digits
            // fit one i64, and vtn >= 2^31 after normalization.
            var numHi = e.LdDig(scratch, e.Add(n, e.Add(jv, n, "jn"), "jn_i"), 0, "num_hi");
            var numLo = e.LdDig(scratch, e.Add(n, e.Sub(e.Add(jv, n, "jn2"), e.K(1), "jn_1"), "jn1_i"), 0, "num_lo");
            var num = e.Or(e.Shl(numHi, e.K(32), "num_hs"), numLo, "num");
            e.St(e.UDiv(num, vtn, "qhat0"), qhatSlot);
            e.St(e.URem(num, vtn, "rhat0"), rhatSlot);
            e.Br(corrCheck);

            // Correct qhat down (at most twice): while qhat >= B, or qhat*vn[n-2] overshoots
            // rhat*B + un[j+n-2]. The multiply is only evaluated once qhat < B (else it could
            // overflow i64) — the short-circuit mirrors the C formulation.
            e.At(corrCheck);
            e.CBr(e.Cmp(LlvmIntPredicate.Uge, e.Ld(qhatSlot, "qh_c"), big, "qh_big"), corrDec, corrMul);
            e.At(corrMul);
            var prod = e.Mul(e.Ld(qhatSlot, "qh_m"), vtn2, "corr_prod");
            var rhs = e.Or(e.Shl(e.Ld(rhatSlot, "rh_m"), e.K(32), "rh_s"), e.LdDig(scratch, e.Add(n, e.Sub(e.Add(e.Ld(jSlot, "j_m"), n, "jn3"), e.K(2), "jn_2"), "jn2_i"), 0, "un_jn2"), "corr_rhs");
            e.CBr(e.Cmp(LlvmIntPredicate.Ugt, prod, rhs, "overshoot"), corrDec, mulSub);
            e.At(corrDec);
            e.St(e.Sub(e.Ld(qhatSlot, "qh_d"), e.K(1), "qh_dec"), qhatSlot);
            var rhNew = e.Add(e.Ld(rhatSlot, "rh_d"), vtn, "rh_inc");
            e.St(rhNew, rhatSlot);
            e.CBr(e.Cmp(LlvmIntPredicate.Ult, rhNew, big, "rh_small"), corrCheck, mulSub);

            // Multiply-and-subtract qhat*vn from un[j..j+n], tracking a SIGNED borrow k
            // (p's high half via logical shift, t's sign via arithmetic shift).
            e.At(mulSub);
            e.St(e.K(0), kSlot);
            e.St(e.K(0), iSlot);
            e.Br(msLoop);
            e.At(msLoop);
            e.CBr(e.Cmp(LlvmIntPredicate.Slt, e.Ld(iSlot, "ms_i"), n, "ms_more"), msBody, msDone);
            e.At(msBody);
            var msI = e.Ld(iSlot, "ms_iv");
            var msJ = e.Ld(jSlot, "ms_j");
            var p = e.Mul(e.Ld(qhatSlot, "ms_qh"), e.LdDig(scratch, msI, 0, "ms_vn"), "ms_p");
            var unIj = e.Add(n, e.Add(msI, msJ, "ms_ij"), "ms_ij_i");
            var t = e.Sub(e.Sub(e.LdDig(scratch, unIj, 0, "ms_un"), e.Ld(kSlot, "ms_k"), "ms_t1"), e.And(p, e.K(0xFFFFFFFF), "ms_pl"), "ms_t");
            e.StDig(scratch, unIj, 0, t);
            e.St(e.Sub(e.Lshr(p, e.K(32), "ms_ph"), e.Ashr(t, e.K(32), "ms_ts"), "ms_k2"), kSlot);
            e.St(e.Add(msI, e.K(1), "ms_inc"), iSlot);
            e.Br(msLoop);
            e.At(msDone);
            var jvd = e.Ld(jSlot, "msd_j");
            var unJn = e.Add(n, e.Add(jvd, n, "msd_jn"), "msd_jn_i");
            var t2 = e.Sub(e.LdDig(scratch, unJn, 0, "msd_un"), e.Ld(kSlot, "msd_k"), "msd_t2");
            e.StDig(scratch, unJn, 0, t2);
            e.StDig(q, jvd, 2, e.Ld(qhatSlot, "msd_qh"));
            e.CBr(e.Cmp(LlvmIntPredicate.Slt, t2, e.K(0), "underflow"), addBack, jNext);

            // Rare add-back (qhat was one too large): q[j]--, un += vn.
            e.At(addBack);
            e.StDig(q, e.Ld(jSlot, "ab_j"), 2, e.Sub(e.Ld(qhatSlot, "ab_qh"), e.K(1), "ab_dec"));
            e.St(e.K(0), kSlot);
            e.St(e.K(0), iSlot);
            e.Br(abLoop);
            e.At(abLoop);
            e.CBr(e.Cmp(LlvmIntPredicate.Slt, e.Ld(iSlot, "ab_i"), n, "ab_more"), abBody, abDone);
            e.At(abBody);
            var abI = e.Ld(iSlot, "ab_iv");
            var abIj = e.Add(n, e.Add(abI, e.Ld(jSlot, "ab_j2"), "ab_ij"), "ab_ij_i");
            var t3 = e.Add(e.Add(e.LdDig(scratch, abIj, 0, "ab_un"), e.LdDig(scratch, abI, 0, "ab_vn"), "ab_s1"), e.Ld(kSlot, "ab_k"), "ab_t3");
            e.StDig(scratch, abIj, 0, t3);
            e.St(e.Lshr(t3, e.K(32), "ab_carry"), kSlot);
            e.St(e.Add(abI, e.K(1), "ab_inc"), iSlot);
            e.Br(abLoop);
            e.At(abDone);
            var abJn = e.Add(n, e.Add(e.Ld(jSlot, "ab_j3"), n, "ab_jn"), "ab_jn_i");
            e.StDig(scratch, abJn, 0, e.Add(e.LdDig(scratch, abJn, 0, "ab_top"), e.Ld(kSlot, "ab_kf"), "ab_top2"));
            e.Br(jNext);

            e.At(jNext);
            e.St(e.Sub(e.Ld(jSlot, "j_d"), e.K(1), "j_dec"), jSlot);
            e.Br(jLoop);

            // Remainder: denormalize un[0..n-1] >> s into r's digits (the i64 shifts make the
            // s == 0 case uniform: << 32 truncates away, >> 32 zeroes).
            e.At(denorm);
            var dnLoop = e.Blk(fn, "dn_loop");
            var dnBody = e.Blk(fn, "dn_body");
            var dnDone = e.Blk(fn, "dn_done");
            e.St(e.K(0), idxSlot);
            e.Br(dnLoop);
            e.At(dnLoop);
            e.CBr(e.Cmp(LlvmIntPredicate.Slt, e.Ld(idxSlot, "dn_i"), n, "dn_more"), dnBody, dnDone);
            e.At(dnBody);
            var di = e.Ld(idxSlot, "dn_iv");
            var rd = e.Or(
                e.Lshr(e.LdDig(scratch, e.Add(n, di, "dn_di"), 0, "dn_lo"), s, "dn_lsh"),
                e.Shl(e.LdDig(scratch, e.Add(n, e.Add(di, e.K(1), "dn_i1"), "dn_di1"), 0, "dn_hi"), rs, "dn_hsh"), "dn_val");
            e.StDig(r, di, 2, rd);
            e.St(e.Add(di, e.K(1), "dn_inc"), idxSlot);
            e.Br(dnLoop);
            e.At(dnDone);
            // Odd digit count: zero the top half of r's top limb, which the digit loop never wrote.
            var padBlk = e.Blk(fn, "dn_pad");
            var rnBlk = e.Blk(fn, "dn_rn");
            e.CBr(e.Cmp(LlvmIntPredicate.Ne, e.And(n, e.K(1), "n_odd"), e.K(0), "is_odd"), padBlk, rnBlk);
            e.At(padBlk);
            e.StDig(r, n, 2, e.K(0));
            e.Br(rnBlk);
            e.At(rnBlk);
            e.St(e.Lshr(e.Add(n, e.K(1), "n_1"), e.K(1), "rn_limbs"), rnSlot);
            e.Br(finish);
        }

        e.At(finish);
        var qneg = e.And(LlvmApi.BuildXor(e.B, e.Neg(a, "san"), e.Neg(b, "sbn"), "qx"), e.K(1), "qneg");
        LlvmApi.BuildCall2(e.B, normTy, e.Get("bi_normalize"), [q, qneg, na], "");
        LlvmApi.BuildCall2(e.B, normTy, e.Get("bi_normalize"), [r, e.Neg(a, "ran"), e.Ld(rnSlot, "rnf")], "");
        e.RetVoid();
    }

    // to_decimal(a, scratch, out): write { i64 len, ascii bytes } into out.
    private static void EmitBiToDecimal(Bi e)
    {
        var fn = e.Declare(BigIntToDecimal, e.Void, [e.Ptr, e.Ptr, e.Ptr]);
        LlvmValueHandle a = e.Param(fn, 0), scratch = e.Param(fn, 1), outP = e.Param(fn, 2);
        var entry = e.Blk(fn, "entry");
        var zero = e.Blk(fn, "zero");
        var nonZero = e.Blk(fn, "nonzero");
        var copyLoop = e.Blk(fn, "copy_loop");
        var copyBody = e.Blk(fn, "copy_body");
        var digitLoop = e.Blk(fn, "digit_loop");
        var emitNeg = e.Blk(fn, "emit_neg");
        var afterNeg = e.Blk(fn, "after_neg");

        e.At(entry);
        // byte buffer starts at out+8
        var bufPtr = LlvmApi.BuildGEP2(e.B, e.I8, outP, [e.K(8)], "buf");
        var na = e.Count(a, "na");
        e.CBr(e.Cmp(LlvmIntPredicate.Eq, na, e.K(0), "is0"), zero, nonZero);

        e.At(zero);
        LlvmApi.BuildStore(e.B, e.Trunc8(e.K('0'), "z0"), bufPtr);
        e.StW(outP, e.K(0), e.K(1));
        e.RetVoid();

        e.At(nonZero);
        // copy a[1..na] into scratch[1..na]
        var iSlot = e.Slot("i");
        var snSlot = e.Slot("sn");
        var lenSlot = e.Slot("len");
        e.St(e.K(1), iSlot);
        e.Br(copyLoop);
        e.At(copyLoop);
        e.CBr(e.Cmp(LlvmIntPredicate.Sle, e.Ld(iSlot, "i"), na, "i_le"), copyBody, digitLoop);
        e.At(copyBody);
        var ci = e.Ld(iSlot, "ci");
        e.StW(scratch, ci, e.LdW(a, ci, "cw"));
        e.St(e.Add(ci, e.K(1), "inc"), iSlot);
        // set sn=na, len=0 lazily (harmless to reassign each iter; do once after loop instead)
        e.Br(copyLoop);

        e.At(digitLoop);
        // one-time init: sn=na, len=0 — done here on first entry. Since digitLoop is also the loop
        // header, we must init before it; use a preheader.
        // (init handled by the dedicated preheader below)
        EmitBiToDecimalLoop(e, fn, scratch, snSlot, lenSlot, na, bufPtr, emitNeg);

        e.At(emitNeg);
        // if a negative, append '-'
        var neg = e.Neg(a, "neg");
        var doNeg = e.Blk(fn, "do_neg");
        e.CBr(e.Cmp(LlvmIntPredicate.Ne, neg, e.K(0), "is_neg"), doNeg, afterNeg);
        e.At(doNeg);
        var lenN = e.Ld(lenSlot, "lenN");
        LlvmApi.BuildStore(e.B, e.Trunc8(e.K('-'), "minus"), LlvmApi.BuildGEP2(e.B, e.I8, bufPtr, [lenN], "negp"));
        e.St(e.Add(lenN, e.K(1), "lenN1"), lenSlot);
        e.Br(afterNeg);

        e.At(afterNeg);
        EmitBiReverse(e, fn, bufPtr, e.Ld(lenSlot, "finalLen"));
        e.StW(outP, e.K(0), e.Ld(lenSlot, "outLen"));
        e.RetVoid();
    }

    // The digit-producing loop: repeatedly divide scratch by 10 (32-bit halves), append each digit.
    private static void EmitBiToDecimalLoop(Bi e, LlvmValueHandle fn, LlvmValueHandle scratch, LlvmValueHandle snSlot, LlvmValueHandle lenSlot, LlvmValueHandle na, LlvmValueHandle bufPtr, LlvmBasicBlockHandle exit)
    {
        var pre = e.Blk(fn, "dl_pre");
        var loop = e.Blk(fn, "dl_loop");
        var body = e.Blk(fn, "dl_body");
        var limbLoop = e.Blk(fn, "dl_limb");
        var limbBody = e.Blk(fn, "dl_limb_body");
        var emit = e.Blk(fn, "dl_emit");
        e.Br(pre);
        e.At(pre);
        e.St(na, snSlot);
        e.St(e.K(0), lenSlot);
        var remSlot = e.Slot("rem");
        var liSlot = e.Slot("li");
        e.Br(loop);
        e.At(loop);
        e.CBr(e.Cmp(LlvmIntPredicate.Sgt, e.Ld(snSlot, "sn"), e.K(0), "sn_pos"), body, exit);
        e.At(body);
        // divmod scratch by 10: rem=0; for i=sn downto 1 process 32-bit halves
        e.St(e.K(0), remSlot);
        e.St(e.Ld(snSlot, "sn0"), liSlot);
        e.Br(limbLoop);
        e.At(limbLoop);
        e.CBr(e.Cmp(LlvmIntPredicate.Sge, e.Ld(liSlot, "li"), e.K(1), "li_ge1"), limbBody, emit);
        e.At(limbBody);
        var li = e.Ld(liSlot, "liv");
        var limb = e.LdW(scratch, li, "limb");
        var hi = e.Lshr(limb, e.K(32), "hi");
        var lo = e.And(limb, e.K(0xFFFFFFFF), "lo");
        // high half
        var rem0 = e.Ld(remSlot, "rem0");
        var curH = e.Or(e.Shl(rem0, e.K(32), "rhs"), hi, "curH");
        var qH = e.UDiv(curH, e.K(10), "qH");
        var remH = e.Sub(curH, e.Mul(qH, e.K(10), "qh10"), "remH");
        // low half
        var curL = e.Or(e.Shl(remH, e.K(32), "rls"), lo, "curL");
        var qL = e.UDiv(curL, e.K(10), "qL");
        var remL = e.Sub(curL, e.Mul(qL, e.K(10), "ql10"), "remL");
        e.StW(scratch, li, e.Or(e.Shl(qH, e.K(32), "qhs"), qL, "qcombine"));
        e.St(remL, remSlot);
        e.St(e.Sub(li, e.K(1), "lidec"), liSlot);
        e.Br(limbLoop);
        e.At(emit);
        // strip leading zero limbs of scratch
        EmitBiStripZeros(e, fn, scratch, snSlot, "dlstrip");
        // append digit '0'+rem
        var len = e.Ld(lenSlot, "len");
        var digit = e.Trunc8(e.Add(e.Ld(remSlot, "remf"), e.K('0'), "digit"), "digit8");
        LlvmApi.BuildStore(e.B, digit, LlvmApi.BuildGEP2(e.B, e.I8, bufPtr, [len], "digp"));
        e.St(e.Add(len, e.K(1), "leninc"), lenSlot);
        e.Br(loop);
    }

    // reverse buf[0..len-1] in place.
    private static void EmitBiReverse(Bi e, LlvmValueHandle fn, LlvmValueHandle bufPtr, LlvmValueHandle len)
    {
        var pre = e.Blk(fn, "rev_pre");
        var loop = e.Blk(fn, "rev_loop");
        var body = e.Blk(fn, "rev_body");
        var doneBB = e.Blk(fn, "rev_done");
        e.Br(pre);
        e.At(pre);
        var iSlot = e.Slot("ri");
        var jSlot = e.Slot("rj");
        e.St(e.K(0), iSlot);
        e.St(e.Sub(len, e.K(1), "len_1"), jSlot);
        e.Br(loop);
        e.At(loop);
        e.CBr(e.Cmp(LlvmIntPredicate.Slt, e.Ld(iSlot, "i"), e.Ld(jSlot, "j"), "i_lt_j"), body, doneBB);
        e.At(body);
        var i = e.Ld(iSlot, "iv");
        var j = e.Ld(jSlot, "jv");
        var pi = LlvmApi.BuildGEP2(e.B, e.I8, bufPtr, [i], "pi");
        var pj = LlvmApi.BuildGEP2(e.B, e.I8, bufPtr, [j], "pj");
        var ci = LlvmApi.BuildLoad2(e.B, e.I8, pi, "ci");
        var cj = LlvmApi.BuildLoad2(e.B, e.I8, pj, "cj");
        LlvmApi.BuildStore(e.B, cj, pi);
        LlvmApi.BuildStore(e.B, ci, pj);
        e.St(e.Add(i, e.K(1), "iinc"), iSlot);
        e.St(e.Sub(j, e.K(1), "jdec"), jSlot);
        e.Br(loop);
        e.At(doneBB);
    }
}
