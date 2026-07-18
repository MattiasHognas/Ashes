using System.Diagnostics;
using Ashes.Frontend;

namespace Ashes.Semantics;

public sealed partial class Lowering
{
    private (int, TypeRef) LowerPrint(Expr arg)
    {
        using var diagnosticSpan = PushDiagnosticSpan(arg);
        var (vTemp, vType) = LowerExpr(arg);
        var t = Prune(vType);

        if (t is TypeRef.TNever)
        {
            return (vTemp, t);
        }

        if (t is TypeRef.TInt or TypeRef.TUInt { Bits: < 64 })
        {
            _usesPrintInt = true;
            Emit(new IrInst.PrintInt(vTemp));
            return LowerUnitValue();
        }

        if (t is TypeRef.TStr)
        {
            _usesPrintStr = true;
            Emit(new IrInst.PrintStr(vTemp));
            return LowerUnitValue();
        }

        if (t is TypeRef.TBool)
        {
            _usesPrintBool = true;
            Emit(new IrInst.PrintBool(vTemp));
            return LowerUnitValue();
        }

        ReportDiagnostic(GetSpan(arg), $"print() does not support type {Pretty(t)} yet.");
        return (vTemp, t);
    }

    private (int, TypeRef) LowerWrite(Expr arg, bool appendNewline)
    {
        using var diagnosticSpan = PushDiagnosticSpan(arg);
        var (valueTemp, valueType) = LowerExpr(arg);
        var loweredType = Prune(valueType);

        if (loweredType is TypeRef.TNever)
        {
            return (valueTemp, loweredType);
        }

        if (loweredType is TypeRef.TVar)
        {
            Unify(loweredType, new TypeRef.TStr());
            loweredType = new TypeRef.TStr();
        }

        if (loweredType is not TypeRef.TStr)
        {
            ReportDiagnostic(GetSpan(arg), $"{(appendNewline ? "writeLine" : "write")}() expects Str but got {Pretty(loweredType)}.");
            return (valueTemp, loweredType);
        }

        if (appendNewline)
        {
            _usesPrintStr = true;
            Emit(new IrInst.PrintStr(valueTemp));
        }
        else
        {
            Emit(new IrInst.WriteStr(valueTemp));
        }

        return LowerUnitValue();
    }

    // Ashes.IO.writeBytes : Bytes -> Unit. Writes a raw Bytes buffer to stdout verbatim (no UTF-8
    // constraint, unlike write, which takes a Str). Bytes and Str share the same [len][payload] heap
    // layout and WriteStr writes exactly `len` bytes with no encoding validation, so it carries the raw
    // buffer correctly -- this is what lets binary output (e.g. a packed PBM image) reach stdout.
    private (int, TypeRef) LowerWriteBytes(Expr arg)
    {
        using var diagnosticSpan = PushDiagnosticSpan(arg);
        var (valueTemp, valueType) = LowerExpr(arg);
        var loweredType = Prune(valueType);

        if (loweredType is TypeRef.TNever)
        {
            return (valueTemp, loweredType);
        }

        if (loweredType is TypeRef.TVar)
        {
            Unify(loweredType, new TypeRef.TBytes());
            loweredType = new TypeRef.TBytes();
        }

        if (loweredType is not TypeRef.TBytes)
        {
            ReportDiagnostic(GetSpan(arg), $"writeBytes() expects Bytes but got {Pretty(loweredType)}.");
            return (valueTemp, loweredType);
        }

        Emit(new IrInst.WriteStr(valueTemp));
        return LowerUnitValue();
    }

    private (int, TypeRef) LowerReadLine(Expr arg)
    {
        using var diagnosticSpan = PushDiagnosticSpan(arg);
        var (unitTemp, unitType) = LowerExpr(arg);
        var loweredType = Prune(unitType);

        if (loweredType is TypeRef.TNever)
        {
            return (unitTemp, loweredType);
        }

        Unify(loweredType, _resolvedTypes["Unit"]);

        var target = NewTemp();
        Emit(new IrInst.ReadLine(target));
        return (target, CreateMaybeType(new TypeRef.TStr()));
    }

    private (int, TypeRef) LowerUnitValue()
    {
        if (!_constructorSymbols.TryGetValue("Unit", out var unitConstructor) || unitConstructor.Arity != 0)
        {
            throw new InvalidOperationException("Built-in Unit constructor is not registered.");
        }

        return LowerNullaryConstructor(unitConstructor);
    }

    private (int, TypeRef) LowerFileReadText(Expr pathArg)
    {
        using var diagnosticSpan = PushDiagnosticSpan(pathArg);
        var (pathTemp, pathType) = LowerExpr(pathArg);
        var loweredType = Prune(pathType);

        if (loweredType is TypeRef.TNever)
        {
            return (pathTemp, loweredType);
        }

        if (loweredType is TypeRef.TVar)
        {
            Unify(loweredType, new TypeRef.TStr());
            loweredType = new TypeRef.TStr();
        }

        if (loweredType is not TypeRef.TStr)
        {
            ReportDiagnostic(GetSpan(pathArg), $"Ashes.IO.File.readText() expects Str but got {Pretty(loweredType)}.");
            return (pathTemp, loweredType);
        }

        var target = NewTemp();
        Emit(new IrInst.FileReadText(target, pathTemp));
        return (target, CreateStringResultType(new TypeRef.TStr()));
    }

    // Ashes.IO.File.open : Str -> Result(Str, FileHandle)
    private Binding.Intrinsic CreateFileOpenBinding()
    {
        return new Binding.Intrinsic(
            IntrinsicKind.FileOpen,
            new TypeScheme([], new TypeRef.TFun(new TypeRef.TStr(), CreateStringResultType(_resolvedTypes["FileHandle"])))
        );
    }

    private (int, TypeRef) LowerFileOpen(Expr pathArg)
    {
        using var diagnosticSpan = PushDiagnosticSpan(pathArg);
        var (pathTemp, pathType) = LowerExpr(pathArg);
        var loweredType = Prune(pathType);

        if (loweredType is TypeRef.TNever)
        {
            return (pathTemp, loweredType);
        }

        if (loweredType is TypeRef.TVar)
        {
            Unify(loweredType, new TypeRef.TStr());
            loweredType = new TypeRef.TStr();
        }

        if (loweredType is not TypeRef.TStr)
        {
            ReportDiagnostic(GetSpan(pathArg), $"Ashes.IO.File.open() expects Str but got {Pretty(loweredType)}.");
            return (pathTemp, CreateStringResultType(_resolvedTypes["FileHandle"]));
        }

        var target = NewTemp();
        Emit(new IrInst.FileOpen(target, pathTemp));
        return (target, CreateStringResultType(_resolvedTypes["FileHandle"]));
    }

    // Ashes.IO.File.readChunk : FileHandle -> Int -> Result(Str, Str)
    private Binding.Intrinsic CreateFileReadChunkBinding()
    {
        return new Binding.Intrinsic(
            IntrinsicKind.FileReadChunk,
            new TypeScheme([], new TypeRef.TFun(_resolvedTypes["FileHandle"], new TypeRef.TFun(new TypeRef.TInt(), CreateStringResultType(new TypeRef.TStr()))))
        );
    }

    private (int, TypeRef) LowerFileReadChunk(Expr handleArg, Expr countArg)
    {
        using var diagnosticSpan = PushDiagnosticSpan(handleArg);
        CheckUseAfterDrop(handleArg);
        var (handleTemp, handleType) = LowerExpr(handleArg);
        Unify(Prune(handleType), _resolvedTypes["FileHandle"]);
        var (countTemp, countType) = LowerExpr(countArg);
        var prunedCount = Prune(countType);
        if (prunedCount is TypeRef.TVar)
        {
            Unify(prunedCount, new TypeRef.TInt());
        }
        else if (prunedCount is not TypeRef.TInt and not TypeRef.TNever)
        {
            ReportDiagnostic(GetSpan(countArg), $"Ashes.IO.File.readChunk() expects Int but got {Pretty(prunedCount)}.");
        }

        var target = NewTemp();
        Emit(new IrInst.FileReadChunk(target, handleTemp, countTemp));
        return (target, CreateStringResultType(new TypeRef.TStr()));
    }

    // Ashes.IO.File.readLine : FileHandle -> Maybe(Str). Reads one line (newline stripped) through a
    // refillable module-global buffer, returning None at EOF. Unlike readChunk it threads no buffer
    // state through the caller, so a whole-file fold can be a single loop that carries only its
    // accumulator (the fold's in-place reuse then stays constant-memory instead of re-deep-copying).
    private Binding.Intrinsic CreateFileReadLineBinding()
    {
        return new Binding.Intrinsic(
            IntrinsicKind.FileReadLine,
            new TypeScheme([], new TypeRef.TFun(_resolvedTypes["FileHandle"], CreateMaybeType(new TypeRef.TStr())))
        );
    }

    private (int, TypeRef) LowerFileReadLine(Expr handleArg)
    {
        using var diagnosticSpan = PushDiagnosticSpan(handleArg);
        CheckUseAfterDrop(handleArg);
        var (handleTemp, handleType) = LowerExpr(handleArg);
        Unify(Prune(handleType), _resolvedTypes["FileHandle"]);
        var target = NewTemp();
        Emit(new IrInst.FileReadLine(target, handleTemp));
        return (target, CreateMaybeType(new TypeRef.TStr()));
    }

    // Ashes.IO.File.close : FileHandle -> Result(Str, Unit)
    private Binding.Intrinsic CreateFileCloseBinding()
    {
        return new Binding.Intrinsic(
            IntrinsicKind.FileClose,
            new TypeScheme([], new TypeRef.TFun(_resolvedTypes["FileHandle"], CreateStringResultType(_resolvedTypes["Unit"])))
        );
    }

    private (int, TypeRef) LowerFileClose(Expr handleArg)
    {
        using var diagnosticSpan = PushDiagnosticSpan(handleArg);
        var (handleTemp, handleType) = LowerExpr(handleArg);
        Unify(Prune(handleType), _resolvedTypes["FileHandle"]);

        // Mark the resource as dropped (explicitly closed) so it is not auto-dropped again at
        // scope exit and a later use is reported as use-after-close.
        if (handleArg is Expr.Var varExpr)
        {
            TryMarkDropped(varExpr.Name);
        }

        var target = NewTemp();
        Emit(new IrInst.FileClose(target, handleTemp));
        return (target, CreateStringResultType(_resolvedTypes["Unit"]));
    }

    // Ashes.Internal.deepCopy : forall a. a -> a  (produces an independent deep copy)
    private Binding.Intrinsic CreateInternalDeepCopyBinding()
    {
        var tv = (TypeRef.TVar)NewTypeVar();
        return new Binding.Intrinsic(
            IntrinsicKind.InternalDeepCopy,
            new TypeScheme([new TypeVar(tv.Id, "a")], new TypeRef.TFun(tv, tv))
        );
    }

    private (int, TypeRef) LowerInternalDeepCopy(Expr arg)
    {
        using var diagnosticSpan = PushDiagnosticSpan(arg);
        var (temp, type) = LowerExpr(arg);
        var pruned = Prune(type);
        if (pruned is TypeRef.TNever)
        {
            return (temp, pruned);
        }

        return (EmitDeepCopy(temp, pruned), pruned);
    }

    // Ashes.Task.Parallel.both : forall a b. (Unit -> a) -> (Unit -> b) -> (a, b)
    // Evaluates left(Unit) and right(Unit) and returns the pair. Purity makes the result
    // order-independent, so it is identical to the sequential pair regardless of which thunk
    // finishes first. At concrete result types `right` may run on a worker thread; abstract
    // (polymorphic) result types always run sequentially (a correct fallback) — see
    // LowerParallelBoth (in this file).
    private Binding.Intrinsic CreateParallelBothBinding()
    {
        var a = (TypeRef.TVar)NewTypeVar();
        var b = (TypeRef.TVar)NewTypeVar();
        var unit = _resolvedTypes["Unit"];
        var leftFn = new TypeRef.TFun(unit, a);
        var rightFn = new TypeRef.TFun(unit, b);
        var resultTuple = new TypeRef.TTuple([a, b]);
        return new Binding.Intrinsic(
            IntrinsicKind.ParallelBoth,
            new TypeScheme(
                [new TypeVar(a.Id, "a"), new TypeVar(b.Id, "b")],
                new TypeRef.TFun(leftFn, new TypeRef.TFun(rightFn, resultTuple))));
    }

    // withWorkers : Int -> (Unit -> a) -> a. A dynamically-scoped runtime override of the worker
    // cap: the enclosing scope's value is saved, the requested count installed, the thunk run, and
    // the previous value restored on normal return. The effective fork cap is min(override,
    // compiledMax), computed in the backend, so a count above the compiled ceiling still clamps.
    private Binding.Intrinsic CreateParallelWithWorkersBinding()
    {
        var a = (TypeRef.TVar)NewTypeVar();
        var unit = _resolvedTypes["Unit"];
        var actionFn = new TypeRef.TFun(unit, a);
        return new Binding.Intrinsic(
            IntrinsicKind.ParallelWithWorkers,
            new TypeScheme(
                [new TypeVar(a.Id, "a")],
                new TypeRef.TFun(new TypeRef.TInt(), new TypeRef.TFun(actionFn, a))));
    }

    private (int, TypeRef) LowerParallelWithWorkers(Expr countArg, Expr actionArg)
    {
        var savedTailPos = _tcoCtx?.InTailPosition ?? false;
        if (_tcoCtx is not null) _tcoCtx.InTailPosition = false;

        using (PushDiagnosticSpan(countArg))
        {
            var (countTemp, countType) = LowerExpr(countArg);
            Unify(countType, new TypeRef.TInt());

            var (actionTemp, actionType) = LowerExpr(actionArg);
            var resultType = Prune(actionType) is TypeRef.TFun actionFn ? Prune(actionFn.Ret) : NewTypeVar();
            Unify(actionType, new TypeRef.TFun(_resolvedTypes["Unit"], resultType));

            // Runtime validation: a non-positive count is a programming error, matching how other
            // stdlib misuse (e.g. panic paths) is surfaced.
            int zeroTemp = NewTemp();
            Emit(new IrInst.LoadConstInt(zeroTemp, 0));
            int isPositiveTemp = NewTemp();
            Emit(new IrInst.CmpIntGt(isPositiveTemp, countTemp, zeroTemp));
            string okLabel = $"withworkers_ok_{_nextLambdaId++}";
            Emit(new IrInst.JumpIfFalse(isPositiveTemp, okLabel + "_bad"));
            Emit(new IrInst.Jump(okLabel));
            Emit(new IrInst.Label(okLabel + "_bad"));
            int panicTemp = NewTemp();
            Emit(new IrInst.LoadConstStr(panicTemp, InternString("Ashes.Task.Parallel.withWorkers: worker count must be positive.")));
            Emit(new IrInst.PanicStr(panicTemp));
            Emit(new IrInst.Label(okLabel));

            // Save the enclosing override, install this scope's count, run the thunk, restore.
            int savedOverrideTemp = NewTemp();
            Emit(new IrInst.LoadParallelWorkerOverride(savedOverrideTemp));
            Emit(new IrInst.StoreParallelWorkerOverride(countTemp));

            var (unitTemp, _) = LowerUnitValue();
            int resultTemp = NewTemp();
            Emit(new IrInst.CallClosure(resultTemp, actionTemp, unitTemp));

            Emit(new IrInst.StoreParallelWorkerOverride(savedOverrideTemp));

            if (_tcoCtx is not null) _tcoCtx.InTailPosition = savedTailPos;
            return (resultTemp, resultType);
        }
    }

    private (int, TypeRef) LowerParallelBoth(Expr leftThunk, Expr rightThunk)
    {
        var savedTailPos = _tcoCtx?.InTailPosition ?? false;
        if (_tcoCtx is not null) _tcoCtx.InTailPosition = false;

        var (leftTemp, leftType) = LowerExpr(leftThunk);
        var (rightTemp, rightType) = LowerExpr(rightThunk);
        var aType = Prune(leftType) is TypeRef.TFun leftFn ? Prune(leftFn.Ret) : NewTypeVar();
        var bType = Prune(rightType) is TypeRef.TFun rightFn ? Prune(rightFn.Ret) : NewTypeVar();

        int aTemp = NewTemp();
        int bTemp;
        var (leftUnit, _) = LowerUnitValue();

        // The right thunk may run on a worker thread with its own arena, so its result must be
        // a value we can safely lift back into the parent arena: a scalar (self-contained) or a
        // type EmitDeepCopy fully duplicates. Otherwise (polymorphic/abstract result, or a type
        // whose deep copy would alias the worker arena) fall back to sequential evaluation — same
        // result, just not parallel. The deep-copy needs the concrete type, which is unavailable
        // for an abstract result, so abstract always degrades here.
        if (CanRunRightOnWorker(bType))
        {
            int descTemp = NewTemp();
            Emit(new IrInst.ParallelFork(descTemp, rightTemp));
            Emit(new IrInst.CallClosure(aTemp, leftTemp, leftUnit));
            int bRawTemp = NewTemp();
            Emit(new IrInst.ParallelJoin(bRawTemp, descTemp));
            bTemp = EmitDeepCopy(bRawTemp, bType);
            Emit(new IrInst.ParallelCleanup(descTemp));
        }
        else
        {
            Emit(new IrInst.CallClosure(aTemp, leftTemp, leftUnit));
            var (rightUnit, _) = LowerUnitValue();
            bTemp = NewTemp();
            Emit(new IrInst.CallClosure(bTemp, rightTemp, rightUnit));
        }

        int tupleTemp = NewTemp();
        Emit(new IrInst.Alloc(tupleTemp, 16));
        Emit(new IrInst.StoreMemOffset(tupleTemp, 0, aTemp));
        Emit(new IrInst.StoreMemOffset(tupleTemp, 8, bTemp));

        if (_tcoCtx is not null) _tcoCtx.InTailPosition = savedTailPos;
        return (tupleTemp, new TypeRef.TTuple([aType, bType]));
    }

    /// <summary>
    /// True when a worker thread's result of type <paramref name="type"/> can be safely lifted
    /// into the parent arena: scalars carry no arena pointers, and the other admitted types are
    /// exactly those <see cref="EmitDeepCopy"/> duplicates fully (no aliasing into the worker
    /// arena). Conservative: abstract types, closures, and partially-copied containers fall back
    /// to sequential evaluation.
    /// </summary>
    private bool CanRunRightOnWorker(TypeRef type)
    {
        var pruned = Prune(type);
        return pruned switch
        {
            TypeRef.TInt or TypeRef.TUInt or TypeRef.TFloat or TypeRef.TBool => true,
            TypeRef.TStr or TypeRef.TBytes => true,
            TypeRef.TList list => CanArenaReset(Prune(list.Element))
                || Prune(list.Element) is TypeRef.TStr
                || (Prune(list.Element) is TypeRef.TList inner && CanArenaReset(Prune(inner.Element))),
            TypeRef.TTuple tuple => tuple.Elements.All(CanRunRightOnWorker),
            TypeRef.TNamedType named when !BuiltinRegistry.IsResourceTypeName(named.Symbol.Name)
                => TrySynthesizeAdtCopier(named) is not null,
            _ => false,
        };
    }

    private (int, TypeRef) LowerFileWriteText(Expr pathArg, Expr textArg)
    {
        using var diagnosticSpan = PushDiagnosticSpan(pathArg);
        var (pathTemp, pathType) = LowerExpr(pathArg);
        var pathLoweredType = Prune(pathType);

        if (pathLoweredType is TypeRef.TNever)
        {
            return (pathTemp, pathLoweredType);
        }

        if (pathLoweredType is TypeRef.TVar)
        {
            Unify(pathLoweredType, new TypeRef.TStr());
            pathLoweredType = new TypeRef.TStr();
        }

        if (pathLoweredType is not TypeRef.TStr)
        {
            ReportDiagnostic(GetSpan(pathArg), $"Ashes.IO.File.writeText() expects Str for path but got {Pretty(pathLoweredType)}.");
            return (pathTemp, pathLoweredType);
        }

        using var textDiagnosticSpan = PushDiagnosticSpan(textArg);
        var (textTemp, textType) = LowerExpr(textArg);
        var textLoweredType = Prune(textType);

        if (textLoweredType is TypeRef.TNever)
        {
            return (textTemp, textLoweredType);
        }

        if (textLoweredType is TypeRef.TVar)
        {
            Unify(textLoweredType, new TypeRef.TStr());
            textLoweredType = new TypeRef.TStr();
        }

        if (textLoweredType is not TypeRef.TStr)
        {
            ReportDiagnostic(GetSpan(textArg), $"Ashes.IO.File.writeText() expects Str for text but got {Pretty(textLoweredType)}.");
            return (textTemp, textLoweredType);
        }

        var target = NewTemp();
        Emit(new IrInst.FileWriteText(target, pathTemp, textTemp));
        return (target, CreateStringResultType(_resolvedTypes["Unit"]));
    }

    private (int, TypeRef) LowerFileExists(Expr pathArg)
    {
        using var diagnosticSpan = PushDiagnosticSpan(pathArg);
        var (pathTemp, pathType) = LowerExpr(pathArg);
        var loweredType = Prune(pathType);

        if (loweredType is TypeRef.TNever)
        {
            return (pathTemp, loweredType);
        }

        if (loweredType is TypeRef.TVar)
        {
            Unify(loweredType, new TypeRef.TStr());
            loweredType = new TypeRef.TStr();
        }

        if (loweredType is not TypeRef.TStr)
        {
            ReportDiagnostic(GetSpan(pathArg), $"Ashes.IO.File.exists() expects Str but got {Pretty(loweredType)}.");
            return (pathTemp, loweredType);
        }

        var target = NewTemp();
        Emit(new IrInst.FileExists(target, pathTemp));
        return (target, CreateStringResultType(new TypeRef.TBool()));
    }

    private (int, TypeRef) LowerTextUncons(Expr textArg)
    {
        using var diagnosticSpan = PushDiagnosticSpan(textArg);
        var (textTemp, textType) = LowerExpr(textArg);
        var loweredType = Prune(textType);

        if (loweredType is TypeRef.TNever)
        {
            return (textTemp, loweredType);
        }

        if (loweredType is TypeRef.TVar)
        {
            Unify(loweredType, new TypeRef.TStr());
            loweredType = new TypeRef.TStr();
        }

        if (loweredType is not TypeRef.TStr)
        {
            ReportDiagnostic(GetSpan(textArg), $"Ashes.Text.uncons() expects Str but got {Pretty(loweredType)}.");
            return (textTemp, loweredType);
        }

        var target = NewTemp();
        Emit(new IrInst.TextUncons(target, textTemp));
        return (target, CreateMaybeType(new TypeRef.TTuple([new TypeRef.TStr(), new TypeRef.TStr()])));
    }

    // Ashes.Text.Regex.Native (PCRE2) primitives.
    // The compiled pattern is a pcre2_code* carried as an Int handle. Ashes.Text.Regex (Regex.ash) wraps
    // it in a Regex ADT and composes the ergonomic Result/Option API from these.

    private int LowerRegexScalarArg(Expr arg, TypeRef expected, string label)
    {
        var (temp, argType) = LowerExpr(arg);
        var pruned = Prune(argType);
        if (pruned is TypeRef.TNever)
        {
            return temp;
        }

        if (pruned is TypeRef.TVar)
        {
            Unify(pruned, expected);
        }
        else
        {
            bool ok = (expected, pruned) switch
            {
                (TypeRef.TStr, TypeRef.TStr) => true,
                (TypeRef.TInt, TypeRef.TInt) => true,
                _ => false,
            };
            if (!ok)
            {
                ReportDiagnostic(GetSpan(arg), $"{label} expects {Pretty(expected)} but got {Pretty(pruned)}.");
            }
        }

        return temp;
    }

    private (int, TypeRef) LowerRegexCompile(Expr patternArg)
    {
        using var diagnosticSpan = PushDiagnosticSpan(patternArg);
        int patternTemp = LowerRegexScalarArg(patternArg, new TypeRef.TStr(), "Ashes.Text.Regex.Native.compile()");
        var target = NewTemp();
        Emit(new IrInst.RegexCompile(target, patternTemp));
        return (target, new TypeRef.TInt());
    }

    private (int, TypeRef) LowerRegexCompileError(Expr patternArg)
    {
        using var diagnosticSpan = PushDiagnosticSpan(patternArg);
        int patternTemp = LowerRegexScalarArg(patternArg, new TypeRef.TStr(), "Ashes.Text.Regex.Native.compileError()");
        var target = NewTemp();
        Emit(new IrInst.RegexCompileError(target, patternTemp));
        return (target, new TypeRef.TStr());
    }

    private (int, TypeRef) LowerRegexFind(Expr codeArg, Expr subjectArg, Expr startArg)
    {
        int codeTemp = LowerRegexScalarArg(codeArg, new TypeRef.TInt(), "Ashes.Text.Regex.Native.find()");
        int subjectTemp = LowerRegexScalarArg(subjectArg, new TypeRef.TStr(), "Ashes.Text.Regex.Native.find()");
        int startTemp = LowerRegexScalarArg(startArg, new TypeRef.TInt(), "Ashes.Text.Regex.Native.find()");
        var target = NewTemp();
        Emit(new IrInst.RegexFind(target, codeTemp, subjectTemp, startTemp));
        return (target, CreateMaybeType(new TypeRef.TTuple([new TypeRef.TInt(), new TypeRef.TInt()])));
    }

