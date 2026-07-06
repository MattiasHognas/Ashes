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

        LlvmTypeHandle voidType = LlvmApi.VoidTypeInContext(state.Target.Context);
        LlvmTypeHandle fnType = LlvmApi.FunctionType(voidType, [state.I8Ptr, state.I8Ptr, state.I8Ptr, state.I8Ptr]);
        LlvmValueHandle fn = LlvmApi.GetNamedFunction(state.Target.Module, BigIntDivModFn);
        LlvmApi.BuildCall2(state.Target.Builder, fnType, fn,
            [BigIntAsPtr(state, left, "bigint_da"), BigIntAsPtr(state, right, "bigint_db"),
             BigIntAsPtr(state, qAddress, "bigint_q"), BigIntAsPtr(state, rAddress, "bigint_r")], "");
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

    // divmod(a, b, q, r): binary long division, truncated toward zero.
    private static void EmitBiDivMod(Bi e)
    {
        var fn = e.Declare(BigIntDivModFn, e.Void, [e.Ptr, e.Ptr, e.Ptr, e.Ptr]);
        LlvmValueHandle a = e.Param(fn, 0), b = e.Param(fn, 1), q = e.Param(fn, 2), r = e.Param(fn, 3);
        var entry = e.Blk(fn, "entry");
        var trivial = e.Blk(fn, "trivial");
        var copyR = e.Blk(fn, "copy_r");
        var zeroR = e.Blk(fn, "zero_r");
        var proceed = e.Blk(fn, "proceed");

        var cmpTy = LlvmApi.FunctionType(e.I64, [e.Ptr, e.I64, e.Ptr, e.I64]);
        var magTy = LlvmApi.FunctionType(e.I64, [e.Ptr, e.I64, e.Ptr, e.I64, e.Ptr]);
        var normTy = LlvmApi.FunctionType(e.Void, [e.Ptr, e.I64, e.I64]);

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
        // q limbs = 0 for [1..na]; r starts empty (rn=0)
        var qInitLoop = e.Blk(fn, "qinit_loop");
        var qInitBody = e.Blk(fn, "qinit_body");
        var bitLoop = e.Blk(fn, "bit_loop");
        var bitBody = e.Blk(fn, "bit_body");
        var reduce = e.Blk(fn, "reduce");
        var noReduce = e.Blk(fn, "no_reduce");
        var finish = e.Blk(fn, "finish");

        var kSlot = e.Slot("k");
        var bitSlot = e.Slot("bit");
        var rnSlot = e.Slot("rn");
        // bit length of a: (na-1)*64 + bits(top limb)
        var topLimb = e.LdW(a, na, "top");
        // compute high bit index of topLimb via a small loop
        var bl = EmitBiBitLength(e, fn, na, topLimb);

        e.St(e.K(1), kSlot);
        e.Br(qInitLoop);
        e.At(qInitLoop);
        e.CBr(e.Cmp(LlvmIntPredicate.Sle, e.Ld(kSlot, "k"), na, "k_le"), qInitBody, bitLoop);
        e.At(qInitBody);
        var kv = e.Ld(kSlot, "kv");
        e.StW(q, kv, e.K(0));
        e.St(e.Add(kv, e.K(1), "kinc"), kSlot);
        e.Br(qInitLoop);

        e.At(bitLoop);
        e.St(e.K(0), rnSlot);
        e.St(e.Sub(bl, e.K(1), "bl_1"), bitSlot); // bit = abits-1
        e.Br(bitBody);
        e.At(bitBody);
        var bit = e.Ld(bitSlot, "bit");
        var moreBits = e.Cmp(LlvmIntPredicate.Sge, bit, e.K(0), "bit_ge0");
        var bitStep = e.Blk(fn, "bit_step");
        e.CBr(moreBits, bitStep, finish);
        e.At(bitStep);
        // ab = (a[1 + bit/64] >> (bit%64)) & 1
        var limbIdx = e.Add(e.K(1), e.UDiv(bit, e.K(64), "bit_div"), "limb_idx");
        var off = e.Sub(bit, e.Mul(e.UDiv(bit, e.K(64), "bd2"), e.K(64), "bd2m"), "off"); // bit%64
        var abit = e.And(e.Lshr(e.LdW(a, limbIdx, "alimb"), off, "ash"), e.K(1), "abit");
        // r = r*2 + abit  (shl1_or)
        EmitBiShl1Or(e, fn, r, rnSlot, abit);
        // if cmp_mag(r, rn, b, nb) >= 0: r -= b; set q bit
        var rn = e.Ld(rnSlot, "rn");
        var c2 = LlvmApi.BuildCall2(e.B, cmpTy, e.Get("bi_cmp_mag"), [r, rn, b, nb], "c2");
        e.CBr(e.Cmp(LlvmIntPredicate.Sge, c2, e.K(0), "r_ge_b"), reduce, noReduce);
        e.At(reduce);
        var rn2 = LlvmApi.BuildCall2(e.B, magTy, e.Get("bi_sub_mag"), [r, e.Ld(rnSlot, "rn2"), b, nb, r], "rsub");
        e.St(rn2, rnSlot);
        EmitBiStripZeros(e, fn, r, rnSlot, "rstrip");
        // set bit in q: q[1+bit/64] |= (1 << (bit%64))
        var qIdx = e.Add(e.K(1), e.UDiv(bit, e.K(64), "qd"), "q_idx");
        var qcur = e.LdW(q, qIdx, "qcur");
        e.StW(q, qIdx, e.Or(qcur, e.Shl(e.K(1), off, "qbit"), "qset"));
        e.Br(noReduce);
        e.At(noReduce);
        e.St(e.Sub(e.Ld(bitSlot, "bd"), e.K(1), "bit_dec"), bitSlot);
        e.Br(bitBody);

        e.At(finish);
        var qneg = e.And(LlvmApi.BuildXor(e.B, e.Neg(a, "san"), e.Neg(b, "sbn"), "qx"), e.K(1), "qneg");
        LlvmApi.BuildCall2(e.B, normTy, e.Get("bi_normalize"), [q, qneg, na], "");
        LlvmApi.BuildCall2(e.B, normTy, e.Get("bi_normalize"), [r, e.Neg(a, "ran"), e.Ld(rnSlot, "rnf")], "");
        e.RetVoid();
    }

    // bit length of magnitude with na limbs whose top limb is topLimb: (na-1)*64 + highbit(topLimb)+1.
    private static LlvmValueHandle EmitBiBitLength(Bi e, LlvmValueHandle fn, LlvmValueHandle na, LlvmValueHandle topLimb)
    {
        var pre = e.Blk(fn, "bl_pre");
        var loop = e.Blk(fn, "bl_loop");
        var body = e.Blk(fn, "bl_body");
        var doneBB = e.Blk(fn, "bl_done");
        e.Br(pre);
        e.At(pre);
        var bitsSlot = e.Slot("bl_bits");
        var topSlot = e.Slot("bl_top");
        e.St(e.Mul(e.Sub(na, e.K(1), "na_1"), e.K(64), "base_bits"), bitsSlot);
        e.St(topLimb, topSlot);
        e.Br(loop);
        e.At(loop);
        e.CBr(e.Cmp(LlvmIntPredicate.Ne, e.Ld(topSlot, "t"), e.K(0), "t_nz"), body, doneBB);
        e.At(body);
        e.St(e.Add(e.Ld(bitsSlot, "bb"), e.K(1), "binc"), bitsSlot);
        e.St(e.Lshr(e.Ld(topSlot, "tt"), e.K(1), "tsh"), topSlot);
        e.Br(loop);
        e.At(doneBB);
        return e.Ld(bitsSlot, "bl_result");
    }

    // r = r*2 + bit (bit in {0,1}); r has *rnSlot limbs with room for one more.
    private static void EmitBiShl1Or(Bi e, LlvmValueHandle fn, LlvmValueHandle r, LlvmValueHandle rnSlot, LlvmValueHandle bit)
    {
        var pre = e.Blk(fn, "shl_pre");
        var loop = e.Blk(fn, "shl_loop");
        var body = e.Blk(fn, "shl_body");
        var after = e.Blk(fn, "shl_after");
        var grow = e.Blk(fn, "shl_grow");
        var doneBB = e.Blk(fn, "shl_done");
        e.Br(pre);
        e.At(pre);
        var iSlot = e.Slot("shl_i");
        var carrySlot = e.Slot("shl_carry");
        e.St(e.K(1), iSlot);
        e.St(e.And(bit, e.K(1), "bit1"), carrySlot);
        e.Br(loop);
        e.At(loop);
        e.CBr(e.Cmp(LlvmIntPredicate.Sle, e.Ld(iSlot, "i"), e.Ld(rnSlot, "rn"), "i_le"), body, after);
        e.At(body);
        var i2 = e.Ld(iSlot, "iv");
        var x = e.LdW(r, i2, "x");
        var carry = e.Ld(carrySlot, "carry");
        e.StW(r, i2, e.Or(e.Shl(x, e.K(1), "xsh"), carry, "xor"));
        e.St(e.Lshr(x, e.K(63), "newc"), carrySlot);
        e.St(e.Add(i2, e.K(1), "inc"), iSlot);
        e.Br(loop);
        e.At(after);
        e.CBr(e.Cmp(LlvmIntPredicate.Ne, e.Ld(carrySlot, "cf"), e.K(0), "has_c"), grow, doneBB);
        e.At(grow);
        var nn = e.Add(e.Ld(rnSlot, "rn2"), e.K(1), "rn_inc");
        e.St(nn, rnSlot);
        e.StW(r, nn, e.Ld(carrySlot, "cc"));
        e.Br(doneBB);
        e.At(doneBB);
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
