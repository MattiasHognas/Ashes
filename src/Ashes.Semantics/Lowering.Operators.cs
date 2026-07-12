using System.Diagnostics;
using Ashes.Frontend;

namespace Ashes.Semantics;

public sealed partial class Lowering
{

    /// <summary>
    /// Emits a bitmask AND so that <paramref name="valueTemp"/> wraps to <paramref name="bits"/> bits.
    /// For u64 (bits == 64) no masking is needed since i64 already wraps in two's complement.
    /// </summary>
    private int EmitUIntMask(int valueTemp, int bits)
    {
        if (bits == 64)
        {
            return valueTemp;
        }

        long mask = (1L << bits) - 1L;
        int maskTemp = NewTemp();
        Emit(new IrInst.LoadConstInt(maskTemp, mask));
        int resultTemp = NewTemp();
        Emit(new IrInst.AndInt(resultTemp, valueTemp, maskTemp));
        return resultTemp;
    }

    private (int, TypeRef) LowerAdd(Expr.Add add)
    {
        using var diagnosticSpan = PushDiagnosticSpan(add);
        var (leftTemp, leftType) = LowerExpr(add.Left);
        var (rightTemp, rightType) = LowerExpr(add.Right);

        var leftPruned = Prune(leftType);
        var rightPruned = Prune(rightType);

        // Whether this add is an armed affine-accumulator append: the left operand's chain leaf is
        // the accumulator param the TCO back-edge armed (see _affineAppendCtx). Resolving to Str
        // then grows the accumulator's reservation (ConcatStrTip) instead of copying.
        int affineResvStart = -1;
        int affineResvEnd = -1;
        if (_affineAppendCtx is { } armedCtx)
        {
            var armedLeaf = add.Left;
            while (armedLeaf is Expr.Add armedAdd)
            {
                armedLeaf = armedAdd.Left;
            }

            if (armedLeaf is Expr.Var armedVar
                && string.Equals(armedVar.Name, armedCtx.Name, StringComparison.Ordinal)
                && Lookup(armedVar.Name) is Binding.Local armedLocal
                && armedLocal.Slot == armedCtx.Slot)
            {
                affineResvStart = armedCtx.ResvStart;
                affineResvEnd = armedCtx.ResvEnd;
            }
        }

        // Both operands unconstrained: don't eagerly pick Int. Unify them into one monomorphic var
        // (kept out of generalization via _addConstrainedTvars) so a later use resolves it — e.g.
        // the seed in `go("")(xs)` makes a `go(acc + x)` accumulator Str. Emit a provisional AddInt,
        // patched to ConcatStr/AddFloat in ResolveDeferredAdds once the operand type is known. If it
        // never resolves (an unused generic '+'), it defaults to Int there, matching the old result.
        if (leftPruned is TypeRef.TVar && rightPruned is TypeRef.TVar)
        {
            Unify(leftPruned, rightPruned);
            if (Prune(leftPruned) is TypeRef.TVar sharedVar)
            {
                _addConstrainedVars.Add(sharedVar);
                _hasDeferredAdds = true;
                int deferredTarget = NewTemp();
                Emit(new IrInst.AddInt(deferredTarget, leftTemp, rightTemp, sharedVar) { AffineResvStartSlot = affineResvStart, AffineResvEndSlot = affineResvEnd });
                return (deferredTarget, sharedVar);
            }
        }

        // Resolve type variables: prefer the other side's concrete type, defaulting to Int
        if (leftPruned is TypeRef.TVar)
        {
            TypeRef resolved = rightPruned switch
            {
                TypeRef.TStr => new TypeRef.TStr(),
                TypeRef.TFloat => new TypeRef.TFloat(),
                TypeRef.TBigInt => new TypeRef.TBigInt(),
                TypeRef.TUInt u => (TypeRef)new TypeRef.TUInt(u.Bits),
                _ => new TypeRef.TInt()
            };
            Unify(leftPruned, resolved);
            leftPruned = resolved;
        }
        if (rightPruned is TypeRef.TVar)
        {
            TypeRef resolved = leftPruned switch
            {
                TypeRef.TStr => new TypeRef.TStr(),
                TypeRef.TFloat => new TypeRef.TFloat(),
                TypeRef.TBigInt => new TypeRef.TBigInt(),
                TypeRef.TUInt u => (TypeRef)new TypeRef.TUInt(u.Bits),
                _ => new TypeRef.TInt()
            };
            Unify(rightPruned, resolved);
            rightPruned = resolved;
        }

        if (leftPruned is TypeRef.TInt && rightPruned is TypeRef.TInt)
        {
            int target = NewTemp();
            Emit(new IrInst.AddInt(target, leftTemp, rightTemp));
            return (target, new TypeRef.TInt());
        }

        if (leftPruned is TypeRef.TUInt luint && rightPruned is TypeRef.TUInt ruint)
        {
            if (luint.Bits != ruint.Bits)
            {
                var addUintTypes = PrettyPair(leftPruned, rightPruned);
                ReportDiagnostic(GetSpan(add), $"'+' requires matching unsigned widths, got {addUintTypes.Left} and {addUintTypes.Right}.", DiagnosticCodes.TypeMismatch);
                return CreateIntErrorFallback();
            }
            int raw = NewTemp();
            Emit(new IrInst.AddInt(raw, leftTemp, rightTemp));
            int wrapped = EmitUIntMask(raw, luint.Bits);
            return (wrapped, luint);
        }

        if (leftPruned is TypeRef.TFloat && rightPruned is TypeRef.TFloat)
        {
            int target = NewTemp();
            Emit(new IrInst.AddFloat(target, leftTemp, rightTemp));
            return (target, new TypeRef.TFloat());
        }

        if (leftPruned is TypeRef.TBigInt && rightPruned is TypeRef.TBigInt)
        {
            int target = NewTemp();
            Emit(new IrInst.BigIntBinary(target, leftTemp, rightTemp, "add"));
            return (target, new TypeRef.TBigInt());
        }

        if (leftPruned is TypeRef.TStr && rightPruned is TypeRef.TStr)
        {
            _usesConcatStr = true;
            int target = NewTemp();

            // Affine accumulator append (armed by the TCO back-edge for the accumulator's own
            // tail-call argument): the accumulator is uniquely owned above the loop-entry
            // watermark, so the append can grow it in place at the arena tip — the runtime checks
            // (and the plain-concat fallback) live in the ConcatStrTip emitter. Fires on every
            // step of a left-nested chain (`acc + r1 + r2`): each step's left VALUE is the same
            // uniquely-owned accumulator (extended in place, or a fresh tip copy after a fallback).
            if (affineResvStart >= 0)
            {
                Emit(new IrInst.ConcatStrTip(target, leftTemp, rightTemp, affineResvStart, affineResvEnd));
                return (target, new TypeRef.TStr());
            }

            Emit(new IrInst.ConcatStr(target, leftTemp, rightTemp));
            return (target, new TypeRef.TStr());
        }

        var addTypes = PrettyPair(leftPruned, rightPruned);
        ReportDiagnostic(GetSpan(add), $"'+' requires Int+Int, Float+Float, or Str+Str, got {addTypes.Left} and {addTypes.Right}.", DiagnosticCodes.TypeMismatch);
        int errorTemp = NewTemp();
        Emit(new IrInst.LoadConstInt(errorTemp, 0));
        return (errorTemp, new TypeRef.TInt());
    }