    private (int, TypeRef) LowerRegexCaptures(Expr codeArg, Expr subjectArg, Expr startArg)
    {
        int codeTemp = LowerRegexScalarArg(codeArg, new TypeRef.TInt(), "Ashes.Text.Regex.Native.captures()");
        int subjectTemp = LowerRegexScalarArg(subjectArg, new TypeRef.TStr(), "Ashes.Text.Regex.Native.captures()");
        int startTemp = LowerRegexScalarArg(startArg, new TypeRef.TInt(), "Ashes.Text.Regex.Native.captures()");
        var target = NewTemp();
        Emit(new IrInst.RegexCaptures(target, codeTemp, subjectTemp, startTemp));
        return (target, CreateMaybeType(new TypeRef.TList(CreateMaybeType(new TypeRef.TStr()))));
    }

    private (int, TypeRef) LowerRegexSubstitute(Expr codeArg, Expr subjectArg, Expr replacementArg)
    {
        int codeTemp = LowerRegexScalarArg(codeArg, new TypeRef.TInt(), "Ashes.Text.Regex.Native.substitute()");
        int subjectTemp = LowerRegexScalarArg(subjectArg, new TypeRef.TStr(), "Ashes.Text.Regex.Native.substitute()");
        int replacementTemp = LowerRegexScalarArg(replacementArg, new TypeRef.TStr(), "Ashes.Text.Regex.Native.substitute()");
        var target = NewTemp();
        Emit(new IrInst.RegexSubstitute(target, codeTemp, subjectTemp, replacementTemp));
        return (target, new TypeRef.TStr());
    }

    private (int, TypeRef) LowerTextParseInt(Expr textArg)
    {
        using var diagnosticSpan = PushDiagnosticSpan(textArg);
        var (textTemp, textType) = LowerExpr(textArg);
        var loweredType = Prune(textType);

        if (loweredType is TypeRef.TNever)
        {
            return (textTemp, loweredType);
        }

        if (loweredType is TypeRef.TVar)
        {
            Unify(loweredType, new TypeRef.TStr());
            loweredType = new TypeRef.TStr();
        }

        if (loweredType is not TypeRef.TStr)
        {
            ReportDiagnostic(GetSpan(textArg), $"Ashes.Text.parseInt() expects Str but got {Pretty(loweredType)}.");
            return (textTemp, loweredType);
        }

        var target = NewTemp();
        Emit(new IrInst.TextParseInt(target, textTemp));
        return (target, CreateStringResultType(new TypeRef.TInt()));
    }

    private (int, TypeRef) LowerTextParseFloat(Expr textArg)
    {
        using var diagnosticSpan = PushDiagnosticSpan(textArg);
        var (textTemp, textType) = LowerExpr(textArg);
        var loweredType = Prune(textType);

        if (loweredType is TypeRef.TNever)
        {
            return (textTemp, loweredType);
        }

        if (loweredType is TypeRef.TVar)
        {
            Unify(loweredType, new TypeRef.TStr());
            loweredType = new TypeRef.TStr();
        }

        if (loweredType is not TypeRef.TStr)
        {
            ReportDiagnostic(GetSpan(textArg), $"Ashes.Text.parseFloat() expects Str but got {Pretty(loweredType)}.");
            return (textTemp, loweredType);
        }

        var target = NewTemp();
        Emit(new IrInst.TextParseFloat(target, textTemp));
        return (target, CreateStringResultType(new TypeRef.TFloat()));
    }

    private (int, TypeRef) LowerTextFromInt(Expr valueArg)
    {
        using var diagnosticSpan = PushDiagnosticSpan(valueArg);
        var (valueTemp, valueType) = LowerExpr(valueArg);
        var loweredType = Prune(valueType);

        if (loweredType is TypeRef.TNever)
        {
            return (valueTemp, loweredType);
        }

        if (loweredType is TypeRef.TVar)
        {
            Unify(loweredType, new TypeRef.TInt());
            loweredType = new TypeRef.TInt();
        }

        if (loweredType is not TypeRef.TInt && loweredType is not TypeRef.TUInt { Bits: <= 32 })
        {
            ReportDiagnostic(GetSpan(valueArg), $"Ashes.Text.fromInt() expects Int but got {Pretty(loweredType)}.");
            return (valueTemp, loweredType);
        }

        var target = NewTemp();
        Emit(new IrInst.TextFromInt(target, valueTemp));
        return (target, new TypeRef.TStr());
    }

    private (int, TypeRef) LowerTextFromFloat(Expr valueArg)
    {
        using var diagnosticSpan = PushDiagnosticSpan(valueArg);
        var (valueTemp, valueType) = LowerExpr(valueArg);
        var loweredType = Prune(valueType);

        if (loweredType is TypeRef.TNever)
        {
            return (valueTemp, loweredType);
        }

        if (loweredType is TypeRef.TVar)
        {
            Unify(loweredType, new TypeRef.TFloat());
            loweredType = new TypeRef.TFloat();
        }

        if (loweredType is not TypeRef.TFloat)
        {
            ReportDiagnostic(GetSpan(valueArg), $"Ashes.Text.fromFloat() expects Float but got {Pretty(loweredType)}.");
            return (valueTemp, loweredType);
        }

        var target = NewTemp();
        Emit(new IrInst.TextFromFloat(target, valueTemp));
        return (target, new TypeRef.TStr());
    }

    private (int, TypeRef) LowerTextFormatFloat(Expr valueArg, Expr decimalsArg)
    {
        using var valueDiagnosticSpan = PushDiagnosticSpan(valueArg);
        var (valueTemp, valueType) = LowerExpr(valueArg);
        var prunedValueType = Prune(valueType);

        if (prunedValueType is TypeRef.TNever)
        {
            return (valueTemp, prunedValueType);
        }

        if (prunedValueType is TypeRef.TVar)
        {
            Unify(prunedValueType, new TypeRef.TFloat());
            prunedValueType = new TypeRef.TFloat();
        }

        if (prunedValueType is not TypeRef.TFloat)
        {
            ReportDiagnostic(GetSpan(valueArg), $"Ashes.Text.formatFloat() expects Float but got {Pretty(prunedValueType)}.");
            return (valueTemp, prunedValueType);
        }

        using var decimalsDiagnosticSpan = PushDiagnosticSpan(decimalsArg);
        var (decimalsTemp, decimalsType) = LowerExpr(decimalsArg);
        var prunedDecimalsType = Prune(decimalsType);

        if (prunedDecimalsType is TypeRef.TNever)
        {
            return (decimalsTemp, prunedDecimalsType);
        }

        if (prunedDecimalsType is TypeRef.TVar)
        {
            Unify(prunedDecimalsType, new TypeRef.TInt());
            prunedDecimalsType = new TypeRef.TInt();
        }

        if (prunedDecimalsType is not TypeRef.TInt)
        {
            ReportDiagnostic(GetSpan(decimalsArg), $"Ashes.Text.formatFloat() expects Int for the decimals argument but got {Pretty(prunedDecimalsType)}.");
            return (decimalsTemp, prunedDecimalsType);
        }

        var target = NewTemp();
        Emit(new IrInst.TextFormatFloat(target, valueTemp, decimalsTemp));
        return (target, new TypeRef.TStr());
    }

    private (int, TypeRef) LowerTextToHex(Expr valueArg)
    {
        using var diagnosticSpan = PushDiagnosticSpan(valueArg);
        var (valueTemp, valueType) = LowerExpr(valueArg);
        var loweredType = Prune(valueType);

        if (loweredType is TypeRef.TNever)
        {
            return (valueTemp, loweredType);
        }

        if (loweredType is TypeRef.TVar)
        {
            Unify(loweredType, new TypeRef.TInt());
            loweredType = new TypeRef.TInt();
        }

        if (loweredType is not TypeRef.TInt and not TypeRef.TUInt)
        {
            ReportDiagnostic(GetSpan(valueArg), $"Ashes.Text.toHex() expects Int but got {Pretty(loweredType)}.");
            return (valueTemp, loweredType);
        }

        var target = NewTemp();
        Emit(new IrInst.TextToHex(target, valueTemp));
        return (target, new TypeRef.TStr());
    }

    private (int, TypeRef) LowerTextAsciiCase(Expr textArg, bool upper)
    {
        using var diagnosticSpan = PushDiagnosticSpan(textArg);
        var (textTemp, textType) = LowerExpr(textArg);
        var loweredType = Prune(textType);

        if (loweredType is TypeRef.TNever)
        {
            return (textTemp, loweredType);
        }

        if (loweredType is TypeRef.TVar)
        {
            Unify(loweredType, new TypeRef.TStr());
            loweredType = new TypeRef.TStr();
        }

        if (loweredType is not TypeRef.TStr)
        {
            var name = upper ? "asciiUpper" : "asciiLower";
            ReportDiagnostic(GetSpan(textArg), $"Ashes.Text.{name}() expects Str but got {Pretty(loweredType)}.");
            return (textTemp, loweredType);
        }

        var target = NewTemp();
        Emit(new IrInst.TextAsciiCase(target, textTemp, upper));
        return (target, new TypeRef.TStr());
    }

    private TypeRef.TNamedType CreateStringResultType(TypeRef successType)
    {
        if (!_typeSymbols.TryGetValue("Result", out var resultSymbol) || resultSymbol.TypeParameters.Count != 2)
        {
            throw new InvalidOperationException("Built-in Result type is not registered.");
        }

        return new TypeRef.TNamedType(resultSymbol, [new TypeRef.TStr(), successType]);
    }

    private TypeRef.TNamedType CreateStringTaskType(TypeRef successType)
    {
        if (!_typeSymbols.TryGetValue("Task", out var taskSymbol) || taskSymbol.TypeParameters.Count != 2)
        {
            throw new InvalidOperationException("Built-in Task type is not registered.");
        }

        return new TypeRef.TNamedType(taskSymbol, [new TypeRef.TStr(), successType]);
    }

    private (int, TypeRef) LowerCapturedStringTask(
        IReadOnlyList<int> captureTemps,
        TypeRef successType,
        Expr origin,
        Func<IReadOnlyList<int>, int> emitBody,
        bool loopResetEligible = false)
    {
        _usesAsync = true;

        int envPtrTemp = LowerCapturedStringTaskEmitEnvironment(captureTemps);

        string coroutineLabel = $"coroutine_{_nextLambdaId++}";

        var saved = LowerCapturedStringTaskSaveAndResetState();

        int stateStructSlot = NewLocal();
        int dummyArgSlot = NewLocal();
        Debug.Assert(stateStructSlot == 0, "State struct slot must be 0");

        _scopes.Clear();
        _scopes.Push(new Dictionary<string, Binding>(StringComparer.Ordinal));
        _ownershipScopes.Clear();
        _ownershipScopes.Push(new Dictionary<string, OwnershipInfo>(StringComparer.Ordinal));
        _arenaWatermarks.Clear();
        _arenaWatermarks.Push((-1, -1));

        int stateStructSize = LowerCapturedStringTaskBuildCoroutine(captureTemps, emitBody, coroutineLabel);

        LowerCapturedStringTaskRestoreState(saved);

        var taskType = CreateStringTaskType(successType);
        _usesClosures = true;
        int closureTemp = NewTemp();
        Emit(new IrInst.MakeClosure(closureTemp, coroutineLabel, envPtrTemp, captureTemps.Count * 8));
        int taskTemp = NewTemp();
        Emit(new IrInst.CreateTask(taskTemp, closureTemp, stateStructSize, captureTemps.Count) { LoopResetEligible = loopResetEligible });
        return (taskTemp, taskType);
    }

    private int LowerCapturedStringTaskEmitEnvironment(IReadOnlyList<int> captureTemps)
    {
        var envPtrTemp = NewTemp();
        if (captureTemps.Count == 0)
        {
            Emit(new IrInst.LoadConstInt(envPtrTemp, 0));
        }
        else
        {
            Emit(new IrInst.Alloc(envPtrTemp, captureTemps.Count * 8));
            for (int i = 0; i < captureTemps.Count; i++)
            {
                Emit(new IrInst.StoreMemOffset(envPtrTemp, i * 8, captureTemps[i]));
            }
        }

        return envPtrTemp;
    }

    // The lowering state saved across a coroutine body's out-of-line emission (see
    // LowerCapturedStringTaskSaveAndResetState / LowerCapturedStringTaskRestoreState).
    private sealed record CapturedStringTaskSavedState(
        List<IrInst> Instructions,
        int NextTempSlot,
        int NextLocalSlot,
        Dictionary<string, Binding>[] Scopes,
        Dictionary<string, OwnershipInfo>[] OwnershipScopes,
        (int CursorSlot, int EndSlot)[] ArenaWatermarks,
        TcoContext? TcoCtx,
        Dictionary<int, string> LocalNames,
        Dictionary<int, TypeRef> LocalTypes,
        Dictionary<int, Dictionary<int, (int Slot, int TotalRefs)>> ReuseTokenFieldBindings,
        Dictionary<int, int> ReuseBindingSeenBySlot,
        Dictionary<int, string> ReuseTrackedSlotNames);

    private CapturedStringTaskSavedState LowerCapturedStringTaskSaveAndResetState()
    {
        var savedInst = new List<IrInst>(_inst);
        var savedTemp = _nextTempSlot;
        var savedLocal = _nextLocalSlot;
        var savedScopes = _scopes.ToArray();
        var savedOwnershipScopes = _ownershipScopes.ToArray();
        var savedArenaWatermarks = _arenaWatermarks.ToArray();
        var savedTcoCtx = _tcoCtx;
        var savedLocalNames = new Dictionary<int, string>(_localNames);
        var savedLocalTypes = new Dictionary<int, TypeRef>(_localTypes);
        _tcoCtx = null;

        _inst.Clear();
        _nextTempSlot = 0;
        var savedReuseTokenFieldBindings = new Dictionary<int, Dictionary<int, (int Slot, int TotalRefs)>>(_reuseTokenFieldBindings);
        var savedReuseBindingSeen = new Dictionary<int, int>(_reuseBindingSeenBySlot);
        var savedReuseTrackedSlotNames = new Dictionary<int, string>(_reuseTrackedSlotNames);
        _reuseTokenFieldBindings.Clear();
        _reuseBindingSeenBySlot.Clear();
        _reuseTrackedSlotNames.Clear();
        _nextLocalSlot = 0;
        _localNames.Clear();
        _localTypes.Clear();

        return new CapturedStringTaskSavedState(
            savedInst,
            savedTemp,
            savedLocal,
            savedScopes,
            savedOwnershipScopes,
            savedArenaWatermarks,
            savedTcoCtx,
            savedLocalNames,
            savedLocalTypes,
            savedReuseTokenFieldBindings,
            savedReuseBindingSeen,
            savedReuseTrackedSlotNames);
    }

    private int LowerCapturedStringTaskBuildCoroutine(
        IReadOnlyList<int> captureTemps,
        Func<IReadOnlyList<int>, int> emitBody,
        string coroutineLabel)
    {
        var coroutineCaptureTemps = new int[captureTemps.Count];
        for (int i = 0; i < captureTemps.Count; i++)
        {
            coroutineCaptureTemps[i] = NewTemp();
            Emit(new IrInst.LoadEnv(coroutineCaptureTemps[i], i));
        }

        int bodyTemp = emitBody(coroutineCaptureTemps);
        Emit(new IrInst.Return(bodyTemp));

        var transformResult = StateMachineTransform.Transform(_inst, captureTemps.Count);
        var coroutineFunc = new IrFunction(
            Label: coroutineLabel,
            Instructions: new List<IrInst>(transformResult.Instructions),
            LocalCount: _nextLocalSlot,
            TempCount: Math.Max(_nextTempSlot, transformResult.MaxTemp + 1),
            HasEnvAndArgParams: true,
            Coroutine: new CoroutineInfo(
                StateCount: transformResult.StateCount,
                StateStructSize: transformResult.StateStructSize,
                CaptureCount: captureTemps.Count
            ),
            LocalNames: new Dictionary<int, string>(_localNames),
            LocalTypes: SnapshotLocalTypes()
        );
        _funcs.Add(coroutineFunc);
        return transformResult.StateStructSize;
    }

    private void LowerCapturedStringTaskRestoreState(CapturedStringTaskSavedState saved)
    {
        _inst.Clear();
        _inst.AddRange(saved.Instructions);
        _nextTempSlot = saved.NextTempSlot;
        _reuseTokenFieldBindings.Clear();
        foreach (var kv in saved.ReuseTokenFieldBindings) _reuseTokenFieldBindings[kv.Key] = kv.Value;
        _reuseBindingSeenBySlot.Clear();
        foreach (var kv in saved.ReuseBindingSeenBySlot) _reuseBindingSeenBySlot[kv.Key] = kv.Value;
        _reuseTrackedSlotNames.Clear();
        foreach (var kv in saved.ReuseTrackedSlotNames) _reuseTrackedSlotNames[kv.Key] = kv.Value;
        _nextLocalSlot = saved.NextLocalSlot;
        _localNames.Clear();
        _localTypes.Clear();
        foreach (var kv in saved.LocalNames) _localNames[kv.Key] = kv.Value;
        foreach (var kv in saved.LocalTypes) _localTypes[kv.Key] = kv.Value;
        _scopes.Clear();
        foreach (var scope in saved.Scopes.Reverse())
        {
            _scopes.Push(new Dictionary<string, Binding>(scope, StringComparer.Ordinal));
        }
        _ownershipScopes.Clear();
        foreach (var scope in saved.OwnershipScopes.Reverse())
        {
            _ownershipScopes.Push(new Dictionary<string, OwnershipInfo>(scope, StringComparer.Ordinal));
        }
        _arenaWatermarks.Clear();
        foreach (var watermark in saved.ArenaWatermarks.Reverse())
        {
            _arenaWatermarks.Push(watermark);
        }
        _tcoCtx = saved.TcoCtx;
    }

    private static bool IsAsyncOnlyNetworkingBuiltin(BuiltinRegistry.BuiltinValueKind kind)
    {
        return kind is BuiltinRegistry.BuiltinValueKind.HttpGet
            or BuiltinRegistry.BuiltinValueKind.HttpPost
            or BuiltinRegistry.BuiltinValueKind.NetTcpConnect
            or BuiltinRegistry.BuiltinValueKind.NetTcpSend
            or BuiltinRegistry.BuiltinValueKind.NetTcpReceive
            or BuiltinRegistry.BuiltinValueKind.NetTcpClose
            or BuiltinRegistry.BuiltinValueKind.NetTcpListen
            or BuiltinRegistry.BuiltinValueKind.NetTcpAccept
            or BuiltinRegistry.BuiltinValueKind.NetTcpForkWorkers
            or BuiltinRegistry.BuiltinValueKind.NetTlsConnect
            or BuiltinRegistry.BuiltinValueKind.NetTlsSend
            or BuiltinRegistry.BuiltinValueKind.NetTlsReceive
            or BuiltinRegistry.BuiltinValueKind.NetTlsClose;
    }

    private static bool IsAsyncOnlyNetworkingIntrinsic(IntrinsicKind kind)
    {
        return kind is IntrinsicKind.HttpGet
            or IntrinsicKind.HttpPost
            or IntrinsicKind.NetTcpConnect
            or IntrinsicKind.NetTcpSend
            or IntrinsicKind.NetTcpReceive
            or IntrinsicKind.NetTcpClose
            or IntrinsicKind.NetTcpListen
            or IntrinsicKind.NetTcpAccept
            or IntrinsicKind.NetForkWorkers
            or IntrinsicKind.NetTlsConnect
            or IntrinsicKind.NetTlsSend
            or IntrinsicKind.NetTlsReceive
            or IntrinsicKind.NetTlsClose;
    }

    private static int GetIntrinsicArity(IntrinsicKind kind) => kind switch
    {
        IntrinsicKind.NetTlsServerHandshake => 3,
        IntrinsicKind.ParallelBoth => 2,
        IntrinsicKind.ParallelWithWorkers => 2,
        IntrinsicKind.FileWriteText => 2,
        IntrinsicKind.FileWriteBytes => 2,
        IntrinsicKind.BytesGet => 2,
        IntrinsicKind.BytesIndexOf => 3,
        IntrinsicKind.BytesCompare => 2,
        IntrinsicKind.BytesScanHash => 3,
        IntrinsicKind.BytesSubText => 3,
        IntrinsicKind.BytesSubView => 3,
        IntrinsicKind.BytesAppend => 2,
        IntrinsicKind.BytesAppendByte => 2,
        IntrinsicKind.TextFormatFloat => 2,
        IntrinsicKind.BigIntFromInt => 1,
        IntrinsicKind.BigIntToString => 1,
        IntrinsicKind.BigIntToInt => 1,
        IntrinsicKind.BigIntFromString => 1,
        IntrinsicKind.BigIntAdd => 2,
        IntrinsicKind.BigIntSub => 2,
        IntrinsicKind.BigIntMul => 2,
        IntrinsicKind.BigIntDiv => 2,
        IntrinsicKind.BigIntMod => 2,
        IntrinsicKind.BigIntCompare => 2,
        IntrinsicKind.BytesGetU16Le => 2,
        IntrinsicKind.BytesGetU32Le => 2,
        IntrinsicKind.BytesGetU64Le => 2,
        IntrinsicKind.HttpPost => 2,
        IntrinsicKind.NetTcpConnect => 2,
        IntrinsicKind.NetTcpSend => 2,
        IntrinsicKind.NetTcpReceive => 2,
        IntrinsicKind.NetTlsConnect => 2,
        IntrinsicKind.NetTlsSend => 2,
        IntrinsicKind.NetTlsReceive => 2,
        IntrinsicKind.SpawnProcess => 2,
        IntrinsicKind.ProcessWriteStdin => 2,
        _ => 1
    };

    private bool TryRequireBuiltinNamedType(TypeRef type, string builtinTypeName, Expr origin, string diagnosticMessage)
    {
        var prunedType = Prune(type);
        if (prunedType is TypeRef.TVar)
        {
            Unify(prunedType, _resolvedTypes[builtinTypeName]);
            return true;
        }

        if (prunedType is TypeRef.TNamedType named && string.Equals(named.Symbol.Name, builtinTypeName, StringComparison.Ordinal))
        {
            return true;
        }

        ReportDiagnostic(GetSpan(origin), $"{diagnosticMessage} Got {Pretty(prunedType)}.");
        return false;
    }

    private bool TryRequireSocketType(TypeRef type, Expr origin, string diagnosticMessage)
        => TryRequireBuiltinNamedType(type, "Socket", origin, diagnosticMessage);

    private bool TryRequireTlsSocketType(TypeRef type, Expr origin, string diagnosticMessage)
        => TryRequireBuiltinNamedType(type, "TlsSocket", origin, diagnosticMessage);

    private (int, TypeRef) LowerNetTcpConnect(Expr hostArg, Expr portArg)
    {
        RequireBuiltinCapability(NetConnectCapabilityName, GetSpan(hostArg));
        using var hostSpan = PushDiagnosticSpan(hostArg);
        var (hostTemp, hostType) = LowerExpr(hostArg);
        var prunedHostType = Prune(hostType);
        if (prunedHostType is TypeRef.TNever)
        {
            return (hostTemp, prunedHostType);
        }

        if (prunedHostType is TypeRef.TVar)
        {
            Unify(prunedHostType, new TypeRef.TStr());
            prunedHostType = new TypeRef.TStr();
        }

        if (prunedHostType is not TypeRef.TStr)
        {
            ReportDiagnostic(GetSpan(hostArg), $"Ashes.Net.Tcp.connect() expects Str for host but got {Pretty(prunedHostType)}.");
            return (hostTemp, prunedHostType);
        }

        using var portSpan = PushDiagnosticSpan(portArg);
        var (portTemp, portType) = LowerExpr(portArg);
        var prunedPortType = Prune(portType);
        if (prunedPortType is TypeRef.TNever)
        {
            return (portTemp, prunedPortType);
        }

        if (prunedPortType is TypeRef.TVar)
        {
            Unify(prunedPortType, new TypeRef.TInt());
            prunedPortType = new TypeRef.TInt();
        }

        if (prunedPortType is not TypeRef.TInt)
        {
            ReportDiagnostic(GetSpan(portArg), $"Ashes.Net.Tcp.connect() expects Int for port but got {Pretty(prunedPortType)}.");
            return (portTemp, prunedPortType);
        }

        var taskTemp = NewTemp();
        Emit(new IrInst.CreateTcpConnectTask(taskTemp, hostTemp, portTemp));
        return (taskTemp, CreateStringTaskType(_resolvedTypes["Socket"]));
    }

