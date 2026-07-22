using Ashes.Backend.Llvm.Interop;

namespace Ashes.Backend.Llvm;

internal static partial class LlvmCodegen
{
    // Ashes.Number.BigInt
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

    // Call sites (in the current user function): size buffers, then call a helper

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

    private static LlvmValueHandle EmitBigIntFromInt(
        LlvmCodegenState state,
        LlvmValueHandle value,
        bool runtimeManaged = false)
    {
        LlvmTypeHandle voidType = LlvmApi.VoidTypeInContext(state.Target.Context);
        LlvmTypeHandle fnType = LlvmApi.FunctionType(voidType, [state.I64, state.I8Ptr]);
        LlvmValueHandle fn = LlvmApi.GetNamedFunction(state.Target.Module, BigIntFromI64);
        LlvmValueHandle outAddress = runtimeManaged
            ? EmitRuntimeRcAlloc(state, 16, "rc_bigint_from_int")
            : EmitAlloc(state, 16); // header + one limb
        LlvmApi.BuildCall2(state.Target.Builder, fnType, fn, [value, BigIntAsPtr(state, outAddress, "bigint_from_out")], "");
        return outAddress;
    }

    private static LlvmValueHandle EmitBigIntArith(
        LlvmCodegenState state,
        LlvmValueHandle left,
        LlvmValueHandle right,
        string fnName,
        bool runtimeManaged)
    {
        LlvmValueHandle la = BigIntLimbCount(state, left, "bigint_l");
        LlvmValueHandle lb = BigIntLimbCount(state, right, "bigint_r");
        // add/sub/mul all fit in la + lb + 3 words (header + magnitude + slack).
        LlvmValueHandle words = BigIntAddConst(state, LlvmApi.BuildAdd(state.Target.Builder, la, lb, "bigint_la_lb"), 3);
        LlvmValueHandle resultBytes = BigIntBytesForWords(state, words);
        LlvmValueHandle outAddress = runtimeManaged
            ? EmitRuntimeRcAllocDynamic(state, resultBytes, "rc_bigint_arith")
            : EmitAllocDynamic(state, resultBytes);

        LlvmTypeHandle voidType = LlvmApi.VoidTypeInContext(state.Target.Context);
        LlvmTypeHandle fnType = LlvmApi.FunctionType(voidType, [state.I8Ptr, state.I8Ptr, state.I8Ptr]);
        LlvmValueHandle fn = LlvmApi.GetNamedFunction(state.Target.Module, fnName);
        LlvmApi.BuildCall2(state.Target.Builder, fnType, fn,
            [BigIntAsPtr(state, left, "bigint_a"), BigIntAsPtr(state, right, "bigint_b"), BigIntAsPtr(state, outAddress, "bigint_out")], "");
        return outAddress;
    }

    private static LlvmValueHandle EmitBigIntDivMod(
        LlvmCodegenState state,
        LlvmValueHandle left,
        LlvmValueHandle right,
        bool returnQuotient,
        bool runtimeManaged)
    {
        LlvmValueHandle la = BigIntLimbCount(state, left, "bigint_dl");
        LlvmValueHandle lb = BigIntLimbCount(state, right, "bigint_dr");
        LlvmValueHandle qBytes = BigIntBytesForWords(state, BigIntAddConst(state, la, 2));
        LlvmValueHandle rBytes = BigIntBytesForWords(state, BigIntAddConst(state, lb, 2));
        LlvmValueHandle qAddress = runtimeManaged
            ? EmitRuntimeRcAllocDynamic(state, qBytes, "rc_bigint_quotient")
            : EmitAllocDynamic(state, qBytes);
        LlvmValueHandle rAddress = runtimeManaged
            ? EmitRuntimeRcAllocDynamic(state, rBytes, "rc_bigint_remainder")
            : EmitAllocDynamic(state, rBytes);
        // Algorithm D scratch: normalized divisor (2*lb digits) + working dividend (2*la + 1 digits),
        // as 32-bit digits — la + lb + 4 words covers both with slack.
        LlvmValueHandle laLb = LlvmApi.BuildAdd(state.Target.Builder, la, lb, "bigint_dsw");
        LlvmValueHandle scratchBytes = BigIntBytesForWords(state, BigIntAddConst(state, laLb, 4));
        LlvmValueHandle scratchAddress = runtimeManaged
            ? EmitRuntimeRcAllocDynamic(state, scratchBytes, "rc_bigint_div_scratch")
            : EmitAllocDynamic(state, scratchBytes);

        LlvmTypeHandle voidType = LlvmApi.VoidTypeInContext(state.Target.Context);
        LlvmTypeHandle fnType = LlvmApi.FunctionType(voidType, [state.I8Ptr, state.I8Ptr, state.I8Ptr, state.I8Ptr, state.I8Ptr]);
        LlvmValueHandle fn = LlvmApi.GetNamedFunction(state.Target.Module, BigIntDivModFn);
        LlvmApi.BuildCall2(state.Target.Builder, fnType, fn,
            [BigIntAsPtr(state, left, "bigint_da"), BigIntAsPtr(state, right, "bigint_db"),
             BigIntAsPtr(state, qAddress, "bigint_q"), BigIntAsPtr(state, rAddress, "bigint_r"),
             BigIntAsPtr(state, scratchAddress, "bigint_dscratch")], "");
        if (runtimeManaged)
        {
            EmitRuntimeRcDrop(state, returnQuotient ? rAddress : qAddress);
            EmitRuntimeRcDrop(state, scratchAddress);
        }
        return returnQuotient ? qAddress : rAddress;
    }