    private (int, TypeRef) LowerSubtract(Expr.Subtract sub)
    {
        using var diagnosticSpan = PushDiagnosticSpan(sub);
        var (leftTemp, leftType) = LowerExpr(sub.Left);
        var (rightTemp, rightType) = LowerExpr(sub.Right);

        // Unary negation `-x` desugars to `0 - x`; the synthesized 0 is an Int literal, which would
        // force the subtraction to Int and reject `-floatVar`. A literal 0 is the identity of every
        // numeric type (0 == 0.0 == 0N == 0u), so when the other operand is a concrete Float/BigInt/UInt
        // re-lower the 0 as that type's zero. (The literal-Int lowering above becomes dead and is elided.)
        if (sub.Left is Expr.IntLit { Value: 0 } && Prune(leftType) is TypeRef.TInt)
        {
            Expr? coercedZero = Prune(rightType) switch
            {
                TypeRef.TFloat => new Expr.FloatLit(0.0),
                TypeRef.TBigInt => new Expr.BigIntLit("0"),
                TypeRef.TUInt u => new Expr.UIntLit(0, u.Bits),
                _ => null,
            };
            if (coercedZero is not null)
            {
                (leftTemp, leftType) = LowerExpr(coercedZero);
            }
        }

        return LowerNumericBinaryOp(sub, leftTemp, leftType, rightTemp, rightType, (target, left, right) => new IrInst.SubInt(target, left, right), (target, left, right) => new IrInst.SubFloat(target, left, right), "'-'", bigIntOp: "sub");
    }

    private (int, TypeRef) LowerMultiply(Expr.Multiply mul)
    {
        using var diagnosticSpan = PushDiagnosticSpan(mul);
        var (leftTemp, leftType) = LowerExpr(mul.Left);
        var (rightTemp, rightType) = LowerExpr(mul.Right);

        // Both operands unconstrained: don't eagerly pick Int (as ResolveNumericOperandTypes would).
        // Unify them into one monomorphic var (kept out of generalization via _mulConstrainedVars) so a
        // later use resolves it — e.g. a `dot xs ys acc = … x * y + acc` fold used at Float. Emit a
        // provisional MulInt, patched to MulFloat/BigIntBinary in ResolveDeferredMuls once the operand
        // type is known. If it never resolves (an unused generic '*'), it defaults to Int there.
        if (Prune(leftType) is TypeRef.TVar && Prune(rightType) is TypeRef.TVar)
        {
            Unify(Prune(leftType), Prune(rightType));
            if (Prune(leftType) is TypeRef.TVar sharedVar)
            {
                _mulConstrainedVars.Add(sharedVar);
                _hasDeferredMuls = true;
                int deferredTarget = NewTemp();
                Emit(new IrInst.MulInt(deferredTarget, leftTemp, rightTemp, sharedVar));
                return (deferredTarget, sharedVar);
            }
        }

        return LowerNumericBinaryOp(mul, leftTemp, leftType, rightTemp, rightType, (target, left, right) => new IrInst.MulInt(target, left, right), (target, left, right) => new IrInst.MulFloat(target, left, right), "'*'", bigIntOp: "mul");
    }