    private (int, TypeRef) LowerNetTcpListen(Expr portArg)
    {
        RequireBuiltinCapability(NetListenCapabilityName, GetSpan(portArg));
        using var portSpan = PushDiagnosticSpan(portArg);
        var (portTemp, portType) = LowerExpr(portArg);
        var prunedPortType = Prune(portType);
        if (prunedPortType is TypeRef.TNever)
        {
            return (portTemp, prunedPortType);
        }

        if (prunedPortType is TypeRef.TVar)
        {
            Unify(prunedPortType, new TypeRef.TInt());
            prunedPortType = new TypeRef.TInt();
        }

        if (prunedPortType is not TypeRef.TInt)
        {
            ReportDiagnostic(GetSpan(portArg), $"Ashes.Net.Tcp.Server.listen() expects Int for port but got {Pretty(prunedPortType)}.");
            return (portTemp, prunedPortType);
        }

        var taskTemp = NewTemp();
        Emit(new IrInst.CreateTcpListenTask(taskTemp, portTemp));
        return (taskTemp, CreateStringTaskType(_resolvedTypes["Socket"]));
    }

    private (int, TypeRef) LowerNetForkWorkers(Expr portArg, Expr countArg)
    {
        RequireBuiltinCapability(NetListenCapabilityName, GetSpan(portArg));
        using var portSpan = PushDiagnosticSpan(portArg);
        var (portTemp, portType) = LowerExpr(portArg);
        var prunedPortType = Prune(portType);
        if (prunedPortType is TypeRef.TNever)
        {
            return (portTemp, prunedPortType);
        }

        if (prunedPortType is TypeRef.TVar)
        {
            Unify(prunedPortType, new TypeRef.TInt());
            prunedPortType = new TypeRef.TInt();
        }

        if (prunedPortType is not TypeRef.TInt)
        {
            ReportDiagnostic(GetSpan(portArg), $"Ashes.Net.Tcp.Server.forkWorkers() expects Int for port but got {Pretty(prunedPortType)}.");
            return (portTemp, prunedPortType);
        }

        using var countSpan = PushDiagnosticSpan(countArg);
        var (countTemp, countType) = LowerExpr(countArg);
        var prunedCountType = Prune(countType);
        if (prunedCountType is TypeRef.TNever)
        {
            return (countTemp, prunedCountType);
        }

        if (prunedCountType is TypeRef.TVar)
        {
            Unify(prunedCountType, new TypeRef.TInt());
            prunedCountType = new TypeRef.TInt();
        }

        if (prunedCountType is not TypeRef.TInt)
        {
            ReportDiagnostic(GetSpan(countArg), $"Ashes.Net.Tcp.Server.forkWorkers() expects Int for worker count but got {Pretty(prunedCountType)}.");
            return (countTemp, prunedCountType);
        }

        var taskTemp = NewTemp();
        Emit(new IrInst.CreateForkWorkersTask(taskTemp, portTemp, countTemp));
        return (taskTemp, CreateStringTaskType(new TypeRef.TInt()));
    }

    private (int, TypeRef) LowerNetSetDrainTimeout(Expr msArg)
    {
        using var msSpan = PushDiagnosticSpan(msArg);
        var (msTemp, msType) = LowerExpr(msArg);
        var prunedMsType = Prune(msType);
        if (prunedMsType is TypeRef.TNever)
        {
            return (msTemp, prunedMsType);
        }

        if (prunedMsType is TypeRef.TVar)
        {
            Unify(prunedMsType, new TypeRef.TInt());
            prunedMsType = new TypeRef.TInt();
        }

        if (prunedMsType is not TypeRef.TInt)
        {
            ReportDiagnostic(GetSpan(msArg), $"Ashes.Net.Tcp.Server.setDrainTimeout() expects Int (milliseconds) but got {Pretty(prunedMsType)}.");
            return (msTemp, prunedMsType);
        }

        var unitTemp = NewTemp();
        Emit(new IrInst.SetDrainTimeout(unitTemp, msTemp));
        return (unitTemp, _resolvedTypes["Unit"]);
    }

    private (int, TypeRef) LowerNetTcpAccept(Expr socketArg)
    {
        using var socketSpan = PushDiagnosticSpan(socketArg);
        CheckUseAfterDrop(socketArg);
        var (socketTemp, socketType) = LowerExpr(socketArg);
        var prunedSocketType = Prune(socketType);
        if (prunedSocketType is TypeRef.TNever)
        {
            return (socketTemp, prunedSocketType);
        }

        if (!TryRequireSocketType(prunedSocketType, socketArg, "Ashes.Net.Tcp.Server.accept() expects Socket."))
        {
            return (socketTemp, prunedSocketType);
        }

        var taskTemp = NewTemp();
        Emit(new IrInst.CreateTcpAcceptTask(taskTemp, socketTemp));
        return (taskTemp, CreateStringTaskType(_resolvedTypes["Socket"]));
    }

    private (int, TypeRef) LowerNetTlsConnect(Expr hostArg, Expr portArg)
    {
        RequireBuiltinCapability(NetConnectCapabilityName, GetSpan(hostArg));
        using var hostSpan = PushDiagnosticSpan(hostArg);
        var (hostTemp, hostType) = LowerExpr(hostArg);
        var prunedHostType = Prune(hostType);
        if (prunedHostType is TypeRef.TNever)
        {
            return (hostTemp, prunedHostType);
        }

        if (prunedHostType is TypeRef.TVar)
        {
            Unify(prunedHostType, new TypeRef.TStr());
            prunedHostType = new TypeRef.TStr();
        }

        if (prunedHostType is not TypeRef.TStr)
        {
            ReportDiagnostic(GetSpan(hostArg), $"Ashes.Net.Tls.connect() expects Str for host but got {Pretty(prunedHostType)}.");
            return (hostTemp, prunedHostType);
        }

        using var portSpan = PushDiagnosticSpan(portArg);
        var (portTemp, portType) = LowerExpr(portArg);
        var prunedPortType = Prune(portType);
        if (prunedPortType is TypeRef.TNever)
        {
            return (portTemp, prunedPortType);
        }

        if (prunedPortType is TypeRef.TVar)
        {
            Unify(prunedPortType, new TypeRef.TInt());
            prunedPortType = new TypeRef.TInt();
        }

        if (prunedPortType is not TypeRef.TInt)
        {
            ReportDiagnostic(GetSpan(portArg), $"Ashes.Net.Tls.connect() expects Int for port but got {Pretty(prunedPortType)}.");
            return (portTemp, prunedPortType);
        }

        var taskTemp = NewTemp();
        Emit(new IrInst.CreateTlsConnectTask(taskTemp, hostTemp, portTemp));
        return (taskTemp, CreateStringTaskType(_resolvedTypes["TlsSocket"]));
    }

    private (int, TypeRef) LowerHttpGet(Expr urlArg)
    {
        RequireBuiltinCapability(NetConnectCapabilityName, GetSpan(urlArg));
        using var urlSpan = PushDiagnosticSpan(urlArg);
        var (urlTemp, urlType) = LowerExpr(urlArg);
        var prunedUrlType = Prune(urlType);
        if (prunedUrlType is TypeRef.TNever)
        {
            return (urlTemp, prunedUrlType);
        }

        if (prunedUrlType is TypeRef.TVar)
        {
            Unify(prunedUrlType, new TypeRef.TStr());
            prunedUrlType = new TypeRef.TStr();
        }

        if (prunedUrlType is not TypeRef.TStr)
        {
            ReportDiagnostic(GetSpan(urlArg), $"Ashes.Net.Http.get() expects Str for url but got {Pretty(prunedUrlType)}.");
            return (urlTemp, prunedUrlType);
        }

        var taskTemp = NewTemp();
        Emit(new IrInst.CreateHttpGetTask(taskTemp, urlTemp));
        return (taskTemp, CreateStringTaskType(new TypeRef.TStr()));
    }

    private (int, TypeRef) LowerHttpPost(Expr urlArg, Expr bodyArg)
    {
        RequireBuiltinCapability(NetConnectCapabilityName, GetSpan(urlArg));
        using var urlSpan = PushDiagnosticSpan(urlArg);
        var (urlTemp, urlType) = LowerExpr(urlArg);
        var prunedUrlType = Prune(urlType);
        if (prunedUrlType is TypeRef.TNever)
        {
            return (urlTemp, prunedUrlType);
        }

        if (prunedUrlType is TypeRef.TVar)
        {
            Unify(prunedUrlType, new TypeRef.TStr());
            prunedUrlType = new TypeRef.TStr();
        }

        if (prunedUrlType is not TypeRef.TStr)
        {
            ReportDiagnostic(GetSpan(urlArg), $"Ashes.Net.Http.post() expects Str for url but got {Pretty(prunedUrlType)}.");
            return (urlTemp, prunedUrlType);
        }

        using var bodySpan = PushDiagnosticSpan(bodyArg);
        var (bodyTemp, bodyType) = LowerExpr(bodyArg);
        var prunedBodyType = Prune(bodyType);
        if (prunedBodyType is TypeRef.TNever)
        {
            return (bodyTemp, prunedBodyType);
        }

        if (prunedBodyType is TypeRef.TVar)
        {
            Unify(prunedBodyType, new TypeRef.TStr());
            prunedBodyType = new TypeRef.TStr();
        }

        if (prunedBodyType is not TypeRef.TStr)
        {
            ReportDiagnostic(GetSpan(bodyArg), $"Ashes.Net.Http.post() expects Str for body but got {Pretty(prunedBodyType)}.");
            return (bodyTemp, prunedBodyType);
        }

        var taskTemp = NewTemp();
        Emit(new IrInst.CreateHttpPostTask(taskTemp, urlTemp, bodyTemp));
        return (taskTemp, CreateStringTaskType(new TypeRef.TStr()));
    }

    private (int, TypeRef) LowerNetTcpSend(Expr socketArg, Expr textArg)
    {
        using var socketSpan = PushDiagnosticSpan(socketArg);
        CheckUseAfterDrop(socketArg);
        var (socketTemp, socketType) = LowerExpr(socketArg);
        var prunedSocketType = Prune(socketType);
        if (prunedSocketType is TypeRef.TNever)
        {
            return (socketTemp, prunedSocketType);
        }

        if (!TryRequireSocketType(prunedSocketType, socketArg, "Ashes.Net.Tcp.send() expects Socket."))
        {
            return (socketTemp, prunedSocketType);
        }

        using var textSpan = PushDiagnosticSpan(textArg);
        var (textTemp, textType) = LowerExpr(textArg);
        var prunedTextType = Prune(textType);
        if (prunedTextType is TypeRef.TNever)
        {
            return (textTemp, prunedTextType);
        }

        if (prunedTextType is TypeRef.TVar)
        {
            Unify(prunedTextType, new TypeRef.TStr());
            prunedTextType = new TypeRef.TStr();
        }

        if (prunedTextType is not TypeRef.TStr)
        {
            ReportDiagnostic(GetSpan(textArg), $"Ashes.Net.Tcp.send() expects Str for text but got {Pretty(prunedTextType)}.");
            return (textTemp, prunedTextType);
        }

        var taskTemp = NewTemp();
        Emit(new IrInst.CreateTcpSendTask(taskTemp, socketTemp, textTemp));
        return (taskTemp, CreateStringTaskType(new TypeRef.TInt()));
    }

    private (int, TypeRef) LowerNetTlsSend(Expr socketArg, Expr textArg)
    {
        using var socketSpan = PushDiagnosticSpan(socketArg);
        CheckUseAfterDrop(socketArg);
        var (socketTemp, socketType) = LowerExpr(socketArg);
        var prunedSocketType = Prune(socketType);
        if (prunedSocketType is TypeRef.TNever)
        {
            return (socketTemp, prunedSocketType);
        }

        if (!TryRequireTlsSocketType(prunedSocketType, socketArg, "Ashes.Net.Tls.send() expects TlsSocket."))
        {
            return (socketTemp, prunedSocketType);
        }

        using var textSpan = PushDiagnosticSpan(textArg);
        var (textTemp, textType) = LowerExpr(textArg);
        var prunedTextType = Prune(textType);
        if (prunedTextType is TypeRef.TNever)
        {
            return (textTemp, prunedTextType);
        }

        if (prunedTextType is TypeRef.TVar)
        {
            Unify(prunedTextType, new TypeRef.TStr());
            prunedTextType = new TypeRef.TStr();
        }

        if (prunedTextType is not TypeRef.TStr)
        {
            ReportDiagnostic(GetSpan(textArg), $"Ashes.Net.Tls.send() expects Str for text but got {Pretty(prunedTextType)}.");
            return (textTemp, prunedTextType);
        }

        var taskTemp = NewTemp();
        Emit(new IrInst.CreateTlsSendTask(taskTemp, socketTemp, textTemp));
        return (taskTemp, CreateStringTaskType(new TypeRef.TInt()));
    }

    private (int, TypeRef) LowerNetTcpReceive(Expr socketArg, Expr maxBytesArg)
    {
        using var socketSpan = PushDiagnosticSpan(socketArg);
        CheckUseAfterDrop(socketArg);
        var (socketTemp, socketType) = LowerExpr(socketArg);
        var prunedSocketType = Prune(socketType);
        if (prunedSocketType is TypeRef.TNever)
        {
            return (socketTemp, prunedSocketType);
        }

        if (!TryRequireSocketType(prunedSocketType, socketArg, "Ashes.Net.Tcp.receive() expects Socket."))
        {
            return (socketTemp, prunedSocketType);
        }

        using var maxBytesSpan = PushDiagnosticSpan(maxBytesArg);
        var (maxBytesTemp, maxBytesType) = LowerExpr(maxBytesArg);
        var prunedMaxBytesType = Prune(maxBytesType);
        if (prunedMaxBytesType is TypeRef.TNever)
        {
            return (maxBytesTemp, prunedMaxBytesType);
        }

        if (prunedMaxBytesType is TypeRef.TVar)
        {
            Unify(prunedMaxBytesType, new TypeRef.TInt());
            prunedMaxBytesType = new TypeRef.TInt();
        }

        if (prunedMaxBytesType is not TypeRef.TInt)
        {
            ReportDiagnostic(GetSpan(maxBytesArg), $"Ashes.Net.Tcp.receive() expects Int for maxBytes but got {Pretty(prunedMaxBytesType)}.");
            return (maxBytesTemp, prunedMaxBytesType);
        }

        var taskTemp = NewTemp();
        Emit(new IrInst.CreateTcpReceiveTask(taskTemp, socketTemp, maxBytesTemp));
        return (taskTemp, CreateStringTaskType(new TypeRef.TStr()));
    }

    private (int, TypeRef) LowerNetTlsReceive(Expr socketArg, Expr maxBytesArg)
    {
        using var socketSpan = PushDiagnosticSpan(socketArg);
        CheckUseAfterDrop(socketArg);
        var (socketTemp, socketType) = LowerExpr(socketArg);
        var prunedSocketType = Prune(socketType);
        if (prunedSocketType is TypeRef.TNever)
        {
            return (socketTemp, prunedSocketType);
        }

        if (!TryRequireTlsSocketType(prunedSocketType, socketArg, "Ashes.Net.Tls.receive() expects TlsSocket."))
        {
            return (socketTemp, prunedSocketType);
        }

        using var maxBytesSpan = PushDiagnosticSpan(maxBytesArg);
        var (maxBytesTemp, maxBytesType) = LowerExpr(maxBytesArg);
        var prunedMaxBytesType = Prune(maxBytesType);
        if (prunedMaxBytesType is TypeRef.TNever)
        {
            return (maxBytesTemp, prunedMaxBytesType);
        }

        if (prunedMaxBytesType is TypeRef.TVar)
        {
            Unify(prunedMaxBytesType, new TypeRef.TInt());
            prunedMaxBytesType = new TypeRef.TInt();
        }

        if (prunedMaxBytesType is not TypeRef.TInt)
        {
            ReportDiagnostic(GetSpan(maxBytesArg), $"Ashes.Net.Tls.receive() expects Int for maxBytes but got {Pretty(prunedMaxBytesType)}.");
            return (maxBytesTemp, prunedMaxBytesType);
        }

        var taskTemp = NewTemp();
        Emit(new IrInst.CreateTlsReceiveTask(taskTemp, socketTemp, maxBytesTemp));
        return (taskTemp, CreateStringTaskType(new TypeRef.TStr()));
    }

    private (int, TypeRef) LowerNetTcpClose(Expr socketArg)
    {
        using var socketSpan = PushDiagnosticSpan(socketArg);

        // Check for double-drop before lowering the argument
        if (socketArg is Expr.Var v)
        {
            var info = LookupOwnedValue(v.Name);
            if (info is not null && info.IsDropped)
            {
                ReportDiagnostic(GetSpan(socketArg),
                    $"Resource '{v.Name}' has already been closed. Closing a resource twice is not allowed.",
                    DiagnosticCodes.DoubleDrop);
            }
        }

        var (socketTemp, socketType) = LowerExpr(socketArg);
        var prunedSocketType = Prune(socketType);
        if (prunedSocketType is TypeRef.TNever)
        {
            return (socketTemp, prunedSocketType);
        }

        if (!TryRequireSocketType(prunedSocketType, socketArg, "Ashes.Net.Tcp.close() expects Socket."))
        {
            return (socketTemp, prunedSocketType);
        }

        // Mark the resource as dropped (explicitly closed)
        if (socketArg is Expr.Var varExpr)
        {
            TryMarkDropped(varExpr.Name);
        }

        var taskTemp = NewTemp();
        Emit(new IrInst.CreateTcpCloseTask(taskTemp, socketTemp));
        return (taskTemp, CreateStringTaskType(_resolvedTypes["Unit"]));
    }

    private (int, TypeRef) LowerNetTlsClose(Expr socketArg)
    {
        using var socketSpan = PushDiagnosticSpan(socketArg);

        if (socketArg is Expr.Var v)
        {
            var info = LookupOwnedValue(v.Name);
            if (info is not null && info.IsDropped)
            {
                ReportDiagnostic(GetSpan(socketArg),
                    $"Resource '{v.Name}' has already been closed. Closing a resource twice is not allowed.",
                    DiagnosticCodes.DoubleDrop);
            }
        }

        var (socketTemp, socketType) = LowerExpr(socketArg);
        var prunedSocketType = Prune(socketType);
        if (prunedSocketType is TypeRef.TNever)
        {
            return (socketTemp, prunedSocketType);
        }

        if (!TryRequireTlsSocketType(prunedSocketType, socketArg, "Ashes.Net.Tls.close() expects TlsSocket."))
        {
            return (socketTemp, prunedSocketType);
        }

        if (socketArg is Expr.Var varExpr)
        {
            TryMarkDropped(varExpr.Name);
        }

        var taskTemp = NewTemp();
        Emit(new IrInst.CreateTlsCloseTask(taskTemp, socketTemp));
        return (taskTemp, CreateStringTaskType(_resolvedTypes["Unit"]));
    }

    /// <summary>
    /// Ashes.Task.run(task) — synchronous execution.
    /// Drives the task's coroutine to completion using RunTask
    /// and returns the resulting Result(E, A).
    /// </summary>
    private (int, TypeRef) LowerAsyncRun(Expr taskArg)
    {
        using var diagnosticSpan = PushDiagnosticSpan(taskArg);

        var (taskTemp, taskType) = LowerExpr(taskArg);

        // Verify the argument is a Task(E, A)
        if (!_typeSymbols.TryGetValue("Task", out var taskSymbol)
            || !_typeSymbols.TryGetValue("Result", out var resultSymbol))
        {
            ReportDiagnostic(GetSpan(taskArg), "Internal error: Task or Result type not registered.");
            return ReturnNeverWithDummyTemp();
        }

        var errorType = NewTypeVar();
        var successType = NewTypeVar();
        var expectedTaskType = new TypeRef.TNamedType(taskSymbol, [errorType, successType]);
        Unify(taskType, expectedTaskType);

        // RunTask synchronously drives the coroutine to completion
        int bodyTemp = NewTemp();
        Emit(new IrInst.RunTask(bodyTemp, taskTemp));

        var resultType = new TypeRef.TNamedType(resultSymbol, [Prune(errorType), Prune(successType)]);
        return (bodyTemp, resultType);
    }

    /// <summary>
    /// Ashes.Task.task(value) — creates a pre-completed successful task.
    /// Convenience form of creating a successful task with error type Str.
    /// </summary>
    private (int, TypeRef) LowerAsyncTask(Expr valueArg)
    {
        using var diagnosticSpan = PushDiagnosticSpan(valueArg);
        _usesAsync = true;

        // An `async(E)` whose body contains an `await` becomes a genuine suspending coroutine (built
        // through StateMachineTransform), so the await is a suspension point rather than an inline
        // blocking run. `Ashes.Task.run` still drives it to completion blockingly, so behavior is
        // identical — but the task is now a live state machine instead of an eager completed value.
        // An await-free body has no suspension point, so it stays the eager pre-completed task.
        if (ExprContainsAwait(valueArg))
        {
            return LowerAsyncTaskCoroutine(valueArg);
        }

        var (valueTemp, valueType) = LowerExpr(valueArg);

        if (!TryGetStandardResultParts(out var resultSymbol, out var okConstructor, out _)
            || !_typeSymbols.TryGetValue("Task", out var taskSymbol))
        {
            return ReturnNeverWithDummyTemp();
        }

        int okResultTemp = LowerSingleFieldConstructorValue(okConstructor, valueTemp);
        int taskTemp = NewTemp();
        Emit(new IrInst.CreateCompletedTask(taskTemp, okResultTemp));
        return (taskTemp, new TypeRef.TNamedType(taskSymbol, [new TypeRef.TStr(), Prune(valueType)]));
    }

    /// <summary>
    /// Lowers <c>async(E)</c> (where <c>E</c> awaits) into a suspending coroutine task. The body's free
    /// variables are captured; the body is emitted into a coroutine (with awaits as suspension points)
    /// via <see cref="LowerCapturedStringTask"/>, and the coroutine returns <c>Ok(E)</c>.
    /// Semantics-preserving: <c>Ashes.Task.run</c> drives the state machine to completion.
    /// </summary>
    private (int, TypeRef) LowerAsyncTaskCoroutine(Expr valueArg)
    {
        if (!TryGetStandardResultParts(out _, out var okConstructor, out _))
        {
            return ReturnNeverWithDummyTemp();
        }

        // Capture the free variables that resolve to a value binding (locals / captured env). Globals,
        // constructors and builtins resolve inside the coroutine without capture (registry / by-label).
        var freeNames = FreeVars(valueArg, new HashSet<string>(StringComparer.Ordinal));
        var captureNames = new List<string>();
        var captureTemps = new List<int>();
        var captureTypes = new List<TypeRef>();
        foreach (var name in freeNames)
        {
            if (Lookup(name) is Binding.Local or Binding.Env or Binding.EnvScheme or Binding.Scheme)
            {
                var (capTemp, capType) = LowerVar(new Expr.Var(name));
                captureNames.Add(name);
                captureTemps.Add(capTemp);
                captureTypes.Add(Prune(capType));
            }
        }

        // Scope-independent bindings (intrinsics like `async`, external functions, prelude values) must be
        // re-seeded into the coroutine's fresh function scope so the body still resolves them; slot-based
        // locals/env can't cross the function boundary and are captured above instead. Bottom-up so an
        // inner scope's binding wins.
        var globalBindings = new Dictionary<string, Binding>(StringComparer.Ordinal);
        foreach (var enclosingScope in _scopes.Reverse())
        {
            foreach (var (bindingName, binding) in enclosingScope)
            {
                if (binding is Binding.Intrinsic or Binding.ExternalFunction or Binding.PreludeValue)
                {
                    globalBindings[bindingName] = binding;
                }
            }
        }

        var successType = NewTypeVar();

        return LowerCapturedStringTask(
            captureTemps,
            successType,
            valueArg,
            coroutineCaptureTemps => LowerAsyncTaskCoroutineEmitBody(
                coroutineCaptureTemps, valueArg, successType, okConstructor, globalBindings, captureNames, captureTypes));
    }

    private int LowerAsyncTaskCoroutineEmitBody(
        IReadOnlyList<int> coroutineCaptureTemps,
        Expr valueArg,
        TypeRef successType,
        ConstructorSymbol okConstructor,
        Dictionary<string, Binding> globalBindings,
        List<string> captureNames,
        List<TypeRef> captureTypes)
    {
        bool savedInCoroutine = _inCoroutineBody;
        _inCoroutineBody = true;

        var scope = _scopes.Peek();
        foreach (var (bindingName, binding) in globalBindings)
        {
            scope[bindingName] = binding;
        }

        for (int i = 0; i < captureNames.Count; i++)
        {
            int slot = NewLocal();
            Emit(new IrInst.StoreLocal(slot, coroutineCaptureTemps[i]));
            RecordLocalDebugInfo(slot, captureNames[i], captureTypes[i]);
            scope[captureNames[i]] = new Binding.Local(slot, captureTypes[i]);
        }

        var (valueTemp, valueType) = LowerExpr(valueArg);
        Unify(valueType, successType);
        int okTemp = LowerSingleFieldConstructorValue(okConstructor, valueTemp);

        _inCoroutineBody = savedInCoroutine;
        return okTemp;
    }