    private static LlvmValueHandle EmitBigIntBinary(
        LlvmCodegenState state,
        LlvmValueHandle left,
        LlvmValueHandle right,
        string op,
        bool runtimeManaged = false) => op switch
        {
            "add" => EmitBigIntArith(state, left, right, BigIntAddFn, runtimeManaged),
            "sub" => EmitBigIntArith(state, left, right, BigIntSubFn, runtimeManaged),
            "mul" => EmitBigIntArith(state, left, right, BigIntMulFn, runtimeManaged),
            "div" => EmitBigIntDivMod(state, left, right, returnQuotient: true, runtimeManaged),
            "mod" => EmitBigIntDivMod(state, left, right, returnQuotient: false, runtimeManaged),
            _ => throw new InvalidOperationException($"Unknown BigInt binary op '{op}'.")
        };

    private static LlvmValueHandle EmitBigIntCompare(LlvmCodegenState state, LlvmValueHandle left, LlvmValueHandle right)
    {
        LlvmTypeHandle fnType = LlvmApi.FunctionType(state.I64, [state.I8Ptr, state.I8Ptr]);
        LlvmValueHandle fn = LlvmApi.GetNamedFunction(state.Target.Module, BigIntCmp);
        return LlvmApi.BuildCall2(state.Target.Builder, fnType, fn,
            [BigIntAsPtr(state, left, "bigint_ca"), BigIntAsPtr(state, right, "bigint_cb")], "bigint_cmp");
    }

    private static LlvmValueHandle EmitBigIntToString(
        LlvmCodegenState state,
        LlvmValueHandle value,
        bool runtimeManaged = false)
    {
        LlvmValueHandle la = BigIntLimbCount(state, value, "bigint_ts");
        LlvmValueHandle scratchBytes = BigIntBytesForWords(state, BigIntAddConst(state, la, 2));
        LlvmValueHandle scratchAddress = runtimeManaged
            ? EmitRuntimeRcAllocDynamic(state, scratchBytes, "rc_bigint_text_scratch")
            : EmitAllocDynamic(state, scratchBytes);
        // Decimal string: <= la*20 digits + sign; string word count la*3 + 4 covers len word + bytes.
        LlvmValueHandle outWords = BigIntAddConst(state, LlvmApi.BuildMul(state.Target.Builder, la, LlvmApi.ConstInt(state.I64, 3, 0), "bigint_ts_w"), 4);
        LlvmValueHandle outBytes = BigIntBytesForWords(state, outWords);
        LlvmValueHandle outAddress = runtimeManaged
            ? EmitRuntimeRcAllocDynamic(state, outBytes, "rc_bigint_text")
            : EmitAllocDynamic(state, outBytes);

        LlvmTypeHandle voidType = LlvmApi.VoidTypeInContext(state.Target.Context);
        LlvmTypeHandle fnType = LlvmApi.FunctionType(voidType, [state.I8Ptr, state.I8Ptr, state.I8Ptr]);
        LlvmValueHandle fn = LlvmApi.GetNamedFunction(state.Target.Module, BigIntToDecimal);
        LlvmApi.BuildCall2(state.Target.Builder, fnType, fn,
            [BigIntAsPtr(state, value, "bigint_tsa"), BigIntAsPtr(state, scratchAddress, "bigint_scratch"), BigIntAsPtr(state, outAddress, "bigint_tsout")], "");
        if (runtimeManaged)
        {
            EmitRuntimeRcDrop(state, scratchAddress);
        }
        return outAddress;
    }

    // BigInt -> Result(Str, Int): Ok(value) when it fits an i64, else Err.
    private static LlvmValueHandle EmitBigIntToInt(LlvmCodegenState state, LlvmValueHandle bigAddr, bool runtimeManaged)
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
        LlvmApi.BuildStore(builder, EmitResultOk(state, LlvmApi.BuildLoad2(builder, state.I64, valueSlot, "bi_toint_v"), runtimeManaged), resultSlot);
        LlvmApi.BuildBr(builder, doneBlk);

        LlvmApi.PositionBuilderAtEnd(builder, errBlk);
        LlvmApi.BuildStore(builder, EmitResultError(state, EmitHeapStringLiteral(state, "BigInt does not fit in Int"), runtimeManaged), resultSlot);
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

    // Runtime helper emission (once per program that uses BigInt)
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

    // Split-helper support records (block/slot bundles keep phase-helper signatures small)

    private readonly record struct EmitBiFromDecimalBlocks(
        LlvmBasicBlockHandle Entry,
        LlvmBasicBlockHandle NotEmpty,
        LlvmBasicBlockHandle Minus,
        LlvmBasicBlockHandle StartLoop,
        LlvmBasicBlockHandle Loop,
        LlvmBasicBlockHandle Body,
        LlvmBasicBlockHandle DigitOk,
        LlvmBasicBlockHandle MulLoop,
        LlvmBasicBlockHandle MulBody,
        LlvmBasicBlockHandle AppendCarry,
        LlvmBasicBlockHandle Grow,
        LlvmBasicBlockHandle Advance,
        LlvmBasicBlockHandle Finish,
        LlvmBasicBlockHandle Invalid);

    private readonly record struct EmitBiFromDecimalSlots(
        LlvmValueHandle I,
        LlvmValueHandle N,
        LlvmValueHandle Neg,
        LlvmValueHandle Carry,
        LlvmValueHandle J);

    private readonly record struct EmitBiAddSubBlocks(
        LlvmBasicBlockHandle Entry,
        LlvmBasicBlockHandle AZero,
        LlvmBasicBlockHandle ChkBZero,
        LlvmBasicBlockHandle BZero,
        LlvmBasicBlockHandle Main,
        LlvmBasicBlockHandle SameSign,
        LlvmBasicBlockHandle DiffSign,
        LlvmBasicBlockHandle ABigger,
        LlvmBasicBlockHandle SubAB,
        LlvmBasicBlockHandle SubBA,
        LlvmBasicBlockHandle EqZero,
        LlvmBasicBlockHandle End);