    private (int, TypeRef) LowerDivide(Expr.Divide div)
    {
        using var diagnosticSpan = PushDiagnosticSpan(div);
        var (leftTemp, leftType) = LowerExpr(div.Left);
        var (rightTemp, rightType) = LowerExpr(div.Right);

        return LowerNumericBinaryOp(div, leftTemp, leftType, rightTemp, rightType, (target, left, right) => new IrInst.DivInt(target, left, right), (target, left, right) => new IrInst.DivFloat(target, left, right), "'/'", (target, left, right) => new IrInst.DivUInt(target, left, right), bigIntOp: "div");
    }

    // Modulo (remainder). Int/UInt reuse existing division: a % b = a - (a / b) * b (truncated,
    // so the remainder's sign follows the dividend, matching C `%`). BigInt uses the mod helper.
    private (int, TypeRef) LowerModulo(Expr.Modulo mod)
    {
        using var diagnosticSpan = PushDiagnosticSpan(mod);
        var (leftTemp, leftType) = LowerExpr(mod.Left);
        var (rightTemp, rightType) = LowerExpr(mod.Right);
        var (resolvedLeft, resolvedRight) = ResolveNumericOperandTypes(leftType, rightType);

        if (resolvedLeft is TypeRef.TBigInt && resolvedRight is TypeRef.TBigInt)
        {
            int target = NewTemp();
            Emit(new IrInst.BigIntBinary(target, leftTemp, rightTemp, "mod"));
            return (target, new TypeRef.TBigInt());
        }

        if (resolvedLeft is TypeRef.TInt && resolvedRight is TypeRef.TInt)
        {
            return (EmitRemainder(leftTemp, rightTemp, isUnsigned: false), new TypeRef.TInt());
        }

        if (resolvedLeft is TypeRef.TUInt luint && resolvedRight is TypeRef.TUInt ruint)
        {
            if (luint.Bits != ruint.Bits)
            {
                var widths = PrettyPair(resolvedLeft, resolvedRight);
                ReportDiagnostic(GetSpan(mod), $"'%' requires matching unsigned widths, got {widths.Left} and {widths.Right}.", DiagnosticCodes.TypeMismatch);
                return CreateIntErrorFallback();
            }
            int raw = EmitRemainder(leftTemp, rightTemp, isUnsigned: true);
            return (EmitUIntMask(raw, luint.Bits), luint);
        }

        var types = PrettyPair(resolvedLeft, resolvedRight);
        ReportDiagnostic(GetSpan(mod), $"'%' requires Int%Int, unsigned%unsigned, or BigInt%BigInt, got {types.Left} and {types.Right}.", DiagnosticCodes.TypeMismatch);
        return CreateIntErrorFallback();
    }

    private int EmitRemainder(int leftTemp, int rightTemp, bool isUnsigned)
    {
        int quotient = NewTemp();
        Emit(isUnsigned ? new IrInst.DivUInt(quotient, leftTemp, rightTemp) : new IrInst.DivInt(quotient, leftTemp, rightTemp));
        int product = NewTemp();
        Emit(new IrInst.MulInt(product, quotient, rightTemp));
        int remainder = NewTemp();
        Emit(new IrInst.SubInt(remainder, leftTemp, product));
        return remainder;
    }

    private (int, TypeRef) LowerBitwiseAnd(Expr.BitwiseAnd bitAnd)
    {
        using var diagnosticSpan = PushDiagnosticSpan(bitAnd);
        var (leftTemp, leftType) = LowerExpr(bitAnd.Left);
        var (rightTemp, rightType) = LowerExpr(bitAnd.Right);

        return LowerIntBinaryOp(bitAnd, leftTemp, leftType, rightTemp, rightType, (target, left, right) => new IrInst.AndInt(target, left, right), "'&'");
    }

    private (int, TypeRef) LowerBitwiseOr(Expr.BitwiseOr bitOr)
    {
        using var diagnosticSpan = PushDiagnosticSpan(bitOr);
        var (leftTemp, leftType) = LowerExpr(bitOr.Left);
        var (rightTemp, rightType) = LowerExpr(bitOr.Right);

        return LowerIntBinaryOp(bitOr, leftTemp, leftType, rightTemp, rightType, (target, left, right) => new IrInst.OrInt(target, left, right), "'|'");
    }

    private (int, TypeRef) LowerBitwiseXor(Expr.BitwiseXor bitXor)
    {
        using var diagnosticSpan = PushDiagnosticSpan(bitXor);
        var (leftTemp, leftType) = LowerExpr(bitXor.Left);
        var (rightTemp, rightType) = LowerExpr(bitXor.Right);

        return LowerIntBinaryOp(bitXor, leftTemp, leftType, rightTemp, rightType, (target, left, right) => new IrInst.XorInt(target, left, right), "'^'");
    }

    private (int, TypeRef) LowerShiftLeft(Expr.ShiftLeft shiftLeft)
    {
        using var diagnosticSpan = PushDiagnosticSpan(shiftLeft);
        var (leftTemp, leftType) = LowerExpr(shiftLeft.Left);
        var (rightTemp, rightType) = LowerExpr(shiftLeft.Right);

        return LowerIntBinaryOp(shiftLeft, leftTemp, leftType, rightTemp, rightType, (target, left, right) => new IrInst.ShlInt(target, left, right), "'<<'");
    }