    /// <summary>
    /// Whether an expression references <c>Ashes.Task.spawn</c> anywhere (including inside nested
    /// lambdas, which run synchronously during a loop iteration). Used as the static safety gate for
    /// the async-loop arena reset: a spawned task's captures may point at allocations made during the
    /// current iteration, which the back-edge reset would free under the detached task. Var references
    /// resolve through the scope (so a selector-import alias of spawn is caught); qualified references
    /// are matched by their member name, which can only over-reject.
    /// </summary>
    private bool ContainsAsyncSpawn(Expr expr)
    {
        switch (expr)
        {
            case Expr.Var v:
                return string.Equals(v.Name, "spawn", StringComparison.Ordinal)
                    || Lookup(v.Name) is Binding.Intrinsic { Kind: IntrinsicKind.AsyncSpawn };
            case Expr.QualifiedVar qv:
                return string.Equals(qv.Name, "spawn", StringComparison.Ordinal);
            case Expr.Add x: return ContainsAsyncSpawn(x.Left) || ContainsAsyncSpawn(x.Right);
            case Expr.Subtract x: return ContainsAsyncSpawn(x.Left) || ContainsAsyncSpawn(x.Right);
            case Expr.Multiply x: return ContainsAsyncSpawn(x.Left) || ContainsAsyncSpawn(x.Right);
            case Expr.Divide x: return ContainsAsyncSpawn(x.Left) || ContainsAsyncSpawn(x.Right);
            case Expr.Modulo x: return ContainsAsyncSpawn(x.Left) || ContainsAsyncSpawn(x.Right);
            case Expr.BitwiseAnd x: return ContainsAsyncSpawn(x.Left) || ContainsAsyncSpawn(x.Right);
            case Expr.BitwiseOr x: return ContainsAsyncSpawn(x.Left) || ContainsAsyncSpawn(x.Right);
            case Expr.BitwiseXor x: return ContainsAsyncSpawn(x.Left) || ContainsAsyncSpawn(x.Right);
            case Expr.ShiftLeft x: return ContainsAsyncSpawn(x.Left) || ContainsAsyncSpawn(x.Right);
            case Expr.ShiftRight x: return ContainsAsyncSpawn(x.Left) || ContainsAsyncSpawn(x.Right);
            case Expr.GreaterThan x: return ContainsAsyncSpawn(x.Left) || ContainsAsyncSpawn(x.Right);
            case Expr.LessThan x: return ContainsAsyncSpawn(x.Left) || ContainsAsyncSpawn(x.Right);
            case Expr.GreaterOrEqual x: return ContainsAsyncSpawn(x.Left) || ContainsAsyncSpawn(x.Right);
            case Expr.LessOrEqual x: return ContainsAsyncSpawn(x.Left) || ContainsAsyncSpawn(x.Right);
            case Expr.Equal x: return ContainsAsyncSpawn(x.Left) || ContainsAsyncSpawn(x.Right);
            case Expr.NotEqual x: return ContainsAsyncSpawn(x.Left) || ContainsAsyncSpawn(x.Right);
            case Expr.ResultPipe x: return ContainsAsyncSpawn(x.Left) || ContainsAsyncSpawn(x.Right);
            case Expr.ResultMapErrorPipe x: return ContainsAsyncSpawn(x.Left) || ContainsAsyncSpawn(x.Right);
            default:
                return ContainsAsyncSpawnStructural(expr);
        }
    }

    // The structural (non-leaf, non-binary-operator) cases of ContainsAsyncSpawn.
    private bool ContainsAsyncSpawnStructural(Expr expr)
    {
        switch (expr)
        {
            case Expr.Cons x: return ContainsAsyncSpawn(x.Head) || ContainsAsyncSpawn(x.Tail);
            case Expr.BitwiseNot x: return ContainsAsyncSpawn(x.Operand);
            case Expr.Await x: return ContainsAsyncSpawn(x.Task);
            case Expr.Call x: return ContainsAsyncSpawn(x.Func) || ContainsAsyncSpawn(x.Arg);
            case Expr.If x: return ContainsAsyncSpawn(x.Cond) || ContainsAsyncSpawn(x.Then) || ContainsAsyncSpawn(x.Else);
            case Expr.Lambda x: return ContainsAsyncSpawn(x.Body);
            case Expr.Let x: return ContainsAsyncSpawn(x.Value) || ContainsAsyncSpawn(x.Body);
            case Expr.LetRecursive x: return ContainsAsyncSpawn(x.Value) || ContainsAsyncSpawn(x.Body);
            case Expr.LetResult x: return ContainsAsyncSpawn(x.Value) || ContainsAsyncSpawn(x.Body);
            case Expr.TupleLit x: return x.Elements.Any(ContainsAsyncSpawn);
            case Expr.ListLit x: return x.Elements.Any(ContainsAsyncSpawn);
            case Expr.RecordLit x: return x.Fields.Any(f => ContainsAsyncSpawn(f.Value));
            case Expr.RecordUpdate x: return ContainsAsyncSpawn(x.Target) || x.Updates.Any(u => ContainsAsyncSpawn(u.Value));
            case Expr.Perform x: return ContainsAsyncSpawn(x.Operation);
            case Expr.Handle x: return ContainsAsyncSpawn(x.Body) || x.Arms.Any(a => ContainsAsyncSpawn(a.Body));
            case Expr.Match x:
                if (ContainsAsyncSpawn(x.Value))
                {
                    return true;
                }

                foreach (var c in x.Cases)
                {
                    if (ContainsAsyncSpawn(c.Body) || (c.Guard is not null && ContainsAsyncSpawn(c.Guard)))
                    {
                        return true;
                    }
                }

                return false;
            default:
                return false;
        }
    }

    /// <summary>
    /// Lowers the innermost body of an async tail-recursive helper (a <c>let recursive</c> defined
    /// inside a coroutine body whose own body awaits) as a *transparent* coroutine task:
    /// - the coroutine's result slot holds the body's RAW value (no Ok-wrap), so a call site that
    ///   awaits the task implicitly sees the helper body's own type;
    /// - a restart label is emitted at the body start and a TCO context targets it, so saturated
    ///   self tail calls store the new arguments into the parameter locals and jump back — the loop
    ///   stays inside ONE coroutine (no per-iteration task, no waiter chain);
    /// - awaits in the body are ordinary suspend points on the enclosing scheduler run.
    /// The helper's parameters reach the coroutine as ordinary captures (they are locals of the
    /// enclosing lambda frame), so the existing capture machinery carries them; the loop-aware locals
    /// liveness in <see cref="StateMachineTransform"/> keeps them valid across suspends.
    /// </summary>
    private (int, TypeRef) LowerHelperCoroutineTask(HelperCoroutineInfo info)
    {
        var body = info.Body;
        var freeNames = FreeVars(body, new HashSet<string>(StringComparer.Ordinal));
        var captureNames = new List<string>();
        var captureTemps = new List<int>();
        var captureTypes = new List<TypeRef>();
        foreach (var name in freeNames)
        {
            if (Lookup(name) is Binding.Local or Binding.Env or Binding.EnvScheme or Binding.Scheme)
            {
                var (capTemp, capType) = LowerVar(new Expr.Var(name));
                captureNames.Add(name);
                captureTemps.Add(capTemp);
                captureTypes.Add(Prune(capType));
            }
        }

        var globalBindings = new Dictionary<string, Binding>(StringComparer.Ordinal);
        foreach (var enclosingScope in _scopes.Reverse())
        {
            foreach (var (bindingName, binding) in enclosingScope)
            {
                if (binding is Binding.Intrinsic or Binding.ExternalFunction or Binding.PreludeValue)
                {
                    globalBindings[bindingName] = binding;
                }
            }
        }

        var successType = NewTypeVar();

        // Static safety gate for the per-iteration arena reset: no spawn in the loop body (a detached
        // task's captures could point at iteration allocations the reset would free). The remaining
        // hazards are covered elsewhere: escaping via the loop-carried arguments is blocked by the
        // copy-out type gate at the back-edge, and a composite ancestor sharing the arena clears the
        // task's LoopResetOk flag at suspend time (see the scheduler's suspend path).
        bool loopResetEligible = !ContainsAsyncSpawn(body);

        return LowerCapturedStringTask(
            captureTemps,
            successType,
            body,
            coroutineCaptureTemps => LowerHelperCoroutineTaskEmitLoopBody(
                coroutineCaptureTemps, info, globalBindings, captureNames, captureTypes, successType, loopResetEligible),
            loopResetEligible);
    }

    private int LowerHelperCoroutineTaskEmitLoopBody(
        IReadOnlyList<int> coroutineCaptureTemps,
        HelperCoroutineInfo info,
        Dictionary<string, Binding> globalBindings,
        List<string> captureNames,
        List<TypeRef> captureTypes,
        TypeRef successType,
        bool loopResetEligible)
    {
        bool savedInCoroutine = _inCoroutineBody;
        _inCoroutineBody = true;

        var orderedParamSlots = LowerHelperCoroutineTaskBindLoopCaptures(
            coroutineCaptureTemps, info, globalBindings, captureNames, captureTypes);

        string restartLabel = $"__async_loop_{_nextAsyncLoopId++}";
        Emit(new IrInst.Label(restartLabel));

        var savedTco = _tcoCtx;
        var loopTco = LowerHelperCoroutineTaskCreateLoopTco(info, orderedParamSlots, restartLabel, loopResetEligible);

        _tcoCtx = loopTco;

        var (valueTemp, valueType) = LowerExpr(info.Body);
        Unify(valueType, successType);

        _tcoCtx = savedTco;
        _inCoroutineBody = savedInCoroutine;
        return valueTemp; // raw body value — no Ok-wrap (transparent to the awaiting call site)
    }

    private List<int> LowerHelperCoroutineTaskBindLoopCaptures(
        IReadOnlyList<int> coroutineCaptureTemps,
        HelperCoroutineInfo info,
        Dictionary<string, Binding> globalBindings,
        List<string> captureNames,
        List<TypeRef> captureTypes)
    {
        var scope = _scopes.Peek();
        foreach (var (bindingName, binding) in globalBindings)
        {
            scope[bindingName] = binding;
        }

        var paramSlotByName = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < captureNames.Count; i++)
        {
            int slot = NewLocal();
            Emit(new IrInst.StoreLocal(slot, coroutineCaptureTemps[i]));
            RecordLocalDebugInfo(slot, captureNames[i], captureTypes[i]);
            scope[captureNames[i]] = new Binding.Local(slot, captureTypes[i]);
            if (info.ParamNames.Contains(captureNames[i]))
            {
                paramSlotByName[captureNames[i]] = slot;
            }
        }

        // A parameter the body never reads is not captured; the back-edge still stores its new
        // value by arity, so give it a scratch slot.
        var orderedParamSlots = new List<int>(info.ParamNames.Count);
        foreach (var paramName in info.ParamNames)
        {
            orderedParamSlots.Add(paramSlotByName.TryGetValue(paramName, out int s) ? s : NewLocal());
        }