    private readonly record struct EmitBiMulBlocks(
        LlvmBasicBlockHandle Entry,
        LlvmBasicBlockHandle Zero,
        LlvmBasicBlockHandle InitLoop,
        LlvmBasicBlockHandle InitBody,
        LlvmBasicBlockHandle OuterLoop,
        LlvmBasicBlockHandle OuterBody,
        LlvmBasicBlockHandle InnerLoop,
        LlvmBasicBlockHandle InnerBody,
        LlvmBasicBlockHandle InnerDone,
        LlvmBasicBlockHandle Done,
        LlvmBasicBlockHandle End);

    private readonly record struct EmitBiDivModLongSlots(
        LlvmValueHandle J,
        LlvmValueHandle Qhat,
        LlvmValueHandle Rhat,
        LlvmValueHandle K,
        LlvmValueHandle I);

    private readonly record struct EmitBiDivModLongBlocks(
        LlvmBasicBlockHandle JLoop,
        LlvmBasicBlockHandle JBody,
        LlvmBasicBlockHandle CorrCheck,
        LlvmBasicBlockHandle CorrMul,
        LlvmBasicBlockHandle CorrDec,
        LlvmBasicBlockHandle MulSub,
        LlvmBasicBlockHandle MsLoop,
        LlvmBasicBlockHandle MsBody,
        LlvmBasicBlockHandle MsDone,
        LlvmBasicBlockHandle AddBack,
        LlvmBasicBlockHandle AbLoop,
        LlvmBasicBlockHandle AbBody,
        LlvmBasicBlockHandle AbDone,
        LlvmBasicBlockHandle JNext,
        LlvmBasicBlockHandle Denorm);

    private static EmitBiFromDecimalBlocks CreateBiFromDecimalBlocks(Bi e, LlvmValueHandle fn)
        => new(
            e.Blk(fn, "entry"),
            e.Blk(fn, "not_empty"),
            e.Blk(fn, "minus"),
            e.Blk(fn, "start_loop"),
            e.Blk(fn, "loop"),
            e.Blk(fn, "body"),
            e.Blk(fn, "digit_ok"),
            e.Blk(fn, "mul_loop"),
            e.Blk(fn, "mul_body"),
            e.Blk(fn, "append_carry"),
            e.Blk(fn, "grow"),
            e.Blk(fn, "advance"),
            e.Blk(fn, "finish"),
            e.Blk(fn, "invalid"));

    private static EmitBiAddSubBlocks CreateBiAddSubBlocks(Bi e, LlvmValueHandle fn)
        => new(
            e.Blk(fn, "entry"),
            e.Blk(fn, "a_zero"),
            e.Blk(fn, "chk_bzero"),
            e.Blk(fn, "b_zero"),
            e.Blk(fn, "main"),
            e.Blk(fn, "same_sign"),
            e.Blk(fn, "diff_sign"),
            e.Blk(fn, "a_bigger"),
            e.Blk(fn, "sub_ab"),
            e.Blk(fn, "sub_ba"),
            e.Blk(fn, "eq_zero"),
            e.Blk(fn, "end"));

    private static EmitBiMulBlocks CreateBiMulBlocks(Bi e, LlvmValueHandle fn)
        => new(
            e.Blk(fn, "entry"),
            e.Blk(fn, "zero"),
            e.Blk(fn, "init_loop"),
            e.Blk(fn, "init_body"),
            e.Blk(fn, "outer"),
            e.Blk(fn, "outer_body"),
            e.Blk(fn, "inner"),
            e.Blk(fn, "inner_body"),
            e.Blk(fn, "inner_done"),
            e.Blk(fn, "done"),
            e.Blk(fn, "end"));

    // bignum_from_decimal(str, len, out) -> i64 (1 = success, 0 = invalid). Horner: out = out*10 + digit
    // per decimal byte, in place; leading '-' sets the sign. out sized >= len + 2 words by the caller.
    private static void EmitBiFromDecimal(Bi e)
    {
        var fn = e.Declare("bignum_from_decimal", e.I64, [e.Ptr, e.I64, e.Ptr]);
        LlvmValueHandle str = e.Param(fn, 0), len = e.Param(fn, 1), outP = e.Param(fn, 2);
        var b = CreateBiFromDecimalBlocks(e, fn);

        e.At(b.Entry);
        var s = new EmitBiFromDecimalSlots(e.Slot("i"), e.Slot("n"), e.Slot("neg"), e.Slot("carry"), e.Slot("j"));
        e.St(e.K(0), s.I);
        e.St(e.K(0), s.N);
        e.St(e.K(0), s.Neg);
        e.CBr(e.Cmp(LlvmIntPredicate.Eq, len, e.K(0), "empty"), b.Invalid, b.NotEmpty);

        EmitBiFromDecimalScan(e, str, len, b, s);
        EmitBiFromDecimalMul(e, outP, b, s);
        EmitBiFromDecimalFinish(e, outP, b, s);
    }