    private (int, TypeRef) LowerShiftRight(Expr.ShiftRight shiftRight)
    {
        using var diagnosticSpan = PushDiagnosticSpan(shiftRight);
        var (leftTemp, leftType) = LowerExpr(shiftRight.Left);
        var (rightTemp, rightType) = LowerExpr(shiftRight.Right);

        return LowerIntBinaryOp(shiftRight, leftTemp, leftType, rightTemp, rightType, (target, left, right) => new IrInst.ShrInt(target, left, right), "'>>'");
    }

    private (int, TypeRef) LowerBitwiseNot(Expr.BitwiseNot bitwiseNot)
    {
        using var diagnosticSpan = PushDiagnosticSpan(bitwiseNot);
        var (operandTemp, operandType) = LowerExpr(bitwiseNot.Operand);
        var prunedOperandType = Prune(operandType);
        if (prunedOperandType is TypeRef.TVar)
        {
            Unify(prunedOperandType, new TypeRef.TInt());
            prunedOperandType = new TypeRef.TInt();
        }

        if (prunedOperandType is TypeRef.TUInt uintType)
        {
            // ~x for unsigned: XOR with the width mask so result stays within bit width.
            long uintMask = uintType.Bits == 64 ? -1L : (1L << uintType.Bits) - 1L;
            int maskTemp = NewTemp();
            Emit(new IrInst.LoadConstInt(maskTemp, uintMask));
            int xorTemp = NewTemp();
            Emit(new IrInst.XorInt(xorTemp, operandTemp, maskTemp));
            // Result already fits in width, no extra masking needed.
            return (xorTemp, uintType);
        }

        if (prunedOperandType is not TypeRef.TInt)
        {
            ReportDiagnostic(GetSpan(bitwiseNot), $"'~' requires Int or unsigned integer, got {Pretty(prunedOperandType)}.", DiagnosticCodes.TypeMismatch);
            return CreateIntErrorFallback();
        }

        int allOnes = NewTemp();
        Emit(new IrInst.LoadConstInt(allOnes, -1));
        int target = NewTemp();
        Emit(new IrInst.XorInt(target, operandTemp, allOnes));
        return (target, new TypeRef.TInt());
    }

    private (int, TypeRef) LowerGreaterThan(Expr.GreaterThan gt)
    {
        using var diagnosticSpan = PushDiagnosticSpan(gt);
        var (leftTemp, leftType) = LowerExpr(gt.Left);
        var (rightTemp, rightType) = LowerExpr(gt.Right);

        return LowerNumericComparisonOp(gt, leftTemp, leftType, rightTemp, rightType, (target, left, right) => new IrInst.CmpIntGt(target, left, right), (target, left, right) => new IrInst.CmpFloatGt(target, left, right), (target, left, right) => new IrInst.CmpUIntGt(target, left, right), "'>'");
    }

    private (int, TypeRef) LowerGreaterOrEqual(Expr.GreaterOrEqual ge)
    {
        using var diagnosticSpan = PushDiagnosticSpan(ge);
        var (leftTemp, leftType) = LowerExpr(ge.Left);
        var (rightTemp, rightType) = LowerExpr(ge.Right);

        return LowerNumericComparisonOp(ge, leftTemp, leftType, rightTemp, rightType, (target, left, right) => new IrInst.CmpIntGe(target, left, right), (target, left, right) => new IrInst.CmpFloatGe(target, left, right), (target, left, right) => new IrInst.CmpUIntGe(target, left, right), "'>='");
    }

    private (int, TypeRef) LowerLessThan(Expr.LessThan lt)
    {
        using var diagnosticSpan = PushDiagnosticSpan(lt);
        var (leftTemp, leftType) = LowerExpr(lt.Left);
        var (rightTemp, rightType) = LowerExpr(lt.Right);

        return LowerNumericComparisonOp(lt, leftTemp, leftType, rightTemp, rightType, (target, left, right) => new IrInst.CmpIntLt(target, left, right), (target, left, right) => new IrInst.CmpFloatLt(target, left, right), (target, left, right) => new IrInst.CmpUIntLt(target, left, right), "'<'");
    }

    private (int, TypeRef) LowerLessOrEqual(Expr.LessOrEqual le)
    {
        using var diagnosticSpan = PushDiagnosticSpan(le);
        var (leftTemp, leftType) = LowerExpr(le.Left);
        var (rightTemp, rightType) = LowerExpr(le.Right);

        return LowerNumericComparisonOp(le, leftTemp, leftType, rightTemp, rightType, (target, left, right) => new IrInst.CmpIntLe(target, left, right), (target, left, right) => new IrInst.CmpFloatLe(target, left, right), (target, left, right) => new IrInst.CmpUIntLe(target, left, right), "'<='");
    }

    private (int, TypeRef) LowerEqual(Expr.Equal eq)
    {
        return LowerEqualityOp(eq.Left, eq.Right, negate: false);
    }

    private (int, TypeRef) LowerNotEqual(Expr.NotEqual ne)
    {
        return LowerEqualityOp(ne.Left, ne.Right, negate: true);
    }