        return orderedParamSlots;
    }

    private TcoContext LowerHelperCoroutineTaskCreateLoopTco(
        HelperCoroutineInfo info,
        List<int> orderedParamSlots,
        string restartLabel,
        bool loopResetEligible)
    {
        var loopTco = new TcoContext
        {
            SelfName = info.Name,
            ParamCount = info.ParamNames.Count,
            ParamNames = new List<string>(info.ParamNames),
            ParamSlots = orderedParamSlots,
            BodyLabel = restartLabel,
            InTailPosition = true,
            DescendingChain = false
        };
        if (loopResetEligible)
        {
            // Per-iteration watermark, re-saved on every pass over the restart label. The slots
            // are ordinary locals, so the loop-aware liveness in StateMachineTransform carries
            // them across suspends. The back-edge emits the flagged restore/reclaim (gated by
            // the backend); heap-typed loop-carried args go through the standard copy-out.
            loopTco.ArenaCursorSlot = NewLocal();
            loopTco.ArenaEndSlot = NewLocal();
            loopTco.CoroutineLoopReset = true;
            Emit(new IrInst.SaveArenaState(loopTco.ArenaCursorSlot, loopTco.ArenaEndSlot) { CoroutineLoop = true });
        }

        return loopTco;
    }

    /// <summary>
    /// Whether an expression syntactically contains an <c>await</c> anywhere within it. Complete over
    /// the expression forms; a missed case would only fall back to the eager (still-correct) task path.
    /// </summary>
    private static bool ExprContainsAwait(Expr expr)
    {
        switch (expr)
        {
            case Expr.Await:
                return true;
            case Expr.Add x: return ExprContainsAwait(x.Left) || ExprContainsAwait(x.Right);
            case Expr.Subtract x: return ExprContainsAwait(x.Left) || ExprContainsAwait(x.Right);
            case Expr.Multiply x: return ExprContainsAwait(x.Left) || ExprContainsAwait(x.Right);
            case Expr.Divide x: return ExprContainsAwait(x.Left) || ExprContainsAwait(x.Right);
            case Expr.Modulo x: return ExprContainsAwait(x.Left) || ExprContainsAwait(x.Right);
            case Expr.BitwiseAnd x: return ExprContainsAwait(x.Left) || ExprContainsAwait(x.Right);
            case Expr.BitwiseOr x: return ExprContainsAwait(x.Left) || ExprContainsAwait(x.Right);
            case Expr.BitwiseXor x: return ExprContainsAwait(x.Left) || ExprContainsAwait(x.Right);
            case Expr.ShiftLeft x: return ExprContainsAwait(x.Left) || ExprContainsAwait(x.Right);
            case Expr.ShiftRight x: return ExprContainsAwait(x.Left) || ExprContainsAwait(x.Right);
            case Expr.GreaterThan x: return ExprContainsAwait(x.Left) || ExprContainsAwait(x.Right);
            case Expr.LessThan x: return ExprContainsAwait(x.Left) || ExprContainsAwait(x.Right);
            case Expr.GreaterOrEqual x: return ExprContainsAwait(x.Left) || ExprContainsAwait(x.Right);
            case Expr.LessOrEqual x: return ExprContainsAwait(x.Left) || ExprContainsAwait(x.Right);
            case Expr.Equal x: return ExprContainsAwait(x.Left) || ExprContainsAwait(x.Right);
            case Expr.NotEqual x: return ExprContainsAwait(x.Left) || ExprContainsAwait(x.Right);
            case Expr.ResultPipe x: return ExprContainsAwait(x.Left) || ExprContainsAwait(x.Right);
            case Expr.ResultMapErrorPipe x: return ExprContainsAwait(x.Left) || ExprContainsAwait(x.Right);
            case Expr.Cons x: return ExprContainsAwait(x.Head) || ExprContainsAwait(x.Tail);
            case Expr.BitwiseNot x: return ExprContainsAwait(x.Operand);
            case Expr.Call x: return ExprContainsAwait(x.Func) || ExprContainsAwait(x.Arg);
            case Expr.If x: return ExprContainsAwait(x.Cond) || ExprContainsAwait(x.Then) || ExprContainsAwait(x.Else);
            case Expr.Lambda x: return ExprContainsAwait(x.Body);
            case Expr.Let x: return ExprContainsAwait(x.Value) || ExprContainsAwait(x.Body);
            case Expr.LetRecursive x: return ExprContainsAwait(x.Value) || ExprContainsAwait(x.Body);
            case Expr.LetResult x: return ExprContainsAwait(x.Value) || ExprContainsAwait(x.Body);
            case Expr.TupleLit x: return x.Elements.Any(ExprContainsAwait);
            case Expr.ListLit x: return x.Elements.Any(ExprContainsAwait);
            case Expr.RecordLit x: return x.Fields.Any(f => ExprContainsAwait(f.Value));
            case Expr.RecordUpdate x: return ExprContainsAwait(x.Target) || x.Updates.Any(u => ExprContainsAwait(u.Value));
            case Expr.Match x:
                if (ExprContainsAwait(x.Value))
                {
                    return true;
                }

                foreach (var c in x.Cases)
                {
                    if (ExprContainsAwait(c.Body) || (c.Guard is not null && ExprContainsAwait(c.Guard)))
                    {
                        return true;
                    }
                }

                return false;
            default:
                return false;
        }
    }

    /// <summary>
    /// Like <see cref="ExprContainsAwait"/> but does not descend into nested lambda bodies: used to
    /// decide whether a let-recursive helper inside a coroutine body needs the async-loop lowering
    /// (only awaits in the helper's own coroutine scope matter).
    /// </summary>
    private static bool ContainsAwaitOutsideNestedLambda(Expr expr)
    {
        switch (expr)
        {
            case Expr.Await:
                return true;
            case Expr.Add x: return ContainsAwaitOutsideNestedLambda(x.Left) || ContainsAwaitOutsideNestedLambda(x.Right);
            case Expr.Subtract x: return ContainsAwaitOutsideNestedLambda(x.Left) || ContainsAwaitOutsideNestedLambda(x.Right);
            case Expr.Multiply x: return ContainsAwaitOutsideNestedLambda(x.Left) || ContainsAwaitOutsideNestedLambda(x.Right);
            case Expr.Divide x: return ContainsAwaitOutsideNestedLambda(x.Left) || ContainsAwaitOutsideNestedLambda(x.Right);
            case Expr.Modulo x: return ContainsAwaitOutsideNestedLambda(x.Left) || ContainsAwaitOutsideNestedLambda(x.Right);
            case Expr.BitwiseAnd x: return ContainsAwaitOutsideNestedLambda(x.Left) || ContainsAwaitOutsideNestedLambda(x.Right);
            case Expr.BitwiseOr x: return ContainsAwaitOutsideNestedLambda(x.Left) || ContainsAwaitOutsideNestedLambda(x.Right);
            case Expr.BitwiseXor x: return ContainsAwaitOutsideNestedLambda(x.Left) || ContainsAwaitOutsideNestedLambda(x.Right);
            case Expr.ShiftLeft x: return ContainsAwaitOutsideNestedLambda(x.Left) || ContainsAwaitOutsideNestedLambda(x.Right);
            case Expr.ShiftRight x: return ContainsAwaitOutsideNestedLambda(x.Left) || ContainsAwaitOutsideNestedLambda(x.Right);
            case Expr.GreaterThan x: return ContainsAwaitOutsideNestedLambda(x.Left) || ContainsAwaitOutsideNestedLambda(x.Right);
            case Expr.LessThan x: return ContainsAwaitOutsideNestedLambda(x.Left) || ContainsAwaitOutsideNestedLambda(x.Right);
            case Expr.GreaterOrEqual x: return ContainsAwaitOutsideNestedLambda(x.Left) || ContainsAwaitOutsideNestedLambda(x.Right);
            case Expr.LessOrEqual x: return ContainsAwaitOutsideNestedLambda(x.Left) || ContainsAwaitOutsideNestedLambda(x.Right);
            case Expr.Equal x: return ContainsAwaitOutsideNestedLambda(x.Left) || ContainsAwaitOutsideNestedLambda(x.Right);
            case Expr.NotEqual x: return ContainsAwaitOutsideNestedLambda(x.Left) || ContainsAwaitOutsideNestedLambda(x.Right);
            case Expr.ResultPipe x: return ContainsAwaitOutsideNestedLambda(x.Left) || ContainsAwaitOutsideNestedLambda(x.Right);
            case Expr.ResultMapErrorPipe x: return ContainsAwaitOutsideNestedLambda(x.Left) || ContainsAwaitOutsideNestedLambda(x.Right);
            case Expr.Cons x: return ContainsAwaitOutsideNestedLambda(x.Head) || ContainsAwaitOutsideNestedLambda(x.Tail);
            case Expr.BitwiseNot x: return ContainsAwaitOutsideNestedLambda(x.Operand);
            case Expr.Call x: return ContainsAwaitOutsideNestedLambda(x.Func) || ContainsAwaitOutsideNestedLambda(x.Arg);
            case Expr.If x: return ContainsAwaitOutsideNestedLambda(x.Cond) || ContainsAwaitOutsideNestedLambda(x.Then) || ContainsAwaitOutsideNestedLambda(x.Else);
            // A nested lambda is its own function; its awaits lower to blocking runs regardless and
            // do not make the ENCLOSING helper's body a suspending loop.
            case Expr.Lambda: return false;
            case Expr.Let x: return ContainsAwaitOutsideNestedLambda(x.Value) || ContainsAwaitOutsideNestedLambda(x.Body);
            case Expr.LetRecursive x: return ContainsAwaitOutsideNestedLambda(x.Value) || ContainsAwaitOutsideNestedLambda(x.Body);
            case Expr.LetResult x: return ContainsAwaitOutsideNestedLambda(x.Value) || ContainsAwaitOutsideNestedLambda(x.Body);
            case Expr.TupleLit x: return x.Elements.Any(ContainsAwaitOutsideNestedLambda);
            case Expr.ListLit x: return x.Elements.Any(ContainsAwaitOutsideNestedLambda);
            case Expr.RecordLit x: return x.Fields.Any(f => ContainsAwaitOutsideNestedLambda(f.Value));
            case Expr.RecordUpdate x: return ContainsAwaitOutsideNestedLambda(x.Target) || x.Updates.Any(u => ContainsAwaitOutsideNestedLambda(u.Value));
            case Expr.Match x:
                if (ContainsAwaitOutsideNestedLambda(x.Value))
                {
                    return true;
                }

                foreach (var c in x.Cases)
                {
                    if (ContainsAwaitOutsideNestedLambda(c.Body) || (c.Guard is not null && ContainsAwaitOutsideNestedLambda(c.Guard)))
                    {
                        return true;
                    }
                }

                return false;
            default:
                return false;
        }
    }

    /// <summary>
    /// Whether an expression is a direct call to the <c>async</c> intrinsic — a helper whose whole
    /// body is already an async block needs no async-loop treatment (it returns a task eagerly).
    /// </summary>
    private bool IsAsyncIntrinsicCall(Expr expr) =>
        expr is Expr.Call { Func: Expr.Var av } && Lookup(av.Name) is Binding.Intrinsic { Kind: IntrinsicKind.AsyncTask };

    /// <summary>
    /// Ashes.Task.fromResult(result) — creates a pre-completed task.
    /// Wraps a Result(E, A) into a Task(E, A) that is already completed.
    /// </summary>
    private (int, TypeRef) LowerAsyncFromResult(Expr resultArg)
    {
        using var diagnosticSpan = PushDiagnosticSpan(resultArg);

        var (resultTemp, resultType) = LowerExpr(resultArg);

        if (!TryGetStandardResultParts(out var resultSymbol, out _, out _)
            || !_typeSymbols.TryGetValue("Task", out var taskSymbol))
        {
            return ReturnNeverWithDummyTemp();
        }

        var errorType = NewTypeVar();
        var successType = NewTypeVar();
        var expectedResultType = new TypeRef.TNamedType(resultSymbol, [errorType, successType]);
        Unify(resultType, expectedResultType);

        int finalTemp = NewTemp();
        Emit(new IrInst.CreateCompletedTask(finalTemp, resultTemp));
        return (finalTemp, new TypeRef.TNamedType(taskSymbol, [Prune(errorType), Prune(successType)]));
    }

    /// <summary>
    /// Ashes.Task.sleep(ms) — creates a sleep task.
    /// Returns Task(Str, Int) — a task that completes after the given milliseconds
    /// and returns 0 (Unit placeholder).
    /// </summary>
    private (int, TypeRef) LowerAsyncSleep(Expr msArg)
    {
        using var diagnosticSpan = PushDiagnosticSpan(msArg);
        _usesAsync = true;

        var (msTemp, msType) = LowerExpr(msArg);
        Unify(msType, new TypeRef.TInt());

        if (!_typeSymbols.TryGetValue("Task", out var taskSymbol))
        {
            ReportDiagnostic(GetSpan(msArg), "Internal error: Task type not registered.");
            return ReturnNeverWithDummyTemp();
        }

        // AsyncSleep creates a pre-configured sleep task
        int taskTemp = NewTemp();
        Emit(new IrInst.AsyncSleep(taskTemp, msTemp));

        // Return type: Task(Str, Int) — sleep returns 0 on completion
        var strType = new TypeRef.TStr();
        var intType = new TypeRef.TInt();
        return (taskTemp, new TypeRef.TNamedType(taskSymbol, [strType, intType]));
    }

    /// <summary>
    /// Ashes.Task.all(tasks) — runs all tasks and collects results.
    /// Returns Task(E, List(A)) — a task containing a list of all results.
    /// </summary>
    private (int, TypeRef) LowerAsyncAll(Expr taskListArg)
    {
        using var diagnosticSpan = PushDiagnosticSpan(taskListArg);
        _usesAsync = true;

        var (listTemp, listType) = LowerExpr(taskListArg);

        if (!_typeSymbols.TryGetValue("Task", out var taskSymbol))
        {
            ReportDiagnostic(GetSpan(taskListArg), "Internal error: Task type not registered.");
            return ReturnNeverWithDummyTemp();
        }

        // Unify input type: List(Task(E, A))
        var errorType = NewTypeVar();
        var successType = NewTypeVar();
        var innerTaskType = new TypeRef.TNamedType(taskSymbol, [errorType, successType]);
        var expectedListType = new TypeRef.TList(innerTaskType);
        Unify(listType, expectedListType);

        // Emit AsyncAll IR instruction
        int taskTemp = NewTemp();
        Emit(new IrInst.AsyncAll(taskTemp, listTemp));

        // Return type: Task(E, List(A))
        var resultListType = new TypeRef.TList(Prune(successType));
        return (taskTemp, new TypeRef.TNamedType(taskSymbol, [Prune(errorType), resultListType]));
    }

    /// <summary>
    /// Ashes.Task.race(tasks) — runs the first task to completion.
    /// Returns Task(E, A) — a task with the first task's result.
    /// </summary>
    private (int, TypeRef) LowerAsyncRace(Expr taskListArg)
    {
        using var diagnosticSpan = PushDiagnosticSpan(taskListArg);
        _usesAsync = true;

        var (listTemp, listType) = LowerExpr(taskListArg);

        if (!_typeSymbols.TryGetValue("Task", out var taskSymbol))
        {
            ReportDiagnostic(GetSpan(taskListArg), "Internal error: Task type not registered.");
            return ReturnNeverWithDummyTemp();
        }

        // Unify input type: List(Task(E, A))
        var errorType = NewTypeVar();
        var successType = NewTypeVar();
        var innerTaskType = new TypeRef.TNamedType(taskSymbol, [errorType, successType]);
        var expectedListType = new TypeRef.TList(innerTaskType);
        Unify(listType, expectedListType);

        // Emit AsyncRace IR instruction
        int taskTemp = NewTemp();
        Emit(new IrInst.AsyncRace(taskTemp, listTemp));

        // Return type: Task(E, A)
        return (taskTemp, new TypeRef.TNamedType(taskSymbol, [Prune(errorType), Prune(successType)]));
    }

    private (int, TypeRef) LowerPanic(Expr arg)
    {
        using var diagnosticSpan = PushDiagnosticSpan(arg);
        var (msgTemp, msgType) = LowerExpr(arg);
        Unify(msgType, new TypeRef.TStr());
        Emit(new IrInst.PanicStr(msgTemp));
        return (msgTemp, new TypeRef.TNever());
    }

    private void AddStdIOBindings(Dictionary<string, Binding> scope)
    {
        scope["print"] = CreatePrintBinding();
        scope["panic"] = CreatePanicBinding();
        scope["args"] = CreateArgsBinding();
    }

    private Binding.Intrinsic CreatePrintBinding()
    {
        var printArgTypeVar = (TypeRef.TVar)NewTypeVar();
        return new Binding.Intrinsic(
            IntrinsicKind.Print,
            new TypeScheme([new TypeVar(printArgTypeVar.Id, "a")], new TypeRef.TFun(printArgTypeVar, _resolvedTypes["Unit"]))
        );
    }

    private Binding.Intrinsic CreateWriteBinding()
    {
        return new Binding.Intrinsic(
            IntrinsicKind.Write,
            new TypeScheme([], new TypeRef.TFun(new TypeRef.TStr(), _resolvedTypes["Unit"]))
        );
    }

    private Binding.Intrinsic CreateWriteBytesBinding()
    {
        return new Binding.Intrinsic(
            IntrinsicKind.WriteBytes,
            new TypeScheme([], new TypeRef.TFun(new TypeRef.TBytes(), _resolvedTypes["Unit"]))
        );
    }

    private Binding.Intrinsic CreateWriteLineBinding()
    {
        return new Binding.Intrinsic(
            IntrinsicKind.WriteLine,
            new TypeScheme([], new TypeRef.TFun(new TypeRef.TStr(), _resolvedTypes["Unit"]))
        );
    }

    private Binding.Intrinsic CreateReadLineBinding()
    {
        return new Binding.Intrinsic(
            IntrinsicKind.ReadLine,
            new TypeScheme([], new TypeRef.TFun(_resolvedTypes["Unit"], CreateMaybeType(new TypeRef.TStr())))
        );
    }

    private Binding.Intrinsic CreateTextUnconsBinding()
    {
        return new Binding.Intrinsic(
            IntrinsicKind.TextUncons,
            new TypeScheme([], new TypeRef.TFun(new TypeRef.TStr(), CreateMaybeType(new TypeRef.TTuple([new TypeRef.TStr(), new TypeRef.TStr()]))))
        );
    }

    private Binding.Intrinsic CreateTextParseIntBinding()
    {
        return new Binding.Intrinsic(
            IntrinsicKind.TextParseInt,
            new TypeScheme([], new TypeRef.TFun(new TypeRef.TStr(), CreateStringResultType(new TypeRef.TInt())))
        );
    }

    private Binding.Intrinsic CreateRegexCompileBinding()
    {
        return new Binding.Intrinsic(
            IntrinsicKind.RegexCompile,
            new TypeScheme([], new TypeRef.TFun(new TypeRef.TStr(), new TypeRef.TInt()))
        );
    }

    private Binding.Intrinsic CreateRegexCompileErrorBinding()
    {
        return new Binding.Intrinsic(
            IntrinsicKind.RegexCompileError,
            new TypeScheme([], new TypeRef.TFun(new TypeRef.TStr(), new TypeRef.TStr()))
        );
    }

    private Binding.Intrinsic CreateRegexFindBinding()
    {
        return new Binding.Intrinsic(
            IntrinsicKind.RegexFind,
            new TypeScheme([], new TypeRef.TFun(new TypeRef.TInt(), new TypeRef.TFun(new TypeRef.TStr(), new TypeRef.TFun(new TypeRef.TInt(), CreateMaybeType(new TypeRef.TTuple([new TypeRef.TInt(), new TypeRef.TInt()]))))))
        );
    }

    private Binding.Intrinsic CreateRegexCapturesBinding()
    {
        return new Binding.Intrinsic(
            IntrinsicKind.RegexCaptures,
            new TypeScheme([], new TypeRef.TFun(new TypeRef.TInt(), new TypeRef.TFun(new TypeRef.TStr(), new TypeRef.TFun(new TypeRef.TInt(), CreateMaybeType(new TypeRef.TList(CreateMaybeType(new TypeRef.TStr())))))))
        );
    }

    private Binding.Intrinsic CreateRegexSubstituteBinding()
    {
        return new Binding.Intrinsic(
            IntrinsicKind.RegexSubstitute,
            new TypeScheme([], new TypeRef.TFun(new TypeRef.TInt(), new TypeRef.TFun(new TypeRef.TStr(), new TypeRef.TFun(new TypeRef.TStr(), new TypeRef.TStr()))))
        );
    }

    private Binding.Intrinsic CreateTextParseFloatBinding()
    {
        return new Binding.Intrinsic(
            IntrinsicKind.TextParseFloat,
            new TypeScheme([], new TypeRef.TFun(new TypeRef.TStr(), CreateStringResultType(new TypeRef.TFloat())))
        );
    }

    private Binding.Intrinsic CreateTextFromIntBinding()
    {
        return new Binding.Intrinsic(
            IntrinsicKind.TextFromInt,
            new TypeScheme([], new TypeRef.TFun(new TypeRef.TInt(), new TypeRef.TStr()))
        );
    }

    private Binding.Intrinsic CreateTextFromFloatBinding()
    {
        return new Binding.Intrinsic(
            IntrinsicKind.TextFromFloat,
            new TypeScheme([], new TypeRef.TFun(new TypeRef.TFloat(), new TypeRef.TStr()))
        );
    }

    private Binding.Intrinsic CreateTextFormatFloatBinding()
    {
        return new Binding.Intrinsic(
            IntrinsicKind.TextFormatFloat,
            new TypeScheme([], new TypeRef.TFun(new TypeRef.TFloat(), new TypeRef.TFun(new TypeRef.TInt(), new TypeRef.TStr())))
        );
    }

    // Ashes.Number.BigInt intrinsics
    private Binding.Intrinsic CreateBigIntFromIntBinding() =>
        new(IntrinsicKind.BigIntFromInt, new TypeScheme([], new TypeRef.TFun(new TypeRef.TInt(), new TypeRef.TBigInt())));

    private Binding.Intrinsic CreateBigIntToStringBinding() =>
        new(IntrinsicKind.BigIntToString, new TypeScheme([], new TypeRef.TFun(new TypeRef.TBigInt(), new TypeRef.TStr())));

    private Binding.Intrinsic CreateBigIntToIntBinding() =>
        new(IntrinsicKind.BigIntToInt, new TypeScheme([], new TypeRef.TFun(new TypeRef.TBigInt(), CreateStringResultType(new TypeRef.TInt()))));

    private Binding.Intrinsic CreateBigIntFromStringBinding() =>
        new(IntrinsicKind.BigIntFromString, new TypeScheme([], new TypeRef.TFun(new TypeRef.TStr(), CreateStringResultType(new TypeRef.TBigInt()))));

    private (int, TypeRef) LowerBigIntToInt(Expr arg)
    {
        using var span = PushDiagnosticSpan(arg);
        var (valueTemp, valueType) = LowerExpr(arg);
        var pruned = Prune(valueType);
        if (pruned is TypeRef.TNever)
        {
            return (valueTemp, pruned);
        }
        if (pruned is TypeRef.TVar)
        {
            Unify(pruned, new TypeRef.TBigInt());
        }
        else if (pruned is not TypeRef.TBigInt)
        {
            ReportDiagnostic(GetSpan(arg), $"Ashes.Number.BigInt.toInt() expects BigInt but got {Pretty(pruned)}.");
            return (valueTemp, pruned);
        }
        var target = NewTemp();
        Emit(new IrInst.BigIntToInt(target, valueTemp));
        return (target, CreateStringResultType(new TypeRef.TInt()));
    }

    private (int, TypeRef) LowerBigIntFromString(Expr arg)
    {
        using var span = PushDiagnosticSpan(arg);
        var (valueTemp, valueType) = LowerExpr(arg);
        var pruned = Prune(valueType);
        if (pruned is TypeRef.TNever)
        {
            return (valueTemp, pruned);
        }
        if (pruned is TypeRef.TVar)
        {
            Unify(pruned, new TypeRef.TStr());
        }
        else if (pruned is not TypeRef.TStr)
        {
            ReportDiagnostic(GetSpan(arg), $"Ashes.Text.parseBigInt() expects Str but got {Pretty(pruned)}.");
            return (valueTemp, pruned);
        }
        var target = NewTemp();
        Emit(new IrInst.BigIntFromString(target, valueTemp));
        return (target, CreateStringResultType(new TypeRef.TBigInt()));
    }

    private Binding.Intrinsic CreateBigIntBinaryBinding(IntrinsicKind kind) =>
        new(kind, new TypeScheme([], new TypeRef.TFun(new TypeRef.TBigInt(), new TypeRef.TFun(new TypeRef.TBigInt(), new TypeRef.TBigInt()))));

    private Binding.Intrinsic CreateBigIntCompareBinding() =>
        new(IntrinsicKind.BigIntCompare, new TypeScheme([], new TypeRef.TFun(new TypeRef.TBigInt(), new TypeRef.TFun(new TypeRef.TBigInt(), new TypeRef.TInt()))));

    // A `<digits>N` BigInt literal. Fits-in-i64 values go straight through fromInt; larger ones are
    // built with chunked Horner in base 10^18 (each 18-digit chunk fits an i64), reusing the BigInt
    // mul/add ops. The literal digits are validated by the lexer, so no error path is needed here.
    private (int, TypeRef) LowerBigIntLit(Expr.BigIntLit lit)
    {
        string digits = lit.Digits;
        if (long.TryParse(digits, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long small))
        {
            return (EmitBigIntFromLongConst(small), new TypeRef.TBigInt());
        }

        const int chunkDigits = 18;
        const long chunkBase = 1_000_000_000_000_000_000L; // 10^18
        int firstLen = digits.Length % chunkDigits;
        if (firstLen == 0)
        {
            firstLen = chunkDigits;
        }
        int accTemp = EmitBigIntFromLongConst(long.Parse(digits[..firstLen], System.Globalization.CultureInfo.InvariantCulture));
        for (int pos = firstLen; pos < digits.Length; pos += chunkDigits)
        {
            int baseTemp = EmitBigIntFromLongConst(chunkBase);
            int mulTemp = NewTemp();
            Emit(new IrInst.BigIntBinary(mulTemp, accTemp, baseTemp, "mul"));
            int chunkTemp = EmitBigIntFromLongConst(long.Parse(digits.Substring(pos, chunkDigits), System.Globalization.CultureInfo.InvariantCulture));
            int addTemp = NewTemp();
            Emit(new IrInst.BigIntBinary(addTemp, mulTemp, chunkTemp, "add"));
            accTemp = addTemp;
        }
        return (accTemp, new TypeRef.TBigInt());
    }

    private int EmitBigIntFromLongConst(long value)
    {
        int constTemp = NewTemp();
        Emit(new IrInst.LoadConstInt(constTemp, value));
        int target = NewTemp();
        Emit(new IrInst.BigIntFromInt(target, constTemp));
        return target;
    }

    private (int, TypeRef) LowerBigIntFromInt(Expr arg)
    {
        using var span = PushDiagnosticSpan(arg);
        var (valueTemp, valueType) = LowerExpr(arg);
        var pruned = Prune(valueType);
        if (pruned is TypeRef.TNever)
        {
            return (valueTemp, pruned);
        }
        if (pruned is TypeRef.TVar)
        {
            Unify(pruned, new TypeRef.TInt());
        }
        else if (pruned is not TypeRef.TInt)
        {
            ReportDiagnostic(GetSpan(arg), $"Ashes.Number.BigInt.fromInt() expects Int but got {Pretty(pruned)}.");
            return (valueTemp, pruned);
        }
        var target = NewTemp();
        Emit(new IrInst.BigIntFromInt(target, valueTemp));
        return (target, new TypeRef.TBigInt());
    }

    private (int, TypeRef) LowerBigIntToString(Expr arg)
    {
        using var span = PushDiagnosticSpan(arg);
        var (valueTemp, valueType) = LowerExpr(arg);
        var pruned = Prune(valueType);
        if (pruned is TypeRef.TNever)
        {
            return (valueTemp, pruned);
        }
        if (pruned is TypeRef.TVar)
        {
            Unify(pruned, new TypeRef.TBigInt());
        }
        else if (pruned is not TypeRef.TBigInt)
        {
            ReportDiagnostic(GetSpan(arg), $"Ashes.Number.BigInt.toString() expects BigInt but got {Pretty(pruned)}.");
            return (valueTemp, pruned);
        }
        var target = NewTemp();
        Emit(new IrInst.BigIntToString(target, valueTemp));
        return (target, new TypeRef.TStr());
    }

    private (int, TypeRef) LowerBigIntBinary(Expr leftArg, Expr rightArg, string op, string display, bool resultIsInt)
    {
        var (leftTemp, leftType) = LowerBigIntOperand(leftArg, display);
        if (Prune(leftType) is TypeRef.TNever)
        {
            return (leftTemp, Prune(leftType));
        }
        var (rightTemp, rightType) = LowerBigIntOperand(rightArg, display);
        if (Prune(rightType) is TypeRef.TNever)
        {
            return (rightTemp, Prune(rightType));
        }
        var target = NewTemp();
        if (resultIsInt)
        {
            Emit(new IrInst.BigIntCompare(target, leftTemp, rightTemp));
            return (target, new TypeRef.TInt());
        }
        Emit(new IrInst.BigIntBinary(target, leftTemp, rightTemp, op));
        return (target, new TypeRef.TBigInt());
    }

    private (int, TypeRef) LowerBigIntOperand(Expr arg, string display)
    {
        using var span = PushDiagnosticSpan(arg);
        var (temp, type) = LowerExpr(arg);
        var pruned = Prune(type);
        if (pruned is TypeRef.TNever)
        {
            return (temp, pruned);
        }
        if (pruned is TypeRef.TVar)
        {
            Unify(pruned, new TypeRef.TBigInt());
        }
        else if (pruned is not TypeRef.TBigInt)
        {
            ReportDiagnostic(GetSpan(arg), $"{display} expects BigInt but got {Pretty(pruned)}.");
        }
        return (temp, new TypeRef.TBigInt());
    }

    private Binding.Intrinsic CreateTextToHexBinding()
    {
        return new Binding.Intrinsic(
            IntrinsicKind.TextToHex,
            new TypeScheme([], new TypeRef.TFun(new TypeRef.TInt(), new TypeRef.TStr()))
        );
    }

    private Binding.Intrinsic CreateTextAsciiCaseBinding(bool upper)
    {
        return new Binding.Intrinsic(
            upper ? IntrinsicKind.TextAsciiUpper : IntrinsicKind.TextAsciiLower,
            new TypeScheme([], new TypeRef.TFun(new TypeRef.TStr(), new TypeRef.TStr()))
        );
    }

    private TypeRef CreateMaybeType(TypeRef innerType)
    {
        if (!_typeSymbols.TryGetValue("Maybe", out var maybeSymbol) || maybeSymbol.TypeParameters.Count != 1)
        {
            throw new InvalidOperationException("Built-in Maybe type is not registered.");
        }

        return new TypeRef.TNamedType(maybeSymbol, [innerType]);
    }

    private Binding.Intrinsic CreateFileReadTextBinding()
    {
        return new Binding.Intrinsic(
            IntrinsicKind.FileReadText,
            new TypeScheme([], new TypeRef.TFun(new TypeRef.TStr(), CreateStringResultType(new TypeRef.TStr())))
        );
    }

    // Ashes.IO.File.readAllBytes(path) : Result(Str, Bytes) — read a whole file into a Bytes with no UTF-8
    // validation. On Linux the read is uncapped (sized by the file); on Windows it currently shares the
    // fixed readText buffer and so caps at the same limit. Enables random-access / chunked processing
    // (e.g. a data-parallel fold that splits the input at record boundaries).
    private Binding.Intrinsic CreateFileReadAllBytesBinding()
    {
        return new Binding.Intrinsic(
            IntrinsicKind.FileReadAllBytes,
            new TypeScheme([], new TypeRef.TFun(new TypeRef.TStr(), CreateStringResultType(new TypeRef.TBytes())))
        );
    }

    // Ashes.IO.File.mmap(path) : Result(Str, Bytes) — memory-map the whole file read-only and return a
    // zero-copy Bytes view over it (no read/copy). The mapping is program-lifetime, so fields sliced out
    // of it stay valid; on a data-parallel fold each worker faults in its own chunk's pages, so the I/O
    // is parallelized for free. On Windows this currently falls back to the capped readAllBytes read.
    private Binding.Intrinsic CreateFileMmapBinding()
    {
        return new Binding.Intrinsic(
            IntrinsicKind.FileMmap,
            new TypeScheme([], new TypeRef.TFun(new TypeRef.TStr(), CreateStringResultType(new TypeRef.TBytes())))
        );
    }

    private (int, TypeRef) LowerFileMmap(Expr pathArg)
    {
        using var diagnosticSpan = PushDiagnosticSpan(pathArg);
        var (pathTemp, pathType) = LowerExpr(pathArg);
        var loweredType = Prune(pathType);

        if (loweredType is TypeRef.TNever)
        {
            return (pathTemp, loweredType);
        }

        if (loweredType is TypeRef.TVar)
        {
            Unify(loweredType, new TypeRef.TStr());
            loweredType = new TypeRef.TStr();
        }

        if (loweredType is not TypeRef.TStr)
        {
            ReportDiagnostic(GetSpan(pathArg), $"Ashes.IO.File.mmap() expects Str but got {Pretty(loweredType)}.");
            return (pathTemp, loweredType);
        }

        var target = NewTemp();
        Emit(new IrInst.FileMmap(target, pathTemp));
        return (target, CreateStringResultType(new TypeRef.TBytes()));
    }

    private (int, TypeRef) LowerFileReadAllBytes(Expr pathArg)
    {
        using var diagnosticSpan = PushDiagnosticSpan(pathArg);
        var (pathTemp, pathType) = LowerExpr(pathArg);
        var loweredType = Prune(pathType);

        if (loweredType is TypeRef.TNever)
        {
            return (pathTemp, loweredType);
        }

        if (loweredType is TypeRef.TVar)
        {
            Unify(loweredType, new TypeRef.TStr());
            loweredType = new TypeRef.TStr();
        }

        if (loweredType is not TypeRef.TStr)
        {
            ReportDiagnostic(GetSpan(pathArg), $"Ashes.IO.File.readAllBytes() expects Str but got {Pretty(loweredType)}.");
            return (pathTemp, loweredType);
        }

        var target = NewTemp();
        Emit(new IrInst.FileReadAllBytes(target, pathTemp));
        return (target, CreateStringResultType(new TypeRef.TBytes()));
    }

    private Binding.Intrinsic CreateFileWriteTextBinding()
    {
        return new Binding.Intrinsic(
            IntrinsicKind.FileWriteText,
            new TypeScheme([], new TypeRef.TFun(new TypeRef.TStr(), new TypeRef.TFun(new TypeRef.TStr(), CreateStringResultType(_resolvedTypes["Unit"]))))
        );
    }

    private Binding.Intrinsic CreateFileExistsBinding()
    {
        return new Binding.Intrinsic(
            IntrinsicKind.FileExists,
            new TypeScheme([], new TypeRef.TFun(new TypeRef.TStr(), CreateStringResultType(new TypeRef.TBool())))
        );
    }

    private Binding.Intrinsic CreateHttpGetBinding()
    {
        return new Binding.Intrinsic(
            IntrinsicKind.HttpGet,
            new TypeScheme([], new TypeRef.TFun(new TypeRef.TStr(), CreateStringTaskType(new TypeRef.TStr())) { Row = BuiltinCapabilityRow(NetConnectCapabilityName) })
        );
    }

    private Binding.Intrinsic CreateHttpPostBinding()
    {
        return new Binding.Intrinsic(
            IntrinsicKind.HttpPost,
            new TypeScheme([], new TypeRef.TFun(new TypeRef.TStr(), new TypeRef.TFun(new TypeRef.TStr(), CreateStringTaskType(new TypeRef.TStr())) { Row = BuiltinCapabilityRow(NetConnectCapabilityName) }))
        );
    }

    private Binding.Intrinsic CreateNetTcpConnectBinding()
    {
        return new Binding.Intrinsic(
            IntrinsicKind.NetTcpConnect,
            new TypeScheme([], new TypeRef.TFun(new TypeRef.TStr(), new TypeRef.TFun(new TypeRef.TInt(), CreateStringTaskType(_resolvedTypes["Socket"])) { Row = BuiltinCapabilityRow(NetConnectCapabilityName) }))
        );
    }

    private Binding.Intrinsic CreateNetTcpSendBinding()
    {
        return new Binding.Intrinsic(
            IntrinsicKind.NetTcpSend,
            new TypeScheme([], new TypeRef.TFun(_resolvedTypes["Socket"], new TypeRef.TFun(new TypeRef.TStr(), CreateStringTaskType(new TypeRef.TInt()))))
        );
    }

    private Binding.Intrinsic CreateNetTcpReceiveBinding()
    {
        return new Binding.Intrinsic(
            IntrinsicKind.NetTcpReceive,
            new TypeScheme([], new TypeRef.TFun(_resolvedTypes["Socket"], new TypeRef.TFun(new TypeRef.TInt(), CreateStringTaskType(new TypeRef.TStr()))))
        );
    }

    private Binding.Intrinsic CreateNetTcpCloseBinding()
    {
        return new Binding.Intrinsic(
            IntrinsicKind.NetTcpClose,
            new TypeScheme([], new TypeRef.TFun(_resolvedTypes["Socket"], CreateStringTaskType(_resolvedTypes["Unit"])))
        );
    }

    private Binding.Intrinsic CreateNetTcpListenBinding()
    {
        return new Binding.Intrinsic(
            IntrinsicKind.NetTcpListen,
            new TypeScheme([], new TypeRef.TFun(new TypeRef.TInt(), CreateStringTaskType(_resolvedTypes["Socket"])) { Row = BuiltinCapabilityRow(NetListenCapabilityName) })
        );
    }

    private Binding.Intrinsic CreateNetTcpAcceptBinding()
    {
        return new Binding.Intrinsic(
            IntrinsicKind.NetTcpAccept,
            new TypeScheme([], new TypeRef.TFun(_resolvedTypes["Socket"], CreateStringTaskType(_resolvedTypes["Socket"])))
        );
    }

    private Binding.Intrinsic CreateNetForkWorkersBinding()
    {
        return new Binding.Intrinsic(
            IntrinsicKind.NetForkWorkers,
            new TypeScheme([], new TypeRef.TFun(new TypeRef.TInt(), new TypeRef.TFun(new TypeRef.TInt(), CreateStringTaskType(new TypeRef.TInt())) { Row = BuiltinCapabilityRow(NetListenCapabilityName) }))
        );
    }

    private Binding.Intrinsic CreateNetSetDrainTimeoutBinding()
    {
        return new Binding.Intrinsic(
            IntrinsicKind.NetSetDrainTimeout,
            new TypeScheme([], new TypeRef.TFun(new TypeRef.TInt(), _resolvedTypes["Unit"]))
        );
    }

    private Binding.Intrinsic CreateNetTlsConnectBinding()
    {
        return new Binding.Intrinsic(
            IntrinsicKind.NetTlsConnect,
            new TypeScheme([], new TypeRef.TFun(new TypeRef.TStr(), new TypeRef.TFun(new TypeRef.TInt(), CreateStringTaskType(_resolvedTypes["TlsSocket"])) { Row = BuiltinCapabilityRow(NetConnectCapabilityName) }))
        );
    }

    // Ashes.Net.Tls.Server.handshake : Socket -> Str -> Str -> Task(Str, TlsSocket)
    // (accepted TCP socket, certificate-chain PEM contents, private-key PEM contents)
    private Binding.Intrinsic CreateNetTlsServerHandshakeBinding()
    {
        return new Binding.Intrinsic(
            IntrinsicKind.NetTlsServerHandshake,
            new TypeScheme([], new TypeRef.TFun(_resolvedTypes["Socket"], new TypeRef.TFun(new TypeRef.TStr(), new TypeRef.TFun(new TypeRef.TStr(), CreateStringTaskType(_resolvedTypes["TlsSocket"])))))
        );
    }

    private (int, TypeRef) LowerNetTlsServerHandshake(Expr socketArg, Expr certArg, Expr keyArg)
    {
        using var socketSpan = PushDiagnosticSpan(socketArg);
        CheckUseAfterDrop(socketArg);
        var (socketTemp, socketType) = LowerExpr(socketArg);
        var prunedSocketType = Prune(socketType);
        if (prunedSocketType is TypeRef.TNever)
        {
            return (socketTemp, prunedSocketType);
        }

        Unify(prunedSocketType, _resolvedTypes["Socket"]);

        // The accepted socket is consumed into the TLS session (which owns the fd from here on),
        // so the caller's scope must not also close it.
        MarkResourceArgMoved(socketArg);

        using var certSpan = PushDiagnosticSpan(certArg);
        var (certTemp, certType) = LowerExpr(certArg);
        Unify(Prune(certType), new TypeRef.TStr());

        using var keySpan = PushDiagnosticSpan(keyArg);
        var (keyTemp, keyType) = LowerExpr(keyArg);
        Unify(Prune(keyType), new TypeRef.TStr());

        var taskTemp = NewTemp();
        Emit(new IrInst.CreateTlsServerHandshakeTask(taskTemp, socketTemp, certTemp, keyTemp));
        return (taskTemp, CreateStringTaskType(_resolvedTypes["TlsSocket"]));
    }

    private Binding.Intrinsic CreateNetTlsSendBinding()
    {
        return new Binding.Intrinsic(
            IntrinsicKind.NetTlsSend,
            new TypeScheme([], new TypeRef.TFun(_resolvedTypes["TlsSocket"], new TypeRef.TFun(new TypeRef.TStr(), CreateStringTaskType(new TypeRef.TInt()))))
        );
    }

    private Binding.Intrinsic CreateNetTlsReceiveBinding()
    {
        return new Binding.Intrinsic(
            IntrinsicKind.NetTlsReceive,
            new TypeScheme([], new TypeRef.TFun(_resolvedTypes["TlsSocket"], new TypeRef.TFun(new TypeRef.TInt(), CreateStringTaskType(new TypeRef.TStr()))))
        );
    }

    private Binding.Intrinsic CreateNetTlsCloseBinding()
    {
        return new Binding.Intrinsic(
            IntrinsicKind.NetTlsClose,
            new TypeScheme([], new TypeRef.TFun(_resolvedTypes["TlsSocket"], CreateStringTaskType(_resolvedTypes["Unit"])))
        );
    }

    private static Binding.Intrinsic CreatePanicBinding()
    {
        return new Binding.Intrinsic(
            IntrinsicKind.Panic,
            new TypeScheme([], new TypeRef.TFun(new TypeRef.TStr(), new TypeRef.TNever()))
        );
    }

    private static Binding.PreludeValue CreateArgsBinding()
    {
        return new Binding.PreludeValue(
            PreludeValueKind.Args,
            new TypeScheme([], new TypeRef.TList(new TypeRef.TStr()))
        );
    }

    // Ashes.Task.run : Task(E, A) -> Result(E, A)
    private Binding.Intrinsic CreateAsyncRunBinding()
    {
        if (!_typeSymbols.TryGetValue("Task", out var taskSymbol)
            || !_typeSymbols.TryGetValue("Result", out var resultSymbol))
        {
            throw new InvalidOperationException("Built-in Task or Result type is not registered.");
        }

        var e = new TypeRef.TVar(_nextTypeVar++);
        var a = new TypeRef.TVar(_nextTypeVar++);
        var taskType = new TypeRef.TNamedType(taskSymbol, [e, a]);
        var resultType = new TypeRef.TNamedType(resultSymbol, [e, a]);
        return new Binding.Intrinsic(
            IntrinsicKind.AsyncRun,
            new TypeScheme([new TypeVar(((TypeRef.TVar)e).Id, "E"), new TypeVar(((TypeRef.TVar)a).Id, "A")], new TypeRef.TFun(taskType, resultType))
        );
    }

    // Ashes.Task.task : A -> Task(Str, A)
    private Binding.Intrinsic CreateAsyncTaskBinding()
    {
        if (!_typeSymbols.TryGetValue("Task", out var taskSymbol))
        {
            throw new InvalidOperationException("Built-in Task type is not registered.");
        }

        var a = new TypeRef.TVar(_nextTypeVar++);
        var taskType = new TypeRef.TNamedType(taskSymbol, [new TypeRef.TStr(), a]);
        return new Binding.Intrinsic(
            IntrinsicKind.AsyncTask,
            new TypeScheme([new TypeVar(((TypeRef.TVar)a).Id, "A")], new TypeRef.TFun(a, taskType))
        );
    }

    // Ashes.Task.fromResult : Result(E, A) -> Task(E, A)
    private Binding.Intrinsic CreateAsyncFromResultBinding()
    {
        if (!_typeSymbols.TryGetValue("Task", out var taskSymbol)
            || !_typeSymbols.TryGetValue("Result", out var resultSymbol))
        {
            throw new InvalidOperationException("Built-in Task or Result type is not registered.");
        }

        var e = new TypeRef.TVar(_nextTypeVar++);
        var a = new TypeRef.TVar(_nextTypeVar++);
        var resultType = new TypeRef.TNamedType(resultSymbol, [e, a]);
        var taskType = new TypeRef.TNamedType(taskSymbol, [e, a]);
        return new Binding.Intrinsic(
            IntrinsicKind.AsyncFromResult,
            new TypeScheme([new TypeVar(((TypeRef.TVar)e).Id, "E"), new TypeVar(((TypeRef.TVar)a).Id, "A")], new TypeRef.TFun(resultType, taskType))
        );
    }

    // Ashes.Task.sleep : Int -> Task(Str, Int)
    private Binding.Intrinsic CreateAsyncSleepBinding()
    {
        if (!_typeSymbols.TryGetValue("Task", out var taskSymbol))
        {
            throw new InvalidOperationException("Built-in Task type is not registered.");
        }

        var taskType = new TypeRef.TNamedType(taskSymbol, [new TypeRef.TStr(), new TypeRef.TInt()]);
        return new Binding.Intrinsic(
            IntrinsicKind.AsyncSleep,
            new TypeScheme([], new TypeRef.TFun(new TypeRef.TInt(), taskType))
        );
    }

    // Ashes.Task.spawn : Task(E, A) -> Unit
    private Binding.Intrinsic CreateAsyncSpawnBinding()
    {
        if (!_typeSymbols.TryGetValue("Task", out var taskSymbol))
        {
            throw new InvalidOperationException("Built-in Task type is not registered.");
        }

        var e = new TypeRef.TVar(_nextTypeVar++);
        var a = new TypeRef.TVar(_nextTypeVar++);
        var taskType = new TypeRef.TNamedType(taskSymbol, [e, a]);
        return new Binding.Intrinsic(
            IntrinsicKind.AsyncSpawn,
            new TypeScheme([new TypeVar(((TypeRef.TVar)e).Id, "E"), new TypeVar(((TypeRef.TVar)a).Id, "A")], new TypeRef.TFun(taskType, _resolvedTypes["Unit"]))
        );
    }

    /// <summary>
    /// Ashes.Task.spawn(task) — detach a task for fire-and-forget cooperative execution.
    /// The task's frame is copied into a private arena and it advances whenever any driver
    /// blocks waiting on a pending leaf; its result is dropped.
    /// </summary>
    private (int, TypeRef) LowerAsyncSpawn(Expr taskArg)
    {
        using var diagnosticSpan = PushDiagnosticSpan(taskArg);

        var (taskTemp, taskType) = LowerExpr(taskArg);

        if (!_typeSymbols.TryGetValue("Task", out var taskSymbol))
        {
            ReportDiagnostic(GetSpan(taskArg), "Internal error: Task type not registered.");
            return ReturnNeverWithDummyTemp();
        }

        var errorType = NewTypeVar();
        var successType = NewTypeVar();
        var expectedTaskType = new TypeRef.TNamedType(taskSymbol, [errorType, successType]);
        Unify(taskType, expectedTaskType);

        // Ownership of every resource the spawned task references moves into the detached task:
        // the task outlives the spawner's scope, so that scope must not drop (close) them — the
        // handler owns its connection and closes it itself. Mirrors the aggregate/closure move rules.
        foreach (var freeName in FreeVars(taskArg, []))
        {
            if (LookupOwnedValue(freeName) is { IsDropped: false } moved
                && (moved.IsResource || moved.IsResourceBearing))
            {
                moved.IsDropped = true;
            }
        }

        int unitTemp = NewTemp();
        Emit(new IrInst.SpawnTask(unitTemp, taskTemp));
        return (unitTemp, _resolvedTypes["Unit"]);
    }

    // Ashes.Task.all : List(Task(E, A)) -> Task(E, List(A))
    private Binding.Intrinsic CreateAsyncAllBinding()
    {
        if (!_typeSymbols.TryGetValue("Task", out var taskSymbol))
        {
            throw new InvalidOperationException("Built-in Task type is not registered.");
        }

        var e = new TypeRef.TVar(_nextTypeVar++);
        var a = new TypeRef.TVar(_nextTypeVar++);
        var innerTaskType = new TypeRef.TNamedType(taskSymbol, [e, a]);
        var inputType = new TypeRef.TList(innerTaskType);
        var resultType = new TypeRef.TNamedType(taskSymbol, [e, new TypeRef.TList(a)]);
        return new Binding.Intrinsic(
            IntrinsicKind.AsyncAll,
            new TypeScheme([new TypeVar(((TypeRef.TVar)e).Id, "E"), new TypeVar(((TypeRef.TVar)a).Id, "A")], new TypeRef.TFun(inputType, resultType))
        );
    }

    // Ashes.Task.race : List(Task(E, A)) -> Task(E, A)
    private Binding.Intrinsic CreateAsyncRaceBinding()
    {
        if (!_typeSymbols.TryGetValue("Task", out var taskSymbol))
        {
            throw new InvalidOperationException("Built-in Task type is not registered.");
        }

        var e = new TypeRef.TVar(_nextTypeVar++);
        var a = new TypeRef.TVar(_nextTypeVar++);
        var innerTaskType = new TypeRef.TNamedType(taskSymbol, [e, a]);
        var inputType = new TypeRef.TList(innerTaskType);
        var resultType = new TypeRef.TNamedType(taskSymbol, [e, a]);
        return new Binding.Intrinsic(
            IntrinsicKind.AsyncRace,
            new TypeScheme([new TypeVar(((TypeRef.TVar)e).Id, "E"), new TypeVar(((TypeRef.TVar)a).Id, "A")], new TypeRef.TFun(inputType, resultType))
        );
    }

    // --- Ashes.Byte lowering methods ---

    private (int, TypeRef) LowerBytesEmpty(Expr arg)
    {
        using var diagnosticSpan = PushDiagnosticSpan(arg);
        var (argTemp, argType) = LowerExpr(arg);
        var prunedArgType = Prune(argType);
        if (prunedArgType is TypeRef.TNever)
        {
            return (argTemp, prunedArgType);
        }

        Unify(prunedArgType, _resolvedTypes["Unit"]);
        var target = NewTemp();
        Emit(new IrInst.BytesEmpty(target));
        return (target, new TypeRef.TBytes());
    }

    private (int, TypeRef) LowerBytesSingleton(Expr byteArg)
    {
        using var diagnosticSpan = PushDiagnosticSpan(byteArg);
        var (byteTemp, byteType) = LowerExpr(byteArg);
        var prunedByteType = Prune(byteType);
        if (prunedByteType is TypeRef.TNever)
        {
            return (byteTemp, prunedByteType);
        }

        if (prunedByteType is TypeRef.TVar)
        {
            Unify(prunedByteType, new TypeRef.TUInt(8));
            prunedByteType = new TypeRef.TUInt(8);
        }

        if (prunedByteType is not TypeRef.TUInt { Bits: 8 })
        {
            ReportDiagnostic(GetSpan(byteArg), $"Ashes.Byte.singleton() expects u8 but got {Pretty(prunedByteType)}.");
            return (byteTemp, prunedByteType);
        }

        var target = NewTemp();
        Emit(new IrInst.BytesSingleton(target, byteTemp));
        return (target, new TypeRef.TBytes());
    }

    private (int, TypeRef) LowerBytesLength(Expr bytesArg)
    {
        using var diagnosticSpan = PushDiagnosticSpan(bytesArg);
        var (bytesTemp, bytesType) = LowerExpr(bytesArg);
        var prunedBytesType = Prune(bytesType);
        if (prunedBytesType is TypeRef.TNever)
        {
            return (bytesTemp, prunedBytesType);
        }

        if (prunedBytesType is TypeRef.TVar)
        {
            Unify(prunedBytesType, new TypeRef.TBytes());
            prunedBytesType = new TypeRef.TBytes();
        }

        if (prunedBytesType is not TypeRef.TBytes)
        {
            ReportDiagnostic(GetSpan(bytesArg), $"Ashes.Byte.length() expects Bytes but got {Pretty(prunedBytesType)}.");
            return (bytesTemp, prunedBytesType);
        }

        var target = NewTemp();
        Emit(new IrInst.BytesLength(target, bytesTemp));
        return (target, new TypeRef.TInt());
    }

    private (int, TypeRef) LowerBytesGet(Expr bytesArg, Expr indexArg)
    {
        using var bytesDiagnosticSpan = PushDiagnosticSpan(bytesArg);
        var (bytesTemp, bytesType) = LowerExpr(bytesArg);
        var prunedBytesType = Prune(bytesType);
        if (prunedBytesType is TypeRef.TNever)
        {
            return (bytesTemp, prunedBytesType);
        }

        if (prunedBytesType is TypeRef.TVar)
        {
            Unify(prunedBytesType, new TypeRef.TBytes());
            prunedBytesType = new TypeRef.TBytes();
        }

        if (prunedBytesType is not TypeRef.TBytes)
        {
            ReportDiagnostic(GetSpan(bytesArg), $"Ashes.Byte.get() expects Bytes but got {Pretty(prunedBytesType)}.");
            return (bytesTemp, prunedBytesType);
        }

        using var indexDiagnosticSpan = PushDiagnosticSpan(indexArg);
        var (indexTemp, indexType) = LowerExpr(indexArg);
        var prunedIndexType = Prune(indexType);
        if (prunedIndexType is TypeRef.TNever)
        {
            return (indexTemp, prunedIndexType);
        }

        if (prunedIndexType is TypeRef.TVar)
        {
            Unify(prunedIndexType, new TypeRef.TInt());
            prunedIndexType = new TypeRef.TInt();
        }

        if (prunedIndexType is not TypeRef.TInt)
        {
            ReportDiagnostic(GetSpan(indexArg), $"Ashes.Byte.get() expects Int for index but got {Pretty(prunedIndexType)}.");
            return (indexTemp, prunedIndexType);
        }

        var target = NewTemp();
        Emit(new IrInst.BytesGet(target, bytesTemp, indexTemp));
        return (target, new TypeRef.TUInt(8));
    }

    // Lower an argument that must be Int (coercing an unresolved type var to Int). Returns
    // (temp, ok); ok is false if the argument had a concrete non-Int type (a diagnostic was
    // reported) so the caller can bail with the offending temp/type.
    private (int Temp, bool Ok) LowerIntArgument(Expr arg, string diagnosticContext)
    {
        using var span = PushDiagnosticSpan(arg);
        var (temp, type) = LowerExpr(arg);
        var pruned = Prune(type);
        if (pruned is TypeRef.TNever)
        {
            return (temp, false);
        }

        if (pruned is TypeRef.TVar)
        {
            Unify(pruned, new TypeRef.TInt());
            pruned = new TypeRef.TInt();
        }

        if (pruned is not TypeRef.TInt)
        {
            ReportDiagnostic(GetSpan(arg), $"{diagnosticContext} expects Int but got {Pretty(pruned)}.");
            return (temp, false);
        }

        return (temp, true);
    }

    private (int Temp, bool Ok) LowerBytesArgument(Expr arg, string diagnosticContext)
    {
        using var span = PushDiagnosticSpan(arg);
        var (temp, type) = LowerExpr(arg);
        var pruned = Prune(type);
        if (pruned is TypeRef.TNever)
        {
            return (temp, false);
        }

        if (pruned is TypeRef.TVar)
        {
            Unify(pruned, new TypeRef.TBytes());
            pruned = new TypeRef.TBytes();
        }

        if (pruned is not TypeRef.TBytes)
        {
            ReportDiagnostic(GetSpan(arg), $"{diagnosticContext} expects Bytes but got {Pretty(pruned)}.");
            return (temp, false);
        }

        return (temp, true);
    }

    private (int, TypeRef) LowerBytesIndexOf(Expr bytesArg, Expr needleArg, Expr fromArg)
    {
        var (bytesTemp, bytesOk) = LowerBytesArgument(bytesArg, "Ashes.Byte.indexOf()");
        if (!bytesOk)
        {
            return (bytesTemp, new TypeRef.TInt());
        }

        var (needleTemp, needleOk) = LowerIntArgument(needleArg, "Ashes.Byte.indexOf() needle");
        if (!needleOk)
        {
            return (needleTemp, new TypeRef.TInt());
        }

        var (fromTemp, fromOk) = LowerIntArgument(fromArg, "Ashes.Byte.indexOf() from");
        if (!fromOk)
        {
            return (fromTemp, new TypeRef.TInt());
        }

        var target = NewTemp();
        Emit(new IrInst.BytesIndexOf(target, bytesTemp, needleTemp, fromTemp));
        return (target, new TypeRef.TInt());
    }

    private (int, TypeRef) LowerBytesCompare(Expr leftArg, Expr rightArg)
    {
        var (leftTemp, leftOk) = LowerBytesArgument(leftArg, "Ashes.Byte.compare()");
        if (!leftOk)
        {
            return (leftTemp, new TypeRef.TInt());
        }

        var (rightTemp, rightOk) = LowerBytesArgument(rightArg, "Ashes.Byte.compare()");
        if (!rightOk)
        {
            return (rightTemp, new TypeRef.TInt());
        }

        var target = NewTemp();
        Emit(new IrInst.BytesCompare(target, leftTemp, rightTemp));
        return (target, new TypeRef.TInt());
    }

    private (int, TypeRef) LowerBytesSubText(Expr bytesArg, Expr startArg, Expr lenArg)
    {
        var (bytesTemp, bytesOk) = LowerBytesArgument(bytesArg, "Ashes.Byte.subText()");
        if (!bytesOk)
        {
            return (bytesTemp, new TypeRef.TStr());
        }

        var (startTemp, startOk) = LowerIntArgument(startArg, "Ashes.Byte.subText() start");
        if (!startOk)
        {
            return (startTemp, new TypeRef.TStr());
        }

        var (lenTemp, lenOk) = LowerIntArgument(lenArg, "Ashes.Byte.subText() length");
        if (!lenOk)
        {
            return (lenTemp, new TypeRef.TStr());
        }

        var target = NewTemp();
        Emit(new IrInst.BytesSubText(target, bytesTemp, startTemp, lenTemp));
        return (target, new TypeRef.TStr());
    }

    private (int, TypeRef) LowerBytesScanHash(Expr bytesArg, Expr needleArg, Expr fromArg)
    {
        var (bytesTemp, bytesOk) = LowerBytesArgument(bytesArg, "Ashes.Byte.scanHash()");
        var resultType = new TypeRef.TTuple([new TypeRef.TInt(), new TypeRef.TInt()]);
        if (!bytesOk)
        {
            return (bytesTemp, resultType);
        }

        var (needleTemp, needleOk) = LowerIntArgument(needleArg, "Ashes.Byte.scanHash() needle");
        if (!needleOk)
        {
            return (needleTemp, resultType);
        }

        var (fromTemp, fromOk) = LowerIntArgument(fromArg, "Ashes.Byte.scanHash() from");
        if (!fromOk)
        {
            return (fromTemp, resultType);
        }

        var target = NewTemp();
        Emit(new IrInst.BytesScanHash(target, bytesTemp, needleTemp, fromTemp));
        return (target, resultType);
    }

    private (int, TypeRef) LowerBytesSubView(Expr bytesArg, Expr startArg, Expr lenArg)
    {
        var (bytesTemp, bytesOk) = LowerBytesArgument(bytesArg, "Ashes.Byte.subView()");
        if (!bytesOk)
        {
            return (bytesTemp, new TypeRef.TStr());
        }

        var (startTemp, startOk) = LowerIntArgument(startArg, "Ashes.Byte.subView() start");
        if (!startOk)
        {
            return (startTemp, new TypeRef.TStr());
        }

        var (lenTemp, lenOk) = LowerIntArgument(lenArg, "Ashes.Byte.subView() length");
        if (!lenOk)
        {
            return (lenTemp, new TypeRef.TStr());
        }

        var target = NewTemp();
        Emit(new IrInst.BytesSubView(target, bytesTemp, startTemp, lenTemp));
        return (target, new TypeRef.TStr());
    }

    private (int, TypeRef) LowerBytesAppend(Expr leftArg, Expr rightArg)
    {
        using var leftDiagnosticSpan = PushDiagnosticSpan(leftArg);
        var (leftTemp, leftType) = LowerExpr(leftArg);
        var prunedLeftType = Prune(leftType);
        if (prunedLeftType is TypeRef.TNever)
        {
            return (leftTemp, prunedLeftType);
        }

        if (prunedLeftType is TypeRef.TVar)
        {
            Unify(prunedLeftType, new TypeRef.TBytes());
            prunedLeftType = new TypeRef.TBytes();
        }

        if (prunedLeftType is not TypeRef.TBytes)
        {
            ReportDiagnostic(GetSpan(leftArg), $"Ashes.Byte.append() expects Bytes for first argument but got {Pretty(prunedLeftType)}.");
            return (leftTemp, prunedLeftType);
        }

        using var rightDiagnosticSpan = PushDiagnosticSpan(rightArg);
        var (rightTemp, rightType) = LowerExpr(rightArg);
        var prunedRightType = Prune(rightType);
        if (prunedRightType is TypeRef.TNever)
        {
            return (rightTemp, prunedRightType);
        }

        if (prunedRightType is TypeRef.TVar)
        {
            Unify(prunedRightType, new TypeRef.TBytes());
            prunedRightType = new TypeRef.TBytes();
        }

        if (prunedRightType is not TypeRef.TBytes)
        {
            ReportDiagnostic(GetSpan(rightArg), $"Ashes.Byte.append() expects Bytes for second argument but got {Pretty(prunedRightType)}.");
            return (rightTemp, prunedRightType);
        }

        var target = NewTemp();
        Emit(new IrInst.BytesAppend(target, leftTemp, rightTemp));
        return (target, new TypeRef.TBytes());
    }

    private (int, TypeRef) LowerBytesAppendByte(Expr bytesArg, Expr byteArg)
    {
        using var bytesDiagnosticSpan = PushDiagnosticSpan(bytesArg);
        var (bytesTemp, bytesType) = LowerExpr(bytesArg);
        var prunedBytesType = Prune(bytesType);
        if (prunedBytesType is TypeRef.TNever)
        {
            return (bytesTemp, prunedBytesType);
        }

        if (prunedBytesType is TypeRef.TVar)
        {
            Unify(prunedBytesType, new TypeRef.TBytes());
            prunedBytesType = new TypeRef.TBytes();
        }

        if (prunedBytesType is not TypeRef.TBytes)
        {
            ReportDiagnostic(GetSpan(bytesArg), $"Ashes.Byte.appendByte() expects Bytes but got {Pretty(prunedBytesType)}.");
            return (bytesTemp, prunedBytesType);
        }

        using var byteDiagnosticSpan = PushDiagnosticSpan(byteArg);
        var (byteTemp, byteType) = LowerExpr(byteArg);
        var prunedByteType = Prune(byteType);
        if (prunedByteType is TypeRef.TNever)
        {
            return (byteTemp, prunedByteType);
        }

        if (prunedByteType is TypeRef.TVar)
        {
            Unify(prunedByteType, new TypeRef.TUInt(8));
            prunedByteType = new TypeRef.TUInt(8);
        }

        if (prunedByteType is not TypeRef.TUInt { Bits: 8 })
        {
            ReportDiagnostic(GetSpan(byteArg), $"Ashes.Byte.appendByte() expects u8 for byte argument but got {Pretty(prunedByteType)}.");
            return (byteTemp, prunedByteType);
        }

        var target = NewTemp();
        Emit(new IrInst.BytesAppendByte(target, bytesTemp, byteTemp));
        return (target, new TypeRef.TBytes());
    }

    private (int, TypeRef) LowerBytesFromList(Expr listArg)
    {
        using var diagnosticSpan = PushDiagnosticSpan(listArg);
        var (listTemp, listType) = LowerExpr(listArg);
        var prunedListType = Prune(listType);
        if (prunedListType is TypeRef.TNever)
        {
            return (listTemp, prunedListType);
        }

        if (prunedListType is TypeRef.TVar)
        {
            Unify(prunedListType, new TypeRef.TList(new TypeRef.TUInt(8)));
            prunedListType = new TypeRef.TList(new TypeRef.TUInt(8));
        }

        if (prunedListType is not TypeRef.TList listT)
        {
            ReportDiagnostic(GetSpan(listArg), $"Ashes.Byte.fromList() expects List(u8) but got {Pretty(prunedListType)}.");
            return (listTemp, prunedListType);
        }

        var prunedElem = Prune(listT.Element);
        if (prunedElem is TypeRef.TVar)
        {
            Unify(prunedElem, new TypeRef.TUInt(8));
            prunedElem = new TypeRef.TUInt(8);
        }
        else if (prunedElem is not TypeRef.TUInt { Bits: 8 })
        {
            ReportDiagnostic(GetSpan(listArg), $"Ashes.Byte.fromList() expects List(u8) but got {Pretty(prunedListType)}.");
            return (listTemp, prunedListType);
        }

        var target = NewTemp();
        Emit(new IrInst.BytesFromList(target, listTemp));
        return (target, new TypeRef.TBytes());
    }

    private (int, TypeRef) LowerBytesU16Le(Expr valueArg)
    {
        using var diagnosticSpan = PushDiagnosticSpan(valueArg);
        var (valueTemp, valueType) = LowerExpr(valueArg);
        var prunedValueType = Prune(valueType);
        if (prunedValueType is TypeRef.TNever)
        {
            return (valueTemp, prunedValueType);
        }

        if (prunedValueType is TypeRef.TVar)
        {
            Unify(prunedValueType, new TypeRef.TUInt(16));
            prunedValueType = new TypeRef.TUInt(16);
        }

        if (prunedValueType is not TypeRef.TUInt { Bits: 16 })
        {
            ReportDiagnostic(GetSpan(valueArg), $"Ashes.Byte.u16Le() expects u16 but got {Pretty(prunedValueType)}.");
            return (valueTemp, prunedValueType);
        }

        var target = NewTemp();
        Emit(new IrInst.BytesU16Le(target, valueTemp));
        return (target, new TypeRef.TBytes());
    }

    private (int, TypeRef) LowerBytesU32Le(Expr valueArg)
    {
        using var diagnosticSpan = PushDiagnosticSpan(valueArg);
        var (valueTemp, valueType) = LowerExpr(valueArg);
        var prunedValueType = Prune(valueType);
        if (prunedValueType is TypeRef.TNever)
        {
            return (valueTemp, prunedValueType);
        }

        if (prunedValueType is TypeRef.TVar)
        {
            Unify(prunedValueType, new TypeRef.TUInt(32));
            prunedValueType = new TypeRef.TUInt(32);
        }

        if (prunedValueType is not TypeRef.TUInt { Bits: 32 })
        {
            ReportDiagnostic(GetSpan(valueArg), $"Ashes.Byte.u32Le() expects u32 but got {Pretty(prunedValueType)}.");
            return (valueTemp, prunedValueType);
        }

        var target = NewTemp();
        Emit(new IrInst.BytesU32Le(target, valueTemp));
        return (target, new TypeRef.TBytes());
    }

    private (int, TypeRef) LowerBytesU64Le(Expr valueArg)
    {
        using var diagnosticSpan = PushDiagnosticSpan(valueArg);
        var (valueTemp, valueType) = LowerExpr(valueArg);
        var prunedValueType = Prune(valueType);
        if (prunedValueType is TypeRef.TNever)
        {
            return (valueTemp, prunedValueType);
        }

        if (prunedValueType is TypeRef.TVar)
        {
            Unify(prunedValueType, new TypeRef.TUInt(64));
            prunedValueType = new TypeRef.TUInt(64);
        }

        if (prunedValueType is not TypeRef.TUInt { Bits: 64 })
        {
            ReportDiagnostic(GetSpan(valueArg), $"Ashes.Byte.u64Le() expects u64 but got {Pretty(prunedValueType)}.");
            return (valueTemp, prunedValueType);
        }

        var target = NewTemp();
        Emit(new IrInst.BytesU64Le(target, valueTemp));
        return (target, new TypeRef.TBytes());
    }

    private (int, TypeRef) LowerBytesGetU16Le(Expr bytesArg, Expr offsetArg)
        => LowerBytesGetUIntLe(bytesArg, offsetArg, 16, "getU16Le",
            (target, bytesTemp, offsetTemp) => new IrInst.BytesGetU16Le(target, bytesTemp, offsetTemp),
            new TypeRef.TUInt(16));

    private (int, TypeRef) LowerBytesGetU32Le(Expr bytesArg, Expr offsetArg)
        => LowerBytesGetUIntLe(bytesArg, offsetArg, 32, "getU32Le",
            (target, bytesTemp, offsetTemp) => new IrInst.BytesGetU32Le(target, bytesTemp, offsetTemp),
            new TypeRef.TUInt(32));

    private (int, TypeRef) LowerBytesGetU64Le(Expr bytesArg, Expr offsetArg)
        => LowerBytesGetUIntLe(bytesArg, offsetArg, 64, "getU64Le",
            (target, bytesTemp, offsetTemp) => new IrInst.BytesGetU64Le(target, bytesTemp, offsetTemp),
            new TypeRef.TUInt(64));

    private (int, TypeRef) LowerBytesGetUIntLe(Expr bytesArg, Expr offsetArg, int bits, string name,
        Func<int, int, int, IrInst> makeInst, TypeRef resultType)
    {
        using var bytesDiagnosticSpan = PushDiagnosticSpan(bytesArg);
        var (bytesTemp, bytesType) = LowerExpr(bytesArg);
        var prunedBytesType = Prune(bytesType);
        if (prunedBytesType is TypeRef.TNever)
        {
            return (bytesTemp, prunedBytesType);
        }

        if (prunedBytesType is TypeRef.TVar)
        {
            Unify(prunedBytesType, new TypeRef.TBytes());
            prunedBytesType = new TypeRef.TBytes();
        }

        if (prunedBytesType is not TypeRef.TBytes)
        {
            ReportDiagnostic(GetSpan(bytesArg), $"Ashes.Byte.{name}() expects Bytes but got {Pretty(prunedBytesType)}.");
            return (bytesTemp, prunedBytesType);
        }

        using var offsetDiagnosticSpan = PushDiagnosticSpan(offsetArg);
        var (offsetTemp, offsetType) = LowerExpr(offsetArg);
        var prunedOffsetType = Prune(offsetType);
        if (prunedOffsetType is TypeRef.TNever)
        {
            return (offsetTemp, prunedOffsetType);
        }

        if (prunedOffsetType is TypeRef.TVar)
        {
            Unify(prunedOffsetType, new TypeRef.TInt());
            prunedOffsetType = new TypeRef.TInt();
        }

        if (prunedOffsetType is not TypeRef.TInt)
        {
            ReportDiagnostic(GetSpan(offsetArg), $"Ashes.Byte.{name}() expects Int for offset but got {Pretty(prunedOffsetType)}.");
            return (offsetTemp, prunedOffsetType);
        }

        var target = NewTemp();
        Emit(makeInst(target, bytesTemp, offsetTemp));
        return (target, resultType);
    }

    private (int, TypeRef) LowerFileWriteBytes(Expr pathArg, Expr bytesArg)
    {
        using var pathDiagnosticSpan = PushDiagnosticSpan(pathArg);
        var (pathTemp, pathType) = LowerExpr(pathArg);
        var prunedPathType = Prune(pathType);
        if (prunedPathType is TypeRef.TNever)
        {
            return (pathTemp, prunedPathType);
        }

        if (prunedPathType is TypeRef.TVar)
        {
            Unify(prunedPathType, new TypeRef.TStr());
            prunedPathType = new TypeRef.TStr();
        }

        if (prunedPathType is not TypeRef.TStr)
        {
            ReportDiagnostic(GetSpan(pathArg), $"Ashes.IO.File.writeBytes() expects Str for path but got {Pretty(prunedPathType)}.");
            return (pathTemp, prunedPathType);
        }

        using var bytesDiagnosticSpan = PushDiagnosticSpan(bytesArg);
        var (bytesTemp, bytesType) = LowerExpr(bytesArg);
        var prunedBytesType = Prune(bytesType);
        if (prunedBytesType is TypeRef.TNever)
        {
            return (bytesTemp, prunedBytesType);
        }

        if (prunedBytesType is TypeRef.TVar)
        {
            Unify(prunedBytesType, new TypeRef.TBytes());
            prunedBytesType = new TypeRef.TBytes();
        }

        if (prunedBytesType is not TypeRef.TBytes)
        {
            ReportDiagnostic(GetSpan(bytesArg), $"Ashes.IO.File.writeBytes() expects Bytes but got {Pretty(prunedBytesType)}.");
            return (bytesTemp, prunedBytesType);
        }

        var target = NewTemp();
        Emit(new IrInst.FileWriteBytes(target, pathTemp, bytesTemp));
        return (target, CreateStringResultType(_resolvedTypes["Unit"]));
    }

    // --- Ashes.Byte binding factories ---

    private Binding.Intrinsic CreateBytesEmptyBinding()
    {
        return new Binding.Intrinsic(
            IntrinsicKind.BytesEmpty,
            new TypeScheme([], new TypeRef.TFun(_resolvedTypes["Unit"], new TypeRef.TBytes()))
        );
    }

    private Binding.Intrinsic CreateBytesSingletonBinding()
    {
        return new Binding.Intrinsic(
            IntrinsicKind.BytesSingleton,
            new TypeScheme([], new TypeRef.TFun(new TypeRef.TUInt(8), new TypeRef.TBytes()))
        );
    }

    private Binding.Intrinsic CreateBytesLengthBinding()
    {
        return new Binding.Intrinsic(
            IntrinsicKind.BytesLength,
            new TypeScheme([], new TypeRef.TFun(new TypeRef.TBytes(), new TypeRef.TInt()))
        );
    }

    // Ashes.Number.UInt.toInt : u8 -> Int (the value type of the partially-applied reference). A saturated
    // call accepts any unsigned width (u8/u16/u32/u64) — the width is checked in LowerUIntToInt, which
    // does not go through this scheme.
    private Binding.Intrinsic CreateUIntToIntBinding()
    {
        return new Binding.Intrinsic(
            IntrinsicKind.UIntToInt,
            new TypeScheme([], new TypeRef.TFun(new TypeRef.TUInt(8), new TypeRef.TInt()))
        );
    }

    // Ashes.Number.UInt.toInt(x) : Int — widen an unsigned integer to a signed Int. Every uN is stored as a
    // width-masked i64, so reinterpreting it as Int is value-preserving for u8/u16/u32 (and a
    // bit-reinterpret for u64); no runtime instruction is needed, just a retype.
    private (int, TypeRef) LowerUIntToInt(Expr arg)
    {
        using var argDiagnosticSpan = PushDiagnosticSpan(arg);
        var (argTemp, argType) = LowerExpr(arg);
        var pruned = Prune(argType);
        if (pruned is TypeRef.TNever)
        {
            return (argTemp, pruned);
        }

        if (pruned is TypeRef.TVar)
        {
            // A bare `Ashes.Number.UInt.toInt(x)` with an otherwise-unconstrained argument defaults to u8 (the
            // common case: a byte from Ashes.Byte.get).
            Unify(pruned, new TypeRef.TUInt(8));
            pruned = new TypeRef.TUInt(8);
        }

        if (pruned is not TypeRef.TUInt)
        {
            ReportDiagnostic(GetSpan(arg), $"Ashes.Number.UInt.toInt() expects an unsigned integer (u8/u16/u32/u64) but got {Pretty(pruned)}.");
            return (argTemp, new TypeRef.TInt());
        }

        return (argTemp, new TypeRef.TInt());
    }

    private Binding.Intrinsic CreateUIntFromIntBinding()
    {
        return new Binding.Intrinsic(
            IntrinsicKind.UIntFromInt,
            new TypeScheme([], new TypeRef.TFun(new TypeRef.TInt(), new TypeRef.TUInt(8)))
        );
    }

    // Ashes.Number.UInt.fromInt(x) : u8 — narrow a signed Int to an unsigned byte, wrapping modulo 256 (mask
    // to the low 8 bits) so the value is a valid u8 regardless of the input's magnitude/sign. The
    // inverse of Ashes.Number.UInt.toInt; unlike toInt this needs a real mask because the internal i64 could
    // carry bits above the byte width.
    private (int, TypeRef) LowerUIntFromInt(Expr arg)
    {
        using var argDiagnosticSpan = PushDiagnosticSpan(arg);
        var (argTemp, argType) = LowerExpr(arg);
        var pruned = Prune(argType);
        if (pruned is TypeRef.TNever)
        {
            return (argTemp, pruned);
        }

        if (pruned is TypeRef.TVar)
        {
            Unify(pruned, new TypeRef.TInt());
            pruned = new TypeRef.TInt();
        }

        if (pruned is not TypeRef.TInt)
        {
            ReportDiagnostic(GetSpan(arg), $"Ashes.Number.UInt.fromInt() expects Int but got {Pretty(pruned)}.");
            return (argTemp, new TypeRef.TUInt(8));
        }

        var maskTemp = NewTemp();
        Emit(new IrInst.LoadConstInt(maskTemp, 0xFF));
        var resultTemp = NewTemp();
        Emit(new IrInst.AndInt(resultTemp, argTemp, maskTemp));
        return (resultTemp, new TypeRef.TUInt(8));
    }

    // Ashes.Number.Math Layer-1 numeric conversions and Float unary primitives.

    private Binding.Intrinsic CreateMathToFloatBinding() =>
        new(IntrinsicKind.MathToFloat, new TypeScheme([], new TypeRef.TFun(new TypeRef.TInt(), new TypeRef.TFloat())));

    private Binding.Intrinsic CreateMathSqrtBinding() => CreateFloatUnaryBinding(IntrinsicKind.MathSqrt);
    private Binding.Intrinsic CreateMathFloorBinding() => CreateFloatUnaryBinding(IntrinsicKind.MathFloor);
    private Binding.Intrinsic CreateMathCeilBinding() => CreateFloatUnaryBinding(IntrinsicKind.MathCeil);
    private Binding.Intrinsic CreateMathRoundBinding() => CreateFloatUnaryBinding(IntrinsicKind.MathRound);
    private Binding.Intrinsic CreateMathTruncBinding() => CreateFloatUnaryBinding(IntrinsicKind.MathTrunc);

    private Binding.Intrinsic CreateMathFloorToIntBinding() => CreateFloatToIntBinding(IntrinsicKind.MathFloorToInt);
    private Binding.Intrinsic CreateMathRoundToIntBinding() => CreateFloatToIntBinding(IntrinsicKind.MathRoundToInt);
    private Binding.Intrinsic CreateMathTruncToIntBinding() => CreateFloatToIntBinding(IntrinsicKind.MathTruncToInt);

    private static Binding.Intrinsic CreateFloatUnaryBinding(IntrinsicKind kind) =>
        new(kind, new TypeScheme([], new TypeRef.TFun(new TypeRef.TFloat(), new TypeRef.TFloat())));

    private static Binding.Intrinsic CreateFloatToIntBinding(IntrinsicKind kind) =>
        new(kind, new TypeScheme([], new TypeRef.TFun(new TypeRef.TFloat(), new TypeRef.TInt())));

    // Ashes.Number.Math.toFloat(n) : Float — widen an Int to a Float (sitofp).
    private (int, TypeRef) LowerMathToFloat(Expr arg)
    {
        using var span = PushDiagnosticSpan(arg);
        var (argTemp, argType) = LowerExpr(arg);
        var pruned = Prune(argType);
        if (pruned is TypeRef.TNever)
        {
            return (argTemp, pruned);
        }

        if (pruned is TypeRef.TVar)
        {
            Unify(pruned, new TypeRef.TInt());
            pruned = new TypeRef.TInt();
        }

        if (pruned is not TypeRef.TInt)
        {
            ReportDiagnostic(GetSpan(arg), $"Ashes.Number.Math.toFloat() expects Int but got {Pretty(pruned)}.");
            return (argTemp, new TypeRef.TFloat());
        }

        var target = NewTemp();
        Emit(new IrInst.IntToFloat(target, argTemp));
        return (target, new TypeRef.TFloat());
    }

    // Ashes.Number.Math Float -> Float primitive (sqrt/floor/ceil/round/trunc), via the named LLVM intrinsic.
    private (int, TypeRef) LowerMathFloatUnary(Expr arg, string functionName, string llvmIntrinsic)
    {
        using var span = PushDiagnosticSpan(arg);
        var valueTemp = LowerFloatArg(arg, functionName, out bool ok);
        if (!ok)
        {
            return (valueTemp, new TypeRef.TFloat());
        }

        var target = NewTemp();
        Emit(new IrInst.FloatUnaryIntrinsic(target, valueTemp, llvmIntrinsic));
        return (target, new TypeRef.TFloat());
    }

    // Ashes.Number.Math Float -> Int narrowing. `preRound` is the LLVM intrinsic applied before the
    // truncating fptosi (floor/round), or null for truncToInt (fptosi truncates toward zero).
    private (int, TypeRef) LowerMathFloatToInt(Expr arg, string functionName, string? preRound)
    {
        using var span = PushDiagnosticSpan(arg);
        var valueTemp = LowerFloatArg(arg, functionName, out bool ok);
        if (!ok)
        {
            return (valueTemp, new TypeRef.TInt());
        }

        if (preRound is not null)
        {
            var rounded = NewTemp();
            Emit(new IrInst.FloatUnaryIntrinsic(rounded, valueTemp, preRound));
            valueTemp = rounded;
        }

        var target = NewTemp();
        Emit(new IrInst.FloatToInt(target, valueTemp));
        return (target, new TypeRef.TInt());
    }

    // Ashes.Number.Math Layer-2 transcendentals (openlibm), data-driven.

    // IntrinsicKind -> (openlibm symbol, arity). Arity 1 = Float -> Float, arity 2 = Float -> Float -> Float.
    private static readonly IReadOnlyDictionary<IntrinsicKind, (string Symbol, int Arity)> LibmIntrinsics =
        new Dictionary<IntrinsicKind, (string, int)>
        {
            [IntrinsicKind.MathSin] = ("sin", 1),
            [IntrinsicKind.MathCos] = ("cos", 1),
            [IntrinsicKind.MathTan] = ("tan", 1),
            [IntrinsicKind.MathAsin] = ("asin", 1),
            [IntrinsicKind.MathAcos] = ("acos", 1),
            [IntrinsicKind.MathAtan] = ("atan", 1),
            [IntrinsicKind.MathSinh] = ("sinh", 1),
            [IntrinsicKind.MathCosh] = ("cosh", 1),
            [IntrinsicKind.MathTanh] = ("tanh", 1),
            [IntrinsicKind.MathExp] = ("exp", 1),
            [IntrinsicKind.MathExpm1] = ("expm1", 1),
            [IntrinsicKind.MathLn] = ("log", 1),
            [IntrinsicKind.MathLog2] = ("log2", 1),
            [IntrinsicKind.MathLog10] = ("log10", 1),
            [IntrinsicKind.MathLog1p] = ("log1p", 1),
            [IntrinsicKind.MathCbrt] = ("cbrt", 1),
            [IntrinsicKind.MathPowF] = ("pow", 2),
            [IntrinsicKind.MathAtan2] = ("atan2", 2),
            [IntrinsicKind.MathHypot] = ("hypot", 2),
            [IntrinsicKind.MathFmod] = ("fmod", 2),
        };

    // The parallel BuiltinValueKind (registry) for each libm IntrinsicKind, used by the qualified
    // reference/dispatch sites. Same member names in both enums.
    private static readonly IReadOnlyDictionary<BuiltinRegistry.BuiltinValueKind, IntrinsicKind> LibmBuiltinKinds =
        new Dictionary<BuiltinRegistry.BuiltinValueKind, IntrinsicKind>
        {
            [BuiltinRegistry.BuiltinValueKind.MathSin] = IntrinsicKind.MathSin,
            [BuiltinRegistry.BuiltinValueKind.MathCos] = IntrinsicKind.MathCos,
            [BuiltinRegistry.BuiltinValueKind.MathTan] = IntrinsicKind.MathTan,
            [BuiltinRegistry.BuiltinValueKind.MathAsin] = IntrinsicKind.MathAsin,
            [BuiltinRegistry.BuiltinValueKind.MathAcos] = IntrinsicKind.MathAcos,
            [BuiltinRegistry.BuiltinValueKind.MathAtan] = IntrinsicKind.MathAtan,
            [BuiltinRegistry.BuiltinValueKind.MathSinh] = IntrinsicKind.MathSinh,
            [BuiltinRegistry.BuiltinValueKind.MathCosh] = IntrinsicKind.MathCosh,
            [BuiltinRegistry.BuiltinValueKind.MathTanh] = IntrinsicKind.MathTanh,
            [BuiltinRegistry.BuiltinValueKind.MathExp] = IntrinsicKind.MathExp,
            [BuiltinRegistry.BuiltinValueKind.MathExpm1] = IntrinsicKind.MathExpm1,
            [BuiltinRegistry.BuiltinValueKind.MathLn] = IntrinsicKind.MathLn,
            [BuiltinRegistry.BuiltinValueKind.MathLog2] = IntrinsicKind.MathLog2,
            [BuiltinRegistry.BuiltinValueKind.MathLog10] = IntrinsicKind.MathLog10,
            [BuiltinRegistry.BuiltinValueKind.MathLog1p] = IntrinsicKind.MathLog1p,
            [BuiltinRegistry.BuiltinValueKind.MathCbrt] = IntrinsicKind.MathCbrt,
            [BuiltinRegistry.BuiltinValueKind.MathPowF] = IntrinsicKind.MathPowF,
            [BuiltinRegistry.BuiltinValueKind.MathAtan2] = IntrinsicKind.MathAtan2,
            [BuiltinRegistry.BuiltinValueKind.MathHypot] = IntrinsicKind.MathHypot,
            [BuiltinRegistry.BuiltinValueKind.MathFmod] = IntrinsicKind.MathFmod,
        };

    private static Binding.Intrinsic CreateLibmBinding(IntrinsicKind kind)
    {
        int arity = LibmIntrinsics[kind].Arity;
        TypeRef type = arity == 2
            ? new TypeRef.TFun(new TypeRef.TFloat(), new TypeRef.TFun(new TypeRef.TFloat(), new TypeRef.TFloat()))
            : new TypeRef.TFun(new TypeRef.TFloat(), new TypeRef.TFloat());
        return new Binding.Intrinsic(kind, new TypeScheme([], type));
    }

    // Lowers a transcendental call (Ashes.Number.Math.sin/pow/...) to a CallLibm over its Float arguments.
    private (int, TypeRef) LowerLibm(IntrinsicKind kind, IReadOnlyList<Expr> args)
    {
        var (symbol, arity) = LibmIntrinsics[kind];
        var argTemps = new int[arity];
        for (int i = 0; i < arity; i++)
        {
            argTemps[i] = LowerFloatArg(args[i], $"Ashes.Number.Math.{symbol}", out bool ok);
            if (!ok)
            {
                return (argTemps[i], new TypeRef.TFloat());
            }
        }

        var target = NewTemp();
        Emit(new IrInst.CallLibm(target, symbol, argTemps));
        return (target, new TypeRef.TFloat());
    }

    // Lowers a Float argument, defaulting an unconstrained type variable to Float and reporting a
    // diagnostic on a non-Float argument. `ok` is false when the argument is Never or ill-typed.
    private int LowerFloatArg(Expr arg, string functionName, out bool ok)
    {
        var (argTemp, argType) = LowerExpr(arg);
        var pruned = Prune(argType);
        if (pruned is TypeRef.TNever)
        {
            ok = false;
            return argTemp;
        }

        if (pruned is TypeRef.TVar)
        {
            Unify(pruned, new TypeRef.TFloat());
            pruned = new TypeRef.TFloat();
        }

        if (pruned is not TypeRef.TFloat)
        {
            ReportDiagnostic(GetSpan(arg), $"{functionName}() expects Float but got {Pretty(pruned)}.");
            ok = false;
            return argTemp;
        }

        ok = true;
        return argTemp;
    }

    private Binding.Intrinsic CreateBytesGetBinding()
    {
        return new Binding.Intrinsic(
            IntrinsicKind.BytesGet,
            new TypeScheme([], new TypeRef.TFun(new TypeRef.TBytes(), new TypeRef.TFun(new TypeRef.TInt(), new TypeRef.TUInt(8))))
        );
    }

    // Ashes.Byte.indexOf : Bytes -> Int -> Int -> Int. Returns the index of the first byte
    // equal to the needle at or after `from`, or -1 if none. O(len - from), no allocation — a
    // memchr for scanning a buffer by integer position without materializing views.
    private Binding.Intrinsic CreateBytesIndexOfBinding()
    {
        return new Binding.Intrinsic(
            IntrinsicKind.BytesIndexOf,
            new TypeScheme([], new TypeRef.TFun(new TypeRef.TBytes(), new TypeRef.TFun(new TypeRef.TInt(), new TypeRef.TFun(new TypeRef.TInt(), new TypeRef.TInt()))))
        );
    }

    // Ashes.Byte.compare : Bytes -> Bytes -> Int. Three-way lexicographic byte comparison:
    // -1 / 0 / 1 for less / equal / greater. A memcmp over min(len) with a length tie-break —
    // one call per comparison instead of a byte-at-a-time loop.
    private Binding.Intrinsic CreateBytesCompareBinding()
    {
        return new Binding.Intrinsic(
            IntrinsicKind.BytesCompare,
            new TypeScheme([], new TypeRef.TFun(new TypeRef.TBytes(), new TypeRef.TFun(new TypeRef.TBytes(), new TypeRef.TInt())))
        );
    }

    // Ashes.Byte.subText : Bytes -> Int -> Int -> Str. Copies `len` bytes starting at `start`
    // into a fresh Str ([length][bytes]). O(len). The caller must ensure the range lies on valid
    // UTF-8 boundaries (splitting at ASCII delimiters like ';'/'\n' always does). Together with
    // indexOf this lets a chunk be scanned by integer index instead of a shrinking Str view.
    private Binding.Intrinsic CreateBytesSubTextBinding()
    {
        return new Binding.Intrinsic(
            IntrinsicKind.BytesSubText,
            new TypeScheme([], new TypeRef.TFun(new TypeRef.TBytes(), new TypeRef.TFun(new TypeRef.TInt(), new TypeRef.TFun(new TypeRef.TInt(), new TypeRef.TStr()))))
        );
    }

    // Ashes.Byte.scanHash : Bytes -> Int -> Int -> (Int, Int). One fused pass from `from`: scans
    // for the needle byte while accumulating the FNV-1a hash of the bytes before it. Returns
    // (index of the needle or -1, hash of [from, index) — or of [from, len) when not found). The
    // hash matches Ashes.Byte.hash of the same range, so it keys Ashes.Collection.HashTrie directly. Saves
    // the separate memchr and hash passes over per-record fields in scan loops.
    private Binding.Intrinsic CreateBytesScanHashBinding()
    {
        return new Binding.Intrinsic(
            IntrinsicKind.BytesScanHash,
            new TypeScheme([], new TypeRef.TFun(new TypeRef.TBytes(), new TypeRef.TFun(new TypeRef.TInt(), new TypeRef.TFun(new TypeRef.TInt(), new TypeRef.TTuple([new TypeRef.TInt(), new TypeRef.TInt()])))))
        );
    }

    // Ashes.Byte.subView : Bytes -> Int -> Int -> Str. A zero-copy VIEW of `len` bytes starting
    // at `start` (bounds-clamped like subText, O(1), no byte copy). The backing bytes must outlive
    // the view; a view stored into a structure is materialized by the copy-out/blob paths, and a
    // view over an Ashes.IO.File.mmap mapping is valid for the program's lifetime. Same UTF-8
    // boundary caveat as subText.
    private Binding.Intrinsic CreateBytesSubViewBinding()
    {
        return new Binding.Intrinsic(
            IntrinsicKind.BytesSubView,
            new TypeScheme([], new TypeRef.TFun(new TypeRef.TBytes(), new TypeRef.TFun(new TypeRef.TInt(), new TypeRef.TFun(new TypeRef.TInt(), new TypeRef.TStr()))))
        );
    }

    private Binding.Intrinsic CreateBytesAppendBinding()
    {
        return new Binding.Intrinsic(
            IntrinsicKind.BytesAppend,
            new TypeScheme([], new TypeRef.TFun(new TypeRef.TBytes(), new TypeRef.TFun(new TypeRef.TBytes(), new TypeRef.TBytes())))
        );
    }

    private Binding.Intrinsic CreateBytesAppendByteBinding()
    {
        return new Binding.Intrinsic(
            IntrinsicKind.BytesAppendByte,
            new TypeScheme([], new TypeRef.TFun(new TypeRef.TBytes(), new TypeRef.TFun(new TypeRef.TUInt(8), new TypeRef.TBytes())))
        );
    }

    private Binding.Intrinsic CreateBytesFromListBinding()
    {
        return new Binding.Intrinsic(
            IntrinsicKind.BytesFromList,
            new TypeScheme([], new TypeRef.TFun(new TypeRef.TList(new TypeRef.TUInt(8)), new TypeRef.TBytes()))
        );
    }

    // Ashes.Byte.fromText : Str -> Bytes. Str and Bytes share the runtime layout
    // ([length:i64][bytes...]) and are both immutable, so this is an identity reinterpret
    // exposing a string's UTF-8 bytes. Byte-lexicographic order over the result equals Unicode
    // codepoint order, which makes a correct total order over strings constructible in pure Ashes.
    private Binding.Intrinsic CreateBytesFromTextBinding()
    {
        return new Binding.Intrinsic(
            IntrinsicKind.BytesFromText,
            new TypeScheme([], new TypeRef.TFun(new TypeRef.TStr(), new TypeRef.TBytes()))
        );
    }

    private (int, TypeRef) LowerBytesFromText(Expr textArg)
    {
        using var diagnosticSpan = PushDiagnosticSpan(textArg);
        var (textTemp, textType) = LowerExpr(textArg);
        var prunedTextType = Prune(textType);

        if (prunedTextType is TypeRef.TNever)
        {
            return (textTemp, prunedTextType);
        }

        if (prunedTextType is TypeRef.TVar)
        {
            Unify(prunedTextType, new TypeRef.TStr());
            prunedTextType = new TypeRef.TStr();
        }

        if (prunedTextType is not TypeRef.TStr)
        {
            ReportDiagnostic(GetSpan(textArg), $"Ashes.Byte.fromText() expects Str but got {Pretty(prunedTextType)}.");
            return (textTemp, new TypeRef.TBytes());
        }

        // Identity: the same heap value is a valid Bytes; only the static type changes.
        return (textTemp, new TypeRef.TBytes());
    }

    // Ashes.Byte.hash : Bytes -> Int. 64-bit FNV-1a over the bytes. With Ashes.Byte.fromText
    // this gives string hashing, the basis for hash-keyed maps (see lib/Ashes/HashMap.ash).
    private Binding.Intrinsic CreateBytesHashBinding()
    {
        return new Binding.Intrinsic(
            IntrinsicKind.BytesHash,
            new TypeScheme([], new TypeRef.TFun(new TypeRef.TBytes(), new TypeRef.TInt()))
        );
    }

    private (int, TypeRef) LowerBytesHash(Expr bytesArg)
    {
        using var diagnosticSpan = PushDiagnosticSpan(bytesArg);
        var (bytesTemp, bytesType) = LowerExpr(bytesArg);
        var prunedBytesType = Prune(bytesType);

        if (prunedBytesType is TypeRef.TNever)
        {
            return (bytesTemp, prunedBytesType);
        }

        if (prunedBytesType is TypeRef.TVar)
        {
            Unify(prunedBytesType, new TypeRef.TBytes());
            prunedBytesType = new TypeRef.TBytes();
        }

        if (prunedBytesType is not TypeRef.TBytes)
        {
            ReportDiagnostic(GetSpan(bytesArg), $"Ashes.Byte.hash() expects Bytes but got {Pretty(prunedBytesType)}.");
            return (bytesTemp, new TypeRef.TInt());
        }

        var target = NewTemp();
        Emit(new IrInst.BytesHash(target, bytesTemp));
        return (target, new TypeRef.TInt());
    }

    private Binding.Intrinsic CreateBytesU16LeBinding()
    {
        return new Binding.Intrinsic(
            IntrinsicKind.BytesU16Le,
            new TypeScheme([], new TypeRef.TFun(new TypeRef.TUInt(16), new TypeRef.TBytes()))
        );
    }

    private Binding.Intrinsic CreateBytesU32LeBinding()
    {
        return new Binding.Intrinsic(
            IntrinsicKind.BytesU32Le,
            new TypeScheme([], new TypeRef.TFun(new TypeRef.TUInt(32), new TypeRef.TBytes()))
        );
    }

    private Binding.Intrinsic CreateBytesU64LeBinding()
    {
        return new Binding.Intrinsic(
            IntrinsicKind.BytesU64Le,
            new TypeScheme([], new TypeRef.TFun(new TypeRef.TUInt(64), new TypeRef.TBytes()))
        );
    }

    private Binding.Intrinsic CreateBytesGetU16LeBinding()
    {
        return new Binding.Intrinsic(
            IntrinsicKind.BytesGetU16Le,
            new TypeScheme([], new TypeRef.TFun(new TypeRef.TBytes(), new TypeRef.TFun(new TypeRef.TInt(), new TypeRef.TUInt(16))))
        );
    }

    private Binding.Intrinsic CreateBytesGetU32LeBinding()
    {
        return new Binding.Intrinsic(
            IntrinsicKind.BytesGetU32Le,
            new TypeScheme([], new TypeRef.TFun(new TypeRef.TBytes(), new TypeRef.TFun(new TypeRef.TInt(), new TypeRef.TUInt(32))))
        );
    }

    private Binding.Intrinsic CreateBytesGetU64LeBinding()
    {
        return new Binding.Intrinsic(
            IntrinsicKind.BytesGetU64Le,
            new TypeScheme([], new TypeRef.TFun(new TypeRef.TBytes(), new TypeRef.TFun(new TypeRef.TInt(), new TypeRef.TUInt(64))))
        );
    }

    private Binding.Intrinsic CreateFileWriteBytesBinding()
    {
        return new Binding.Intrinsic(
            IntrinsicKind.FileWriteBytes,
            new TypeScheme([], new TypeRef.TFun(new TypeRef.TStr(), new TypeRef.TFun(new TypeRef.TBytes(), CreateStringResultType(_resolvedTypes["Unit"]))))
        );
    }

    // Ashes.IO.readExact : Int -> Result(Str, Str)
    private Binding.Intrinsic CreateReadExactBinding()
    {
        return new Binding.Intrinsic(
            IntrinsicKind.ReadExact,
            new TypeScheme([], new TypeRef.TFun(new TypeRef.TInt(), CreateStringResultType(new TypeRef.TStr())))
        );
    }

    private (int, TypeRef) LowerReadExact(Expr countArg)
    {
        using var diagnosticSpan = PushDiagnosticSpan(countArg);
        var (countTemp, countType) = LowerExpr(countArg);
        var prunedCountType = Prune(countType);

        if (prunedCountType is TypeRef.TNever)
        {
            return (countTemp, prunedCountType);
        }

        if (prunedCountType is TypeRef.TVar)
        {
            Unify(prunedCountType, new TypeRef.TInt());
            prunedCountType = new TypeRef.TInt();
        }

        if (prunedCountType is not TypeRef.TInt)
        {
            ReportDiagnostic(GetSpan(countArg), $"Ashes.IO.readExact() expects Int but got {Pretty(prunedCountType)}.");
            return (countTemp, prunedCountType);
        }

        var target = NewTemp();
        Emit(new IrInst.ReadExact(target, countTemp));
        return (target, CreateStringResultType(new TypeRef.TStr()));
    }

    // Ashes.IO.Console.enableRawInput : Unit -> Bool
    private Binding.Intrinsic CreateConsoleEnableRawBinding()
    {
        return new Binding.Intrinsic(
            IntrinsicKind.ConsoleEnableRaw,
            new TypeScheme([], new TypeRef.TFun(_resolvedTypes["Unit"], new TypeRef.TBool()))
        );
    }

    private (int, TypeRef) LowerConsoleEnableRaw(Expr arg)
    {
        using var diagnosticSpan = PushDiagnosticSpan(arg);
        var (unitTemp, unitType) = LowerExpr(arg);
        var loweredType = Prune(unitType);

        if (loweredType is TypeRef.TNever)
        {
            return (unitTemp, loweredType);
        }

        Unify(loweredType, _resolvedTypes["Unit"]);

        var target = NewTemp();
        Emit(new IrInst.ConsoleEnableRaw(target));
        return (target, new TypeRef.TBool());
    }

    // Ashes.IO.Console.restoreInput : Unit -> Unit
    private Binding.Intrinsic CreateConsoleRestoreBinding()
    {
        return new Binding.Intrinsic(
            IntrinsicKind.ConsoleRestore,
            new TypeScheme([], new TypeRef.TFun(_resolvedTypes["Unit"], _resolvedTypes["Unit"]))
        );
    }

    private (int, TypeRef) LowerConsoleRestore(Expr arg)
    {
        using var diagnosticSpan = PushDiagnosticSpan(arg);
        var (unitTemp, unitType) = LowerExpr(arg);
        var loweredType = Prune(unitType);

        if (loweredType is TypeRef.TNever)
        {
            return (unitTemp, loweredType);
        }

        Unify(loweredType, _resolvedTypes["Unit"]);

        var target = NewTemp();
        Emit(new IrInst.ConsoleRestore(target));
        return (target, _resolvedTypes["Unit"]);
    }

    // Ashes.IO.Console.pollInput : Int -> Maybe(Str)
    private Binding.Intrinsic CreateConsolePollBinding()
    {
        return new Binding.Intrinsic(
            IntrinsicKind.ConsolePoll,
            new TypeScheme([], new TypeRef.TFun(new TypeRef.TInt(), CreateMaybeType(new TypeRef.TStr())))
        );
    }

    private (int, TypeRef) LowerConsolePoll(Expr timeoutArg)
    {
        using var diagnosticSpan = PushDiagnosticSpan(timeoutArg);
        var (timeoutTemp, timeoutType) = LowerExpr(timeoutArg);
        var prunedTimeoutType = Prune(timeoutType);

        if (prunedTimeoutType is TypeRef.TNever)
        {
            return (timeoutTemp, prunedTimeoutType);
        }

        Unify(prunedTimeoutType, new TypeRef.TInt());

        var target = NewTemp();
        Emit(new IrInst.ConsolePoll(target, timeoutTemp));
        return (target, CreateMaybeType(new TypeRef.TStr()));
    }

    // Ashes.IO.Console.monotonicMillis : Unit -> Int
    private Binding.Intrinsic CreateConsoleMonotonicMillisBinding()
    {
        return new Binding.Intrinsic(
            IntrinsicKind.ConsoleMonotonicMillis,
            new TypeScheme([], new TypeRef.TFun(_resolvedTypes["Unit"], new TypeRef.TInt()))
        );
    }

    private (int, TypeRef) LowerConsoleMonotonicMillis(Expr arg)
    {
        using var diagnosticSpan = PushDiagnosticSpan(arg);
        var (unitTemp, unitType) = LowerExpr(arg);
        var loweredType = Prune(unitType);

        if (loweredType is TypeRef.TNever)
        {
            return (unitTemp, loweredType);
        }

        Unify(loweredType, _resolvedTypes["Unit"]);

        var target = NewTemp();
        Emit(new IrInst.MonotonicMillis(target));
        return (target, new TypeRef.TInt());
    }

    // Ashes.Text.byteLength : Str -> Int
    private Binding.Intrinsic CreateTextByteLengthBinding()
    {
        return new Binding.Intrinsic(
            IntrinsicKind.TextByteLength,
            new TypeScheme([], new TypeRef.TFun(new TypeRef.TStr(), new TypeRef.TInt()))
        );
    }

    private (int, TypeRef) LowerTextByteLength(Expr textArg)
    {
        using var diagnosticSpan = PushDiagnosticSpan(textArg);
        var (textTemp, textType) = LowerExpr(textArg);
        var prunedTextType = Prune(textType);

        if (prunedTextType is TypeRef.TNever)
        {
            return (textTemp, prunedTextType);
        }

        if (prunedTextType is TypeRef.TVar)
        {
            Unify(prunedTextType, new TypeRef.TStr());
            prunedTextType = new TypeRef.TStr();
        }

        if (prunedTextType is not TypeRef.TStr)
        {
            ReportDiagnostic(GetSpan(textArg), $"Ashes.Text.byteLength() expects Str but got {Pretty(prunedTextType)}.");
            return (textTemp, prunedTextType);
        }

        var target = NewTemp();
        Emit(new IrInst.TextByteLength(target, textTemp));
        return (target, new TypeRef.TInt());
    }

    // Ashes.IO.Process.spawn : Str -> List(Str) -> Result(Str, Process)
    private Binding.Intrinsic CreateSpawnProcessBinding()
    {
        return new Binding.Intrinsic(
            IntrinsicKind.SpawnProcess,
            new TypeScheme([], new TypeRef.TFun(new TypeRef.TStr(), new TypeRef.TFun(new TypeRef.TList(new TypeRef.TStr()), CreateStringResultType(_resolvedTypes["Process"]))))
        );
    }

    private (int, TypeRef) LowerSpawnProcess(Expr exeArg, Expr argsArg)
    {
        using var exeSpan = PushDiagnosticSpan(exeArg);
        var (exeTemp, exeType) = LowerExpr(exeArg);
        var prunedExeType = Prune(exeType);

        if (prunedExeType is TypeRef.TNever)
        {
            return (exeTemp, prunedExeType);
        }

        if (prunedExeType is TypeRef.TVar)
        {
            Unify(prunedExeType, new TypeRef.TStr());
            prunedExeType = new TypeRef.TStr();
        }

        if (prunedExeType is not TypeRef.TStr)
        {
            ReportDiagnostic(GetSpan(exeArg), $"Ashes.IO.Process.spawn() expects Str for exe but got {Pretty(prunedExeType)}.");
            return (exeTemp, prunedExeType);
        }

        using var argsSpan = PushDiagnosticSpan(argsArg);
        var (argsTemp, argsType) = LowerExpr(argsArg);
        var prunedArgsType = Prune(argsType);

        if (prunedArgsType is TypeRef.TNever)
        {
            return (argsTemp, prunedArgsType);
        }

        if (prunedArgsType is TypeRef.TVar)
        {
            Unify(prunedArgsType, new TypeRef.TList(new TypeRef.TStr()));
            prunedArgsType = new TypeRef.TList(new TypeRef.TStr());
        }

        if (prunedArgsType is not TypeRef.TList)
        {
            ReportDiagnostic(GetSpan(argsArg), $"Ashes.IO.Process.spawn() expects List(Str) for args but got {Pretty(prunedArgsType)}.");
            return (argsTemp, prunedArgsType);
        }

        var target = NewTemp();
        Emit(new IrInst.SpawnProcess(target, exeTemp, argsTemp));
        return (target, CreateStringResultType(_resolvedTypes["Process"]));
    }

    // Ashes.IO.Process.writeStdin : Process -> Str -> Unit
    private Binding.Intrinsic CreateProcessWriteStdinBinding()
    {
        return new Binding.Intrinsic(
            IntrinsicKind.ProcessWriteStdin,
            new TypeScheme([], new TypeRef.TFun(_resolvedTypes["Process"], new TypeRef.TFun(new TypeRef.TStr(), _resolvedTypes["Unit"])))
        );
    }

    private (int, TypeRef) LowerProcessWriteStdin(Expr procArg, Expr textArg)
    {
        using var procSpan = PushDiagnosticSpan(procArg);
        CheckUseAfterDrop(procArg);
        var (procTemp, procType) = LowerExpr(procArg);
        var prunedProcType = Prune(procType);

        if (prunedProcType is TypeRef.TNever)
        {
            return (procTemp, prunedProcType);
        }

        if (!TryRequireBuiltinNamedType(prunedProcType, "Process", procArg, "Ashes.IO.Process.writeStdin() expects Process."))
        {
            return (procTemp, prunedProcType);
        }

        using var textSpan = PushDiagnosticSpan(textArg);
        var (textTemp, textType) = LowerExpr(textArg);
        var prunedTextType = Prune(textType);

        if (prunedTextType is TypeRef.TNever)
        {
            return (textTemp, prunedTextType);
        }

        if (prunedTextType is TypeRef.TVar)
        {
            Unify(prunedTextType, new TypeRef.TStr());
            prunedTextType = new TypeRef.TStr();
        }

        if (prunedTextType is not TypeRef.TStr)
        {
            ReportDiagnostic(GetSpan(textArg), $"Ashes.IO.Process.writeStdin() expects Str but got {Pretty(prunedTextType)}.");
            return (textTemp, prunedTextType);
        }

        var target = NewTemp();
        Emit(new IrInst.ProcessWriteStdin(target, procTemp, textTemp));
        return (target, _resolvedTypes["Unit"]);
    }

    // Ashes.IO.Process.readStdoutLine : Process -> Maybe(Str)
    private Binding.Intrinsic CreateProcessReadStdoutLineBinding()
    {
        return new Binding.Intrinsic(
            IntrinsicKind.ProcessReadStdoutLine,
            new TypeScheme([], new TypeRef.TFun(_resolvedTypes["Process"], CreateMaybeType(new TypeRef.TStr())))
        );
    }

    private (int, TypeRef) LowerProcessReadStdoutLine(Expr procArg)
    {
        using var procSpan = PushDiagnosticSpan(procArg);
        CheckUseAfterDrop(procArg);
        var (procTemp, procType) = LowerExpr(procArg);
        var prunedProcType = Prune(procType);

        if (prunedProcType is TypeRef.TNever)
        {
            return (procTemp, prunedProcType);
        }

        if (!TryRequireBuiltinNamedType(prunedProcType, "Process", procArg, "Ashes.IO.Process.readStdoutLine() expects Process."))
        {
            return (procTemp, prunedProcType);
        }

        var target = NewTemp();
        Emit(new IrInst.ProcessReadStdoutLine(target, procTemp));
        return (target, CreateMaybeType(new TypeRef.TStr()));
    }

    // Ashes.IO.Process.readStderrLine : Process -> Maybe(Str)
    private Binding.Intrinsic CreateProcessReadStderrLineBinding()
    {
        return new Binding.Intrinsic(
            IntrinsicKind.ProcessReadStderrLine,
            new TypeScheme([], new TypeRef.TFun(_resolvedTypes["Process"], CreateMaybeType(new TypeRef.TStr())))
        );
    }

    private (int, TypeRef) LowerProcessReadStderrLine(Expr procArg)
    {
        using var procSpan = PushDiagnosticSpan(procArg);
        CheckUseAfterDrop(procArg);
        var (procTemp, procType) = LowerExpr(procArg);
        var prunedProcType = Prune(procType);

        if (prunedProcType is TypeRef.TNever)
        {
            return (procTemp, prunedProcType);
        }

        if (!TryRequireBuiltinNamedType(prunedProcType, "Process", procArg, "Ashes.IO.Process.readStderrLine() expects Process."))
        {
            return (procTemp, prunedProcType);
        }

        var target = NewTemp();
        Emit(new IrInst.ProcessReadStderrLine(target, procTemp));
        return (target, CreateMaybeType(new TypeRef.TStr()));
    }

    // Ashes.IO.Process.waitForExit : Process -> Int
    private Binding.Intrinsic CreateProcessWaitForExitBinding()
    {
        return new Binding.Intrinsic(
            IntrinsicKind.ProcessWaitForExit,
            new TypeScheme([], new TypeRef.TFun(_resolvedTypes["Process"], new TypeRef.TInt()))
        );
    }

    private (int, TypeRef) LowerProcessWaitForExit(Expr procArg)
    {
        using var procSpan = PushDiagnosticSpan(procArg);
        CheckUseAfterDrop(procArg);
        var (procTemp, procType) = LowerExpr(procArg);
        var prunedProcType = Prune(procType);

        if (prunedProcType is TypeRef.TNever)
        {
            return (procTemp, prunedProcType);
        }

        if (!TryRequireBuiltinNamedType(prunedProcType, "Process", procArg, "Ashes.IO.Process.waitForExit() expects Process."))
        {
            return (procTemp, prunedProcType);
        }

        var target = NewTemp();
        Emit(new IrInst.ProcessWaitForExit(target, procTemp));
        return (target, new TypeRef.TInt());
    }

    // Ashes.IO.Process.kill : Process -> Unit
    private Binding.Intrinsic CreateProcessKillBinding()
    {
        return new Binding.Intrinsic(
            IntrinsicKind.ProcessKill,
            new TypeScheme([], new TypeRef.TFun(_resolvedTypes["Process"], _resolvedTypes["Unit"]))
        );
    }

    private (int, TypeRef) LowerProcessKill(Expr procArg)
    {
        using var procSpan = PushDiagnosticSpan(procArg);
        CheckUseAfterDrop(procArg);
        var (procTemp, procType) = LowerExpr(procArg);
        var prunedProcType = Prune(procType);

        if (prunedProcType is TypeRef.TNever)
        {
            return (procTemp, prunedProcType);
        }

        if (!TryRequireBuiltinNamedType(prunedProcType, "Process", procArg, "Ashes.IO.Process.kill() expects Process."))
        {
            return (procTemp, prunedProcType);
        }

        var target = NewTemp();
        Emit(new IrInst.ProcessKill(target, procTemp));
        return (target, _resolvedTypes["Unit"]);
    }
}