    // notEmpty → digitOk: skip an optional leading '-', then validate each byte is a decimal digit.
    private static void EmitBiFromDecimalScan(Bi e, LlvmValueHandle str, LlvmValueHandle len, EmitBiFromDecimalBlocks b, EmitBiFromDecimalSlots s)
    {
        e.At(b.NotEmpty);
        var b0 = LlvmApi.BuildLoad2(e.B, e.I8, LlvmApi.BuildGEP2(e.B, e.I8, str, [e.K(0)], "b0p"), "b0");
        var b0w = LlvmApi.BuildZExt(e.B, b0, e.I64, "b0w");
        e.CBr(e.Cmp(LlvmIntPredicate.Eq, b0w, e.K('-'), "is_minus"), b.Minus, b.StartLoop);

        e.At(b.Minus);
        e.St(e.K(1), s.Neg);
        e.St(e.K(1), s.I);
        e.CBr(e.Cmp(LlvmIntPredicate.Eq, len, e.K(1), "only_minus"), b.Invalid, b.StartLoop);

        e.At(b.StartLoop);
        e.Br(b.Loop);
        e.At(b.Loop);
        e.CBr(e.Cmp(LlvmIntPredicate.Slt, e.Ld(s.I, "i"), len, "i_lt_len"), b.Body, b.Finish);

        e.At(b.Body);
        var iv = e.Ld(s.I, "iv");
        var bch = LlvmApi.BuildZExt(e.B, LlvmApi.BuildLoad2(e.B, e.I8, LlvmApi.BuildGEP2(e.B, e.I8, str, [iv], "bp"), "b"), e.I64, "bch");
        var tooLow = e.Cmp(LlvmIntPredicate.Slt, bch, e.K('0'), "too_low");
        var tooHigh = e.Cmp(LlvmIntPredicate.Sgt, bch, e.K('9'), "too_high");
        e.CBr(e.Or(tooLow, tooHigh, "bad"), b.Invalid, b.DigitOk);

        e.At(b.DigitOk);
        var digit = e.Sub(bch, e.K('0'), "digit");
        // out = out*10 + digit (in place); carry starts at digit
        e.St(digit, s.Carry);
        e.St(e.K(1), s.J);
        e.Br(b.MulLoop);
    }

    // mulLoop → advance: out = out*10 + carry (in place), grow by one limb on final carry, advance i.
    private static void EmitBiFromDecimalMul(Bi e, LlvmValueHandle outP, EmitBiFromDecimalBlocks b, EmitBiFromDecimalSlots s)
    {
        e.At(b.MulLoop);
        e.CBr(e.Cmp(LlvmIntPredicate.Sle, e.Ld(s.J, "j"), e.Ld(s.N, "n"), "j_le_n"), b.MulBody, b.AppendCarry);
        e.At(b.MulBody);
        var jv = e.Ld(s.J, "jv");
        var limb = e.LdW(outP, jv, "limb");
        var p = LlvmApi.BuildAdd(e.B, e.Mul(e.ZExt128(limb, "l128"), e.K128(10), "l10"), e.ZExt128(e.Ld(s.Carry, "c"), "c128"), "p");
        e.StW(outP, jv, e.Trunc64(p, "plow"));
        e.St(e.Trunc64(LlvmApi.BuildLShr(e.B, p, e.K128(64), "phi"), "chi"), s.Carry);
        e.St(e.Add(jv, e.K(1), "jinc"), s.J);
        e.Br(b.MulLoop);
        e.At(b.AppendCarry);
        e.CBr(e.Cmp(LlvmIntPredicate.Ne, e.Ld(s.Carry, "cf"), e.K(0), "has_carry"), b.Grow, b.Advance);
        e.At(b.Grow);
        var nn = e.Add(e.Ld(s.N, "n2"), e.K(1), "n_inc");
        e.St(nn, s.N);
        e.StW(outP, nn, e.Ld(s.Carry, "cc"));
        e.Br(b.Advance);
        e.At(b.Advance);
        e.St(e.Add(e.Ld(s.I, "id"), e.K(1), "i_inc"), s.I);
        e.Br(b.Loop);
    }

    private static void EmitBiFromDecimalFinish(Bi e, LlvmValueHandle outP, EmitBiFromDecimalBlocks b, EmitBiFromDecimalSlots s)
    {
        e.At(b.Finish);
        var normTy = LlvmApi.FunctionType(e.Void, [e.Ptr, e.I64, e.I64]);
        LlvmApi.BuildCall2(e.B, normTy, e.Get("bi_normalize"), [outP, e.Ld(s.Neg, "neg"), e.Ld(s.N, "nf")], "");
        e.Ret(e.K(1));

        e.At(b.Invalid);
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
        var blk = CreateBiAddSubBlocks(e, fn);

        var magTy = LlvmApi.FunctionType(e.I64, [e.Ptr, e.I64, e.Ptr, e.I64, e.Ptr]);
        var cmpTy = LlvmApi.FunctionType(e.I64, [e.Ptr, e.I64, e.Ptr, e.I64]);
        var normTy = LlvmApi.FunctionType(e.Void, [e.Ptr, e.I64, e.I64]);

        e.At(blk.Entry);
        var na = e.Count(a, "na");
        var nb = e.Count(b, "nb");
        var sa = e.Neg(a, "sa");
        var sbRaw = e.Neg(b, "sb0");
        var sb = isSub ? e.And(e.Add(sbRaw, e.K(1), "flip"), e.K(1), "sb") : sbRaw;
        e.CBr(e.Cmp(LlvmIntPredicate.Eq, na, e.K(0), "a_is0"), blk.AZero, blk.ChkBZero);

        e.At(blk.AZero);
        EmitBiCopyMag(e, fn, b, nb, sb, outP, "cpb"); // out = ±b
        e.Br(blk.End);

        e.At(blk.ChkBZero);
        e.CBr(e.Cmp(LlvmIntPredicate.Eq, nb, e.K(0), "b_is0"), blk.BZero, blk.Main);
        e.At(blk.BZero);
        EmitBiCopyMag(e, fn, a, na, sa, outP, "cpa"); // out = a
        e.Br(blk.End);

        e.At(blk.Main);
        e.CBr(e.Cmp(LlvmIntPredicate.Eq, sa, sb, "same"), blk.SameSign, blk.DiffSign);

        e.At(blk.SameSign);
        var addN = LlvmApi.BuildCall2(e.B, magTy, e.Get("bi_add_mag"), [a, na, b, nb, outP], "add_n");
        LlvmApi.BuildCall2(e.B, normTy, e.Get("bi_normalize"), [outP, sa, addN], "");
        e.Br(blk.End);

        EmitBiAddSubDiff(e, a, b, na, nb, sa, sb, outP, magTy, cmpTy, normTy, blk);

        e.At(blk.End);
        e.RetVoid();
    }