    private (int, TypeRef) LowerResultPipe(Expr.ResultPipe pipe)
    {
        using var diagnosticSpan = PushDiagnosticSpan(pipe);
        if (!TryGetStandardResultParts(out var resultSymbol, out var okConstructor, out _))
        {
            return ReturnNeverWithDummyTemp();
        }

        var (leftTemp, leftType) = LowerExpr(pipe.Left);
        var errorType = NewTypeVar();
        var successType = NewTypeVar();
        var expectedLeftType = new TypeRef.TNamedType(resultSymbol, [errorType, successType]);
        Unify(leftType, expectedLeftType);

        if (!TryGetResultTypeArgs(Prune(leftType), resultSymbol, out errorType, out successType))
        {
            return ReturnNeverWithDummyTemp();
        }

        var (funcTemp, funcType) = LowerExpr(pipe.Right);
        var returnType = NewTypeVar();
        Unify(funcType, new TypeRef.TFun(successType, returnType));

        if (Prune(funcType) is not TypeRef.TFun and not TypeRef.TVar)
        {
            return ReturnNeverWithDummyTemp();
        }

        var prunedErrorType = Prune(errorType);
        var prunedReturnType = Prune(returnType);
        var isFlatMap = TryGetResultTypeArgs(prunedReturnType, resultSymbol, out var nestedErrorType, out var nestedSuccessType);
        if (isFlatMap)
        {
            Unify(prunedErrorType, nestedErrorType);
        }

        TypeRef resultType = isFlatMap
            ? new TypeRef.TNamedType(resultSymbol, [Prune(prunedErrorType), Prune(nestedSuccessType)])
            : new TypeRef.TNamedType(resultSymbol, [Prune(prunedErrorType), prunedReturnType]);

        var resultSlot = NewLocal();
        var errorLabel = NewLabel("result_error");
        var endLabel = NewLabel("result_end");

        var tagTemp = NewTemp();
        var expectedOkTagTemp = NewTemp();
        var isOkTemp = NewTemp();
        Emit(new IrInst.GetAdtTag(tagTemp, leftTemp));
        Emit(new IrInst.LoadConstInt(expectedOkTagTemp, GetConstructorTag(okConstructor)));
        Emit(new IrInst.CmpIntEq(isOkTemp, tagTemp, expectedOkTagTemp));
        Emit(new IrInst.JumpIfFalse(isOkTemp, errorLabel));

        var payloadTemp = NewTemp();
        Emit(new IrInst.GetAdtField(payloadTemp, leftTemp, 0));
        var rhsResultTemp = NewTemp();
        Emit(new IrInst.CallClosure(rhsResultTemp, funcTemp, payloadTemp));

        if (isFlatMap)
        {
            Emit(new IrInst.StoreLocal(resultSlot, rhsResultTemp));
        }
        else
        {
            var wrappedTemp = LowerSingleFieldConstructorValue(okConstructor, rhsResultTemp);
            Emit(new IrInst.StoreLocal(resultSlot, wrappedTemp));
        }

        Emit(new IrInst.Jump(endLabel));
        Emit(new IrInst.Label(errorLabel));
        Emit(new IrInst.StoreLocal(resultSlot, leftTemp));
        Emit(new IrInst.Label(endLabel));

        var resultTemp = NewTemp();
        Emit(new IrInst.LoadLocal(resultTemp, resultSlot));
        return (resultTemp, Prune(resultType));
    }

    private (int, TypeRef) LowerResultMapErrorPipe(Expr.ResultMapErrorPipe pipe)
    {
        using var diagnosticSpan = PushDiagnosticSpan(pipe);
        if (!TryGetStandardResultParts(out var resultSymbol, out _, out var errorConstructor))
        {
            return ReturnNeverWithDummyTemp();
        }

        var (leftTemp, leftType) = LowerExpr(pipe.Left);
        var errorType = NewTypeVar();
        var successType = NewTypeVar();
        var expectedLeftType = new TypeRef.TNamedType(resultSymbol, [errorType, successType]);
        Unify(leftType, expectedLeftType);

        if (!TryGetResultTypeArgs(Prune(leftType), resultSymbol, out errorType, out successType))
        {
            return ReturnNeverWithDummyTemp();
        }

        var (funcTemp, funcType) = LowerExpr(pipe.Right);
        var mappedErrorType = NewTypeVar();
        Unify(funcType, new TypeRef.TFun(errorType, mappedErrorType));

        if (Prune(funcType) is not TypeRef.TFun and not TypeRef.TVar)
        {
            return ReturnNeverWithDummyTemp();
        }

        TypeRef resultType = new TypeRef.TNamedType(resultSymbol, [Prune(mappedErrorType), Prune(successType)]);
        var resultSlot = NewLocal();
        var errorLabel = NewLabel("result_map_error");
        var endLabel = NewLabel("result_map_error_end");

        var tagTemp = NewTemp();
        var expectedErrorTagTemp = NewTemp();
        var isErrorTemp = NewTemp();
        Emit(new IrInst.GetAdtTag(tagTemp, leftTemp));
        Emit(new IrInst.LoadConstInt(expectedErrorTagTemp, GetConstructorTag(errorConstructor)));
        Emit(new IrInst.CmpIntEq(isErrorTemp, tagTemp, expectedErrorTagTemp));
        Emit(new IrInst.JumpIfFalse(isErrorTemp, errorLabel));

        var payloadTemp = NewTemp();
        Emit(new IrInst.GetAdtField(payloadTemp, leftTemp, 0));
        var mappedPayloadTemp = NewTemp();
        Emit(new IrInst.CallClosure(mappedPayloadTemp, funcTemp, payloadTemp));
        var wrappedTemp = LowerSingleFieldConstructorValue(errorConstructor, mappedPayloadTemp);
        Emit(new IrInst.StoreLocal(resultSlot, wrappedTemp));
        Emit(new IrInst.Jump(endLabel));

        Emit(new IrInst.Label(errorLabel));
        Emit(new IrInst.StoreLocal(resultSlot, leftTemp));
        Emit(new IrInst.Label(endLabel));

        var resultTemp = NewTemp();
        Emit(new IrInst.LoadLocal(resultTemp, resultSlot));
        return (resultTemp, Prune(resultType));
    }

    private (int, TypeRef) LowerEqualityOp(Expr left, Expr right, bool negate)
    {
        using var diagnosticSpan = PushDiagnosticSpan(CombineSpans(left, right));
        var (leftTemp, leftType) = LowerExpr(left);
        var (rightTemp, rightType) = LowerExpr(right);

        var leftPruned = Prune(leftType);
        var rightPruned = Prune(rightType);

        // Both operands unconstrained: don't eagerly pick Int. Unify them into one monomorphic var
        // (kept out of generalization via _eqConstrainedVars) so a later use resolves it — e.g.
        // `assertEqual expected actual = expected == actual` called with two Strs. Emit a
        // provisional CmpIntEq/CmpIntNe, patched to CmpStrEq/CmpFloatEq (or their Ne counterparts)
        // in ResolveDeferredEqs once the operand type is known. If it never resolves (an unused
        // generic '=='), it defaults to Int there, matching the old result. Mirrors LowerAdd.
        if (leftPruned is TypeRef.TVar && rightPruned is TypeRef.TVar)
        {
            Unify(leftPruned, rightPruned);
            if (Prune(leftPruned) is TypeRef.TVar sharedVar)
            {
                _eqConstrainedVars.Add(sharedVar);
                _hasDeferredEqs = true;
                int deferredTarget = NewTemp();
                Emit(negate
                    ? new IrInst.CmpIntNe(deferredTarget, leftTemp, rightTemp, sharedVar)
                    : new IrInst.CmpIntEq(deferredTarget, leftTemp, rightTemp, sharedVar));
                return (deferredTarget, new TypeRef.TBool());
            }
        }

        // Resolve type variables: prefer the other side's concrete type, defaulting to Int
        if (leftPruned is TypeRef.TVar)
        {
            TypeRef resolved = rightPruned switch
            {
                TypeRef.TStr => new TypeRef.TStr(),
                TypeRef.TFloat => new TypeRef.TFloat(),
                TypeRef.TBool => new TypeRef.TBool(),
                TypeRef.TBigInt => new TypeRef.TBigInt(),
                TypeRef.TUInt u => (TypeRef)new TypeRef.TUInt(u.Bits),
                _ => new TypeRef.TInt()
            };
            Unify(leftPruned, resolved);
            leftPruned = resolved;
        }
        if (rightPruned is TypeRef.TVar)
        {
            TypeRef resolved = leftPruned switch
            {
                TypeRef.TStr => new TypeRef.TStr(),
                TypeRef.TFloat => new TypeRef.TFloat(),
                TypeRef.TBool => new TypeRef.TBool(),
                TypeRef.TBigInt => new TypeRef.TBigInt(),
                TypeRef.TUInt u => (TypeRef)new TypeRef.TUInt(u.Bits),
                _ => new TypeRef.TInt()
            };
            Unify(rightPruned, resolved);
            rightPruned = resolved;
        }

        if (leftPruned is TypeRef.TInt && rightPruned is TypeRef.TInt)
        {
            int target = NewTemp();
            Emit(negate ? new IrInst.CmpIntNe(target, leftTemp, rightTemp) : new IrInst.CmpIntEq(target, leftTemp, rightTemp));
            return (target, new TypeRef.TBool());
        }

        // Booleans are represented as i64 0/1, so they compare with the integer equality ops.
        if (leftPruned is TypeRef.TBool && rightPruned is TypeRef.TBool)
        {
            int target = NewTemp();
            Emit(negate ? new IrInst.CmpIntNe(target, leftTemp, rightTemp) : new IrInst.CmpIntEq(target, leftTemp, rightTemp));
            return (target, new TypeRef.TBool());
        }

        if (leftPruned is TypeRef.TBigInt && rightPruned is TypeRef.TBigInt)
        {
            int cmpTemp = NewTemp();
            Emit(new IrInst.BigIntCompare(cmpTemp, leftTemp, rightTemp));
            int zeroTemp = NewTemp();
            Emit(new IrInst.LoadConstInt(zeroTemp, 0));
            int target = NewTemp();
            Emit(negate ? new IrInst.CmpIntNe(target, cmpTemp, zeroTemp) : new IrInst.CmpIntEq(target, cmpTemp, zeroTemp));
            return (target, new TypeRef.TBool());
        }

        if (leftPruned is TypeRef.TUInt luint && rightPruned is TypeRef.TUInt ruint)
        {
            if (luint.Bits != ruint.Bits)
            {
                var uintWidthTypes = PrettyPair(leftPruned, rightPruned);
                var eqOp = negate ? "!=" : "==";
                ReportDiagnostic(CombineSpans(left, right), $"'{eqOp}' requires matching unsigned widths, got {uintWidthTypes.Left} and {uintWidthTypes.Right}.", DiagnosticCodes.TypeMismatch);
                int boolFallback = NewTemp();
                Emit(new IrInst.LoadConstBool(boolFallback, false));
                return (boolFallback, new TypeRef.TBool());
            }
            int target = NewTemp();
            Emit(negate ? new IrInst.CmpIntNe(target, leftTemp, rightTemp) : new IrInst.CmpIntEq(target, leftTemp, rightTemp));
            return (target, new TypeRef.TBool());
        }

        if (leftPruned is TypeRef.TFloat && rightPruned is TypeRef.TFloat)
        {
            int target = NewTemp();
            Emit(negate ? new IrInst.CmpFloatNe(target, leftTemp, rightTemp) : new IrInst.CmpFloatEq(target, leftTemp, rightTemp));
            return (target, new TypeRef.TBool());
        }

        if (leftPruned is TypeRef.TStr && rightPruned is TypeRef.TStr)
        {
            int target = NewTemp();
            Emit(negate ? new IrInst.CmpStrNe(target, leftTemp, rightTemp) : new IrInst.CmpStrEq(target, leftTemp, rightTemp));
            return (target, new TypeRef.TBool());
        }

        var op = negate ? "!=" : "==";
        var equalityTypes = PrettyPair(leftPruned, rightPruned);
        ReportDiagnostic(0, $"'{op}' requires Int{op}Int, Float{op}Float, or Str{op}Str, got {equalityTypes.Left} and {equalityTypes.Right}.", DiagnosticCodes.TypeMismatch);
        int errorTemp = NewTemp();
        Emit(new IrInst.LoadConstBool(errorTemp, false));
        return (errorTemp, new TypeRef.TBool());
    }