    // diffSign → eqZero: opposite signs, so subtract the smaller magnitude from the larger.
    private static void EmitBiAddSubDiff(Bi e, LlvmValueHandle a, LlvmValueHandle b, LlvmValueHandle na, LlvmValueHandle nb, LlvmValueHandle sa, LlvmValueHandle sb, LlvmValueHandle outP, LlvmTypeHandle magTy, LlvmTypeHandle cmpTy, LlvmTypeHandle normTy, EmitBiAddSubBlocks blk)
    {
        e.At(blk.DiffSign);
        var c = LlvmApi.BuildCall2(e.B, cmpTy, e.Get("bi_cmp_mag"), [a, na, b, nb], "c");
        e.CBr(e.Cmp(LlvmIntPredicate.Eq, c, e.K(0), "mag_eq"), blk.EqZero, blk.ABigger);
        e.At(blk.ABigger);
        e.CBr(e.Cmp(LlvmIntPredicate.Sgt, c, e.K(0), "a_big"), blk.SubAB, blk.SubBA);

        e.At(blk.SubAB);
        var s1 = LlvmApi.BuildCall2(e.B, magTy, e.Get("bi_sub_mag"), [a, na, b, nb, outP], "sab");
        LlvmApi.BuildCall2(e.B, normTy, e.Get("bi_normalize"), [outP, sa, s1], "");
        e.Br(blk.End);

        e.At(blk.SubBA);
        var s2 = LlvmApi.BuildCall2(e.B, magTy, e.Get("bi_sub_mag"), [b, nb, a, na, outP], "sba");
        LlvmApi.BuildCall2(e.B, normTy, e.Get("bi_normalize"), [outP, sb, s2], "");
        e.Br(blk.End);

        e.At(blk.EqZero);
        e.StW(outP, e.K(0), e.K(0));
        e.Br(blk.End);
    }

    // mul: out = a*b (schoolbook, i128 partial products).
    private static void EmitBiMul(Bi e)
    {
        var fn = e.Declare(BigIntMulFn, e.Void, [e.Ptr, e.Ptr, e.Ptr]);
        LlvmValueHandle a = e.Param(fn, 0), b = e.Param(fn, 1), outP = e.Param(fn, 2);
        var blk = CreateBiMulBlocks(e, fn);

        var normTy = LlvmApi.FunctionType(e.Void, [e.Ptr, e.I64, e.I64]);

        e.At(blk.Entry);
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
        e.CBr(e.Cmp(LlvmIntPredicate.Ne, anyZero, e.K(0), "is0"), blk.Zero, blk.InitLoop);

        e.At(blk.Zero);
        e.StW(outP, e.K(0), e.K(0));
        e.Br(blk.End);

        // init out[1..total] = 0
        e.At(blk.InitLoop);
        e.CBr(e.Cmp(LlvmIntPredicate.Sle, e.Ld(kSlot, "k"), total, "k_le"), blk.InitBody, blk.OuterLoop);
        e.At(blk.InitBody);
        var kv = e.Ld(kSlot, "kv");
        e.StW(outP, kv, e.K(0));
        e.St(e.Add(kv, e.K(1), "kinc"), kSlot);
        e.Br(blk.InitLoop);

        EmitBiMulLoops(e, fn, a, b, na, nb, outP, iSlot, jSlot, carrySlot, blk);

        e.At(blk.Done);
        var neg = e.And(LlvmApi.BuildXor(e.B, e.Neg(a, "san"), e.Neg(b, "sbn"), "sx"), e.K(1), "neg");
        LlvmApi.BuildCall2(e.B, normTy, e.Get("bi_normalize"), [outP, neg, total], "");
        e.Br(blk.End);

        e.At(blk.End);
        e.RetVoid();
    }

    // outer i = 1..na, inner j = 1..nb schoolbook multiply accumulating i128 partial products.
    private static void EmitBiMulLoops(Bi e, LlvmValueHandle fn, LlvmValueHandle a, LlvmValueHandle b, LlvmValueHandle na, LlvmValueHandle nb, LlvmValueHandle outP, LlvmValueHandle iSlot, LlvmValueHandle jSlot, LlvmValueHandle carrySlot, EmitBiMulBlocks blk)
    {
        e.At(blk.OuterLoop);
        e.St(e.K(1), iSlot);
        e.Br(blk.OuterBody);
        e.At(blk.OuterBody);
        e.CBr(e.Cmp(LlvmIntPredicate.Sle, e.Ld(iSlot, "i"), na, "i_le"), blk.InnerLoop, blk.Done);

        // inner j = 1..nb
        e.At(blk.InnerLoop);
        e.St(e.K(1), jSlot);
        e.St(e.K(0), carrySlot);
        e.Br(blk.InnerBody);
        e.At(blk.InnerBody);
        var jv = e.Ld(jSlot, "j");
        var proceed = e.Cmp(LlvmIntPredicate.Sle, jv, nb, "j_le");
        var innerStep = e.Blk(fn, "inner_step");
        e.CBr(proceed, innerStep, blk.InnerDone);
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
        e.Br(blk.InnerBody);

        e.At(blk.InnerDone);
        var ivd = e.Ld(iSlot, "ivd");
        e.StW(outP, e.Add(ivd, nb, "i_nb"), e.Ld(carrySlot, "cend"));
        e.St(e.Add(ivd, e.K(1), "iinc"), iSlot);
        e.Br(blk.OuterBody);
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
        var normTy = LlvmApi.FunctionType(e.Void, [e.Ptr, e.I64, e.I64]);

        var (na, nb) = EmitBiDivModEntry(e, fn, a, b, q, r, entry, trivial, copyR, zeroR, proceed);
        EmitBiDivModProceed(e, fn, a, b, q, r, scratch, na, nb, proceed, normTy);
    }

    // entry + trivial cases: q=0 when nb==0, na==0 or |a|<|b|; then r = a (or 0). Returns |a|,|b| limb counts.
    private static (LlvmValueHandle Na, LlvmValueHandle Nb) EmitBiDivModEntry(Bi e, LlvmValueHandle fn, LlvmValueHandle a, LlvmValueHandle b, LlvmValueHandle q, LlvmValueHandle r, LlvmBasicBlockHandle entry, LlvmBasicBlockHandle trivial, LlvmBasicBlockHandle copyR, LlvmBasicBlockHandle zeroR, LlvmBasicBlockHandle proceed)
    {
        var cmpTy = LlvmApi.FunctionType(e.I64, [e.Ptr, e.I64, e.Ptr, e.I64]);
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
        return (na, nb);
    }

    // proceed: zero q's limbs, count digits, dispatch to short/long division, then normalize.
    private static void EmitBiDivModProceed(Bi e, LlvmValueHandle fn, LlvmValueHandle a, LlvmValueHandle b, LlvmValueHandle q, LlvmValueHandle r, LlvmValueHandle scratch, LlvmValueHandle na, LlvmValueHandle nb, LlvmBasicBlockHandle proceed, LlvmTypeHandle normTy)
    {
        e.At(proceed);
        var qInitLoop = e.Blk(fn, "qinit_loop");
        var qInitBody = e.Blk(fn, "qinit_body");
        var countDigits = e.Blk(fn, "count_digits");
        var shortDiv = e.Blk(fn, "short_div");
        var longDiv = e.Blk(fn, "long_div");
        var finish = e.Blk(fn, "finish");
        var idxSlot = e.Slot("idx");
        var rnSlot = e.Slot("rn");

        var (m, n) = EmitBiDivModCountDigits(e, a, b, q, na, nb, idxSlot, qInitLoop, qInitBody, countDigits, shortDiv, longDiv);
        EmitBiDivModShort(e, fn, a, b, q, r, m, idxSlot, rnSlot, shortDiv, finish);
        EmitBiDivModLong(e, fn, a, b, q, r, scratch, m, n, idxSlot, rnSlot, longDiv, finish);
        EmitBiDivModFinish(e, a, b, q, r, na, rnSlot, normTy, finish);
    }

    // q limbs = 0 for [1..na] (quotient digits are written as 32-bit stores below, so the whole
    // limb area must start zeroed), then compute m/n = digit counts. Returns (m, n).
    private static (LlvmValueHandle M, LlvmValueHandle N) EmitBiDivModCountDigits(Bi e, LlvmValueHandle a, LlvmValueHandle b, LlvmValueHandle q, LlvmValueHandle na, LlvmValueHandle nb, LlvmValueHandle idxSlot, LlvmBasicBlockHandle qInitLoop, LlvmBasicBlockHandle qInitBody, LlvmBasicBlockHandle countDigits, LlvmBasicBlockHandle shortDiv, LlvmBasicBlockHandle longDiv)
    {
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
        return (m, n);
    }

    // Short division: single-digit divisor, one native 64/32 divide per dividend digit
    private static void EmitBiDivModShort(Bi e, LlvmValueHandle fn, LlvmValueHandle a, LlvmValueHandle b, LlvmValueHandle q, LlvmValueHandle r, LlvmValueHandle m, LlvmValueHandle idxSlot, LlvmValueHandle rnSlot, LlvmBasicBlockHandle shortDiv, LlvmBasicBlockHandle finish)
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

    // Algorithm D proper (n >= 2)
    private static void EmitBiDivModLong(Bi e, LlvmValueHandle fn, LlvmValueHandle a, LlvmValueHandle b, LlvmValueHandle q, LlvmValueHandle r, LlvmValueHandle scratch, LlvmValueHandle m, LlvmValueHandle n, LlvmValueHandle idxSlot, LlvmValueHandle rnSlot, LlvmBasicBlockHandle longDiv, LlvmBasicBlockHandle finish)
    {
        var (s, rs) = EmitBiDivModLongNlz(e, fn, b, n, longDiv);
        EmitBiDivModLongNorm(e, fn, a, b, scratch, m, n, s, rs, idxSlot);
        var (slots, blocks, vtn, vtn2) = EmitBiDivModLongMain(e, fn, scratch, m, n);
        EmitBiDivModLongCorr(e, scratch, n, vtn, vtn2, slots, blocks);
        EmitBiDivModLongMulSub(e, q, scratch, n, slots, blocks);
        EmitBiDivModLongAddBack(e, q, scratch, n, slots, blocks);
        EmitBiDivModLongDenorm(e, fn, r, scratch, n, s, rs, idxSlot, rnSlot, blocks.Denorm, finish);
    }

    // nlz32(vn top digit): shift so the divisor's top digit has its high bit set. Returns (s, rs).
    private static (LlvmValueHandle S, LlvmValueHandle Rs) EmitBiDivModLongNlz(Bi e, LlvmValueHandle fn, LlvmValueHandle b, LlvmValueHandle n, LlvmBasicBlockHandle longDiv)
    {
        e.At(longDiv);
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
        return (s, rs);
    }

    // Build the normalized divisor vn at scratch[0..n-1] and dividend un at scratch[n..n+m].
    private static void EmitBiDivModLongNorm(Bi e, LlvmValueHandle fn, LlvmValueHandle a, LlvmValueHandle b, LlvmValueHandle scratch, LlvmValueHandle m, LlvmValueHandle n, LlvmValueHandle s, LlvmValueHandle rs, LlvmValueHandle idxSlot)
    {
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
    }