    private (int, TypeRef) LowerNumericBinaryOp(
        Expr expr,
        int leftTemp,
        TypeRef leftType,
        int rightTemp,
        TypeRef rightType,
        Func<int, int, int, IrInst> intFactory,
        Func<int, int, int, IrInst> floatFactory,
        string op,
        Func<int, int, int, IrInst>? uintFactory = null,
        string? bigIntOp = null)
    {
        var (resolvedLeft, resolvedRight) = ResolveNumericOperandTypes(leftType, rightType);

        if (resolvedLeft is TypeRef.TInt && resolvedRight is TypeRef.TInt)
        {
            int target = NewTemp();
            Emit(intFactory(target, leftTemp, rightTemp));
            return (target, new TypeRef.TInt());
        }

        if (bigIntOp is not null && resolvedLeft is TypeRef.TBigInt && resolvedRight is TypeRef.TBigInt)
        {
            int target = NewTemp();
            Emit(new IrInst.BigIntBinary(target, leftTemp, rightTemp, bigIntOp));
            return (target, new TypeRef.TBigInt());
        }

        if (resolvedLeft is TypeRef.TUInt luint && resolvedRight is TypeRef.TUInt ruint)
        {
            if (luint.Bits != ruint.Bits)
            {
                var uintWidthTypes = PrettyPair(resolvedLeft, resolvedRight);
                ReportDiagnostic(GetSpan(expr), $"{op} requires matching unsigned widths, got {uintWidthTypes.Left} and {uintWidthTypes.Right}.", DiagnosticCodes.TypeMismatch);
                return CreateIntErrorFallback();
            }
            int raw = NewTemp();
            Emit((uintFactory ?? intFactory)(raw, leftTemp, rightTemp));
            int wrapped = EmitUIntMask(raw, luint.Bits);
            return (wrapped, luint);
        }

        if (resolvedLeft is TypeRef.TFloat && resolvedRight is TypeRef.TFloat)
        {
            int target = NewTemp();
            Emit(floatFactory(target, leftTemp, rightTemp));
            return (target, new TypeRef.TFloat());
        }

        var types = PrettyPair(resolvedLeft, resolvedRight);
        ReportDiagnostic(GetSpan(expr), $"{op} requires Int{op}Int or Float{op}Float, got {types.Left} and {types.Right}.", DiagnosticCodes.TypeMismatch);
        return CreateIntErrorFallback();
    }