    // Main-loop setup + first quotient-digit estimate (qhat/rhat). Returns loop state and vn tops.
    private static (EmitBiDivModLongSlots Slots, EmitBiDivModLongBlocks Blocks, LlvmValueHandle Vtn, LlvmValueHandle Vtn2) EmitBiDivModLongMain(Bi e, LlvmValueHandle fn, LlvmValueHandle scratch, LlvmValueHandle m, LlvmValueHandle n)
    {
        // Main loop: one quotient digit per iteration, j = m-n down to 0.
        var slots = new EmitBiDivModLongSlots(e.Slot("d_j"), e.Slot("d_qhat"), e.Slot("d_rhat"), e.Slot("d_k"), e.Slot("d_i"));
        var blocks = new EmitBiDivModLongBlocks(
            e.Blk(fn, "j_loop"), e.Blk(fn, "j_body"), e.Blk(fn, "corr_check"), e.Blk(fn, "corr_mul"), e.Blk(fn, "corr_dec"),
            e.Blk(fn, "mul_sub"), e.Blk(fn, "ms_loop"), e.Blk(fn, "ms_body"), e.Blk(fn, "ms_done"),
            e.Blk(fn, "add_back"), e.Blk(fn, "ab_loop"), e.Blk(fn, "ab_body"), e.Blk(fn, "ab_done"),
            e.Blk(fn, "j_next"), e.Blk(fn, "denorm"));

        var vtn = e.LdDig(scratch, e.Sub(n, e.K(1), "vtn_i"), 0, "vtn");
        var vtn2 = e.LdDig(scratch, e.Sub(n, e.K(2), "vtn2_i"), 0, "vtn2");

        e.St(e.Sub(m, n, "j0"), slots.J);
        e.Br(blocks.JLoop);
        e.At(blocks.JLoop);
        e.CBr(e.Cmp(LlvmIntPredicate.Sge, e.Ld(slots.J, "j"), e.K(0), "j_more"), blocks.JBody, blocks.Denorm);

        e.At(blocks.JBody);
        var jv = e.Ld(slots.J, "jv");
        // qhat = (un[j+n]*B + un[j+n-1]) / vn[n-1]; rhat = the remainder. Both dividend digits
        // fit one i64, and vtn >= 2^31 after normalization.
        var numHi = e.LdDig(scratch, e.Add(n, e.Add(jv, n, "jn"), "jn_i"), 0, "num_hi");
        var numLo = e.LdDig(scratch, e.Add(n, e.Sub(e.Add(jv, n, "jn2"), e.K(1), "jn_1"), "jn1_i"), 0, "num_lo");
        var num = e.Or(e.Shl(numHi, e.K(32), "num_hs"), numLo, "num");
        e.St(e.UDiv(num, vtn, "qhat0"), slots.Qhat);
        e.St(e.URem(num, vtn, "rhat0"), slots.Rhat);
        e.Br(blocks.CorrCheck);
        return (slots, blocks, vtn, vtn2);
    }

    // Correct qhat down (at most twice): while qhat >= B, or qhat*vn[n-2] overshoots
    // rhat*B + un[j+n-2]. The multiply is only evaluated once qhat < B (else it could
    // overflow i64) — the short-circuit mirrors the C formulation.
    private static void EmitBiDivModLongCorr(Bi e, LlvmValueHandle scratch, LlvmValueHandle n, LlvmValueHandle vtn, LlvmValueHandle vtn2, EmitBiDivModLongSlots slots, EmitBiDivModLongBlocks blocks)
    {
        var big = e.K(0x100000000); // the base, 2^32
        e.At(blocks.CorrCheck);
        e.CBr(e.Cmp(LlvmIntPredicate.Uge, e.Ld(slots.Qhat, "qh_c"), big, "qh_big"), blocks.CorrDec, blocks.CorrMul);
        e.At(blocks.CorrMul);
        var prod = e.Mul(e.Ld(slots.Qhat, "qh_m"), vtn2, "corr_prod");
        var rhs = e.Or(e.Shl(e.Ld(slots.Rhat, "rh_m"), e.K(32), "rh_s"), e.LdDig(scratch, e.Add(n, e.Sub(e.Add(e.Ld(slots.J, "j_m"), n, "jn3"), e.K(2), "jn_2"), "jn2_i"), 0, "un_jn2"), "corr_rhs");
        e.CBr(e.Cmp(LlvmIntPredicate.Ugt, prod, rhs, "overshoot"), blocks.CorrDec, blocks.MulSub);
        e.At(blocks.CorrDec);
        e.St(e.Sub(e.Ld(slots.Qhat, "qh_d"), e.K(1), "qh_dec"), slots.Qhat);
        var rhNew = e.Add(e.Ld(slots.Rhat, "rh_d"), vtn, "rh_inc");
        e.St(rhNew, slots.Rhat);
        e.CBr(e.Cmp(LlvmIntPredicate.Ult, rhNew, big, "rh_small"), blocks.CorrCheck, blocks.MulSub);
    }

    // Multiply-and-subtract qhat*vn from un[j..j+n], tracking a SIGNED borrow k
    // (p's high half via logical shift, t's sign via arithmetic shift).
    private static void EmitBiDivModLongMulSub(Bi e, LlvmValueHandle q, LlvmValueHandle scratch, LlvmValueHandle n, EmitBiDivModLongSlots slots, EmitBiDivModLongBlocks blocks)
    {
        e.At(blocks.MulSub);
        e.St(e.K(0), slots.K);
        e.St(e.K(0), slots.I);
        e.Br(blocks.MsLoop);
        e.At(blocks.MsLoop);
        e.CBr(e.Cmp(LlvmIntPredicate.Slt, e.Ld(slots.I, "ms_i"), n, "ms_more"), blocks.MsBody, blocks.MsDone);
        e.At(blocks.MsBody);
        var msI = e.Ld(slots.I, "ms_iv");
        var msJ = e.Ld(slots.J, "ms_j");
        var p = e.Mul(e.Ld(slots.Qhat, "ms_qh"), e.LdDig(scratch, msI, 0, "ms_vn"), "ms_p");
        var unIj = e.Add(n, e.Add(msI, msJ, "ms_ij"), "ms_ij_i");
        var t = e.Sub(e.Sub(e.LdDig(scratch, unIj, 0, "ms_un"), e.Ld(slots.K, "ms_k"), "ms_t1"), e.And(p, e.K(0xFFFFFFFF), "ms_pl"), "ms_t");
        e.StDig(scratch, unIj, 0, t);
        e.St(e.Sub(e.Lshr(p, e.K(32), "ms_ph"), e.Ashr(t, e.K(32), "ms_ts"), "ms_k2"), slots.K);
        e.St(e.Add(msI, e.K(1), "ms_inc"), slots.I);
        e.Br(blocks.MsLoop);
        e.At(blocks.MsDone);
        var jvd = e.Ld(slots.J, "msd_j");
        var unJn = e.Add(n, e.Add(jvd, n, "msd_jn"), "msd_jn_i");
        var t2 = e.Sub(e.LdDig(scratch, unJn, 0, "msd_un"), e.Ld(slots.K, "msd_k"), "msd_t2");
        e.StDig(scratch, unJn, 0, t2);
        e.StDig(q, jvd, 2, e.Ld(slots.Qhat, "msd_qh"));
        e.CBr(e.Cmp(LlvmIntPredicate.Slt, t2, e.K(0), "underflow"), blocks.AddBack, blocks.JNext);
    }

    // Rare add-back (qhat was one too large): q[j]--, un += vn; then advance j.
    private static void EmitBiDivModLongAddBack(Bi e, LlvmValueHandle q, LlvmValueHandle scratch, LlvmValueHandle n, EmitBiDivModLongSlots slots, EmitBiDivModLongBlocks blocks)
    {
        e.At(blocks.AddBack);
        e.StDig(q, e.Ld(slots.J, "ab_j"), 2, e.Sub(e.Ld(slots.Qhat, "ab_qh"), e.K(1), "ab_dec"));
        e.St(e.K(0), slots.K);
        e.St(e.K(0), slots.I);
        e.Br(blocks.AbLoop);
        e.At(blocks.AbLoop);
        e.CBr(e.Cmp(LlvmIntPredicate.Slt, e.Ld(slots.I, "ab_i"), n, "ab_more"), blocks.AbBody, blocks.AbDone);
        e.At(blocks.AbBody);
        var abI = e.Ld(slots.I, "ab_iv");
        var abIj = e.Add(n, e.Add(abI, e.Ld(slots.J, "ab_j2"), "ab_ij"), "ab_ij_i");
        var t3 = e.Add(e.Add(e.LdDig(scratch, abIj, 0, "ab_un"), e.LdDig(scratch, abI, 0, "ab_vn"), "ab_s1"), e.Ld(slots.K, "ab_k"), "ab_t3");
        e.StDig(scratch, abIj, 0, t3);
        e.St(e.Lshr(t3, e.K(32), "ab_carry"), slots.K);
        e.St(e.Add(abI, e.K(1), "ab_inc"), slots.I);
        e.Br(blocks.AbLoop);
        e.At(blocks.AbDone);
        var abJn = e.Add(n, e.Add(e.Ld(slots.J, "ab_j3"), n, "ab_jn"), "ab_jn_i");
        e.StDig(scratch, abJn, 0, e.Add(e.LdDig(scratch, abJn, 0, "ab_top"), e.Ld(slots.K, "ab_kf"), "ab_top2"));
        e.Br(blocks.JNext);

        e.At(blocks.JNext);
        e.St(e.Sub(e.Ld(slots.J, "j_d"), e.K(1), "j_dec"), slots.J);
        e.Br(blocks.JLoop);
    }

    // Remainder: denormalize un[0..n-1] >> s into r's digits (the i64 shifts make the
    // s == 0 case uniform: << 32 truncates away, >> 32 zeroes).
    private static void EmitBiDivModLongDenorm(Bi e, LlvmValueHandle fn, LlvmValueHandle r, LlvmValueHandle scratch, LlvmValueHandle n, LlvmValueHandle s, LlvmValueHandle rs, LlvmValueHandle idxSlot, LlvmValueHandle rnSlot, LlvmBasicBlockHandle denorm, LlvmBasicBlockHandle finish)
    {
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

    // finish: normalize q with the XOR of operand signs, and r with a's sign.
    private static void EmitBiDivModFinish(Bi e, LlvmValueHandle a, LlvmValueHandle b, LlvmValueHandle q, LlvmValueHandle r, LlvmValueHandle na, LlvmValueHandle rnSlot, LlvmTypeHandle normTy, LlvmBasicBlockHandle finish)
    {
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

        EmitBiToDecimalSign(e, fn, a, bufPtr, lenSlot, outP, emitNeg, afterNeg);
    }

    // emitNeg → afterNeg: append a leading '-' for negatives, reverse the buffer, store the length.
    private static void EmitBiToDecimalSign(Bi e, LlvmValueHandle fn, LlvmValueHandle a, LlvmValueHandle bufPtr, LlvmValueHandle lenSlot, LlvmValueHandle outP, LlvmBasicBlockHandle emitNeg, LlvmBasicBlockHandle afterNeg)
    {
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
        EmitBiToDecimalLimbDiv(e, scratch, remSlot, liSlot, limbBody, limbLoop);
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

    // limbBody: divide scratch by 10 across 32-bit halves (high then low), storing the quotient back.
    private static void EmitBiToDecimalLimbDiv(Bi e, LlvmValueHandle scratch, LlvmValueHandle remSlot, LlvmValueHandle liSlot, LlvmBasicBlockHandle limbBody, LlvmBasicBlockHandle limbLoop)
    {
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