    private (int, TypeRef) LowerIntBinaryOp(
        Expr expr,
        int leftTemp,
        TypeRef leftType,
        int rightTemp,
        TypeRef rightType,
        Func<int, int, int, IrInst> intFactory,
        string op)
    {
        var left = Prune(leftType);
        var right = Prune(rightType);

        if (left is TypeRef.TVar)
        {
            TypeRef resolved = right is TypeRef.TUInt u ? (TypeRef)new TypeRef.TUInt(u.Bits) : new TypeRef.TInt();
            Unify(left, resolved);
            left = resolved;
        }

        if (right is TypeRef.TVar)
        {
            TypeRef resolved = left is TypeRef.TUInt u ? (TypeRef)new TypeRef.TUInt(u.Bits) : new TypeRef.TInt();
            Unify(right, resolved);
            right = resolved;
        }

        if (left is TypeRef.TInt && right is TypeRef.TInt)
        {
            int target = NewTemp();
            Emit(intFactory(target, leftTemp, rightTemp));
            return (target, new TypeRef.TInt());
        }

        if (left is TypeRef.TUInt luint && right is TypeRef.TUInt ruint)
        {
            if (luint.Bits != ruint.Bits)
            {
                var uintWidthTypes = PrettyPair(left, right);
                ReportDiagnostic(GetSpan(expr), $"{op} requires matching unsigned widths, got {uintWidthTypes.Left} and {uintWidthTypes.Right}.", DiagnosticCodes.TypeMismatch);
                return CreateIntErrorFallback();
            }
            int raw = NewTemp();
            Emit(intFactory(raw, leftTemp, rightTemp));
            int wrapped = EmitUIntMask(raw, luint.Bits);
            return (wrapped, luint);
        }

        var types = PrettyPair(left, right);
        ReportDiagnostic(GetSpan(expr), $"{op} requires Int{op}Int, got {types.Left} and {types.Right}.", DiagnosticCodes.TypeMismatch);
        return CreateIntErrorFallback();
    }

    private (int, TypeRef) CreateIntErrorFallback()
    {
        int fallback = NewTemp();
        Emit(new IrInst.LoadConstInt(fallback, 0));
        return (fallback, new TypeRef.TInt());
    }

    private (int, TypeRef) LowerNumericComparisonOp(
        Expr expr,
        int leftTemp,
        TypeRef leftType,
        int rightTemp,
        TypeRef rightType,
        Func<int, int, int, IrInst> intFactory,
        Func<int, int, int, IrInst> floatFactory,
        Func<int, int, int, IrInst>? uintFactory,
        string op)
    {
        var (resolvedLeft, resolvedRight) = ResolveNumericOperandTypes(leftType, rightType);

        if (resolvedLeft is TypeRef.TInt && resolvedRight is TypeRef.TInt)
        {
            int target = NewTemp();
            Emit(intFactory(target, leftTemp, rightTemp));
            return (target, new TypeRef.TBool());
        }

        // BigInt ordering: compare(a, b) yields -1/0/1, then apply the Int predicate against 0.
        if (resolvedLeft is TypeRef.TBigInt && resolvedRight is TypeRef.TBigInt)
        {
            int cmpTemp = NewTemp();
            Emit(new IrInst.BigIntCompare(cmpTemp, leftTemp, rightTemp));
            int zeroTemp = NewTemp();
            Emit(new IrInst.LoadConstInt(zeroTemp, 0));
            int target = NewTemp();
            Emit(intFactory(target, cmpTemp, zeroTemp));
            return (target, new TypeRef.TBool());
        }

        if (resolvedLeft is TypeRef.TUInt luint && resolvedRight is TypeRef.TUInt ruint)
        {
            if (luint.Bits != ruint.Bits)
            {
                var uintWidthTypes = PrettyPair(resolvedLeft, resolvedRight);
                ReportDiagnostic(GetSpan(expr), $"{op} requires matching unsigned widths, got {uintWidthTypes.Left} and {uintWidthTypes.Right}.", DiagnosticCodes.TypeMismatch);
                int boolFallback = NewTemp();
                Emit(new IrInst.LoadConstBool(boolFallback, false));
                return (boolFallback, new TypeRef.TBool());
            }
            int target = NewTemp();
            Emit((uintFactory ?? intFactory)(target, leftTemp, rightTemp));
            return (target, new TypeRef.TBool());
        }

        if (resolvedLeft is TypeRef.TFloat && resolvedRight is TypeRef.TFloat)
        {
            int target = NewTemp();
            Emit(floatFactory(target, leftTemp, rightTemp));
            return (target, new TypeRef.TBool());
        }

        var types = PrettyPair(resolvedLeft, resolvedRight);
        ReportDiagnostic(GetSpan(expr), $"{op} requires Int{op}Int or Float{op}Float, got {types.Left} and {types.Right}.", DiagnosticCodes.TypeMismatch);
        int fallback = NewTemp();
        Emit(new IrInst.LoadConstBool(fallback, false));
        return (fallback, new TypeRef.TBool());
    }

    private (TypeRef Left, TypeRef Right) ResolveNumericOperandTypes(TypeRef leftType, TypeRef rightType)
    {
        var left = Prune(leftType);
        var right = Prune(rightType);

        if (left is TypeRef.TVar)
        {
            TypeRef resolved = right switch
            {
                TypeRef.TFloat => new TypeRef.TFloat(),
                TypeRef.TBigInt => new TypeRef.TBigInt(),
                TypeRef.TUInt u => (TypeRef)new TypeRef.TUInt(u.Bits),
                _ => new TypeRef.TInt()
            };
            Unify(left, resolved);
            left = resolved;
        }

        if (right is TypeRef.TVar)
        {
            TypeRef resolved = left switch
            {
                TypeRef.TFloat => new TypeRef.TFloat(),
                TypeRef.TBigInt => new TypeRef.TBigInt(),
                TypeRef.TUInt u => (TypeRef)new TypeRef.TUInt(u.Bits),
                _ => new TypeRef.TInt()
            };
            Unify(right, resolved);
            right = resolved;
        }

        return (left, right);
    }
}
