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
            ReportDiagnostic(GetSpan(pathArg), $"Ashes.File.readText() expects Str but got {Pretty(loweredType)}.");
            return (pathTemp, loweredType);
        }

        var target = NewTemp();
        Emit(new IrInst.FileReadText(target, pathTemp));
        return (target, CreateStringResultType(new TypeRef.TStr()));
    }

    // Ashes.File.open : Str -> Result(Str, FileHandle)
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
            ReportDiagnostic(GetSpan(pathArg), $"Ashes.File.open() expects Str but got {Pretty(loweredType)}.");
            return (pathTemp, CreateStringResultType(_resolvedTypes["FileHandle"]));
        }

        var target = NewTemp();
        Emit(new IrInst.FileOpen(target, pathTemp));
        return (target, CreateStringResultType(_resolvedTypes["FileHandle"]));
    }

    // Ashes.File.readChunk : FileHandle -> Int -> Result(Str, Str)
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
            ReportDiagnostic(GetSpan(countArg), $"Ashes.File.readChunk() expects Int but got {Pretty(prunedCount)}.");
        }

        var target = NewTemp();
        Emit(new IrInst.FileReadChunk(target, handleTemp, countTemp));
        return (target, CreateStringResultType(new TypeRef.TStr()));
    }

    // Ashes.File.close : FileHandle -> Result(Str, Unit)
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

    // Ashes.Parallel.both : forall a b. (Unit -> a) -> (Unit -> b) -> (a, b)
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
            ReportDiagnostic(GetSpan(pathArg), $"Ashes.File.writeText() expects Str for path but got {Pretty(pathLoweredType)}.");
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
            ReportDiagnostic(GetSpan(textArg), $"Ashes.File.writeText() expects Str for text but got {Pretty(textLoweredType)}.");
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
            ReportDiagnostic(GetSpan(pathArg), $"Ashes.File.exists() expects Str but got {Pretty(loweredType)}.");
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
        Func<IReadOnlyList<int>, int> emitBody)
    {
        _usesAsync = true;

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

        string coroutineLabel = $"coroutine_{_nextLambdaId++}";

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
        _nextLocalSlot = 0;
        _localNames.Clear();
        _localTypes.Clear();

        int stateStructSlot = NewLocal();
        int dummyArgSlot = NewLocal();
        Debug.Assert(stateStructSlot == 0, "State struct slot must be 0");

        _scopes.Clear();
        _scopes.Push(new Dictionary<string, Binding>(StringComparer.Ordinal));
        _ownershipScopes.Clear();
        _ownershipScopes.Push(new Dictionary<string, OwnershipInfo>(StringComparer.Ordinal));
        _arenaWatermarks.Clear();
        _arenaWatermarks.Push((-1, -1));

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

        _inst.Clear();
        _inst.AddRange(savedInst);
        _nextTempSlot = savedTemp;
        _nextLocalSlot = savedLocal;
        _localNames.Clear();
        _localTypes.Clear();
        foreach (var kv in savedLocalNames) _localNames[kv.Key] = kv.Value;
        foreach (var kv in savedLocalTypes) _localTypes[kv.Key] = kv.Value;
        _scopes.Clear();
        foreach (var scope in savedScopes.Reverse())
        {
            _scopes.Push(new Dictionary<string, Binding>(scope, StringComparer.Ordinal));
        }
        _ownershipScopes.Clear();
        foreach (var scope in savedOwnershipScopes.Reverse())
        {
            _ownershipScopes.Push(new Dictionary<string, OwnershipInfo>(scope, StringComparer.Ordinal));
        }
        _arenaWatermarks.Clear();
        foreach (var watermark in savedArenaWatermarks.Reverse())
        {
            _arenaWatermarks.Push(watermark);
        }
        _tcoCtx = savedTcoCtx;

        var taskType = CreateStringTaskType(successType);
        _usesClosures = true;
        int closureTemp = NewTemp();
        Emit(new IrInst.MakeClosure(closureTemp, coroutineLabel, envPtrTemp, captureTemps.Count * 8));
        int taskTemp = NewTemp();
        Emit(new IrInst.CreateTask(taskTemp, closureTemp, transformResult.StateStructSize, captureTemps.Count));
        return (taskTemp, taskType);
    }

    private static bool IsAsyncOnlyNetworkingBuiltin(BuiltinRegistry.BuiltinValueKind kind)
    {
        return kind is BuiltinRegistry.BuiltinValueKind.HttpGet
            or BuiltinRegistry.BuiltinValueKind.HttpPost
            or BuiltinRegistry.BuiltinValueKind.NetTcpConnect
            or BuiltinRegistry.BuiltinValueKind.NetTcpSend
            or BuiltinRegistry.BuiltinValueKind.NetTcpReceive
            or BuiltinRegistry.BuiltinValueKind.NetTcpClose
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
            or IntrinsicKind.NetTlsConnect
            or IntrinsicKind.NetTlsSend
            or IntrinsicKind.NetTlsReceive
            or IntrinsicKind.NetTlsClose;
    }

    private static int GetIntrinsicArity(IntrinsicKind kind) => kind switch
    {
        IntrinsicKind.ParallelBoth => 2,
        IntrinsicKind.FileWriteText => 2,
        IntrinsicKind.FileWriteBytes => 2,
        IntrinsicKind.BytesGet => 2,
        IntrinsicKind.BytesIndexOf => 3,
        IntrinsicKind.BytesSubText => 3,
        IntrinsicKind.BytesAppend => 2,
        IntrinsicKind.BytesAppendByte => 2,
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

    private (int, TypeRef) LowerNetTlsConnect(Expr hostArg, Expr portArg)
    {
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
            ReportDiagnostic(GetSpan(urlArg), $"Ashes.Http.get() expects Str for url but got {Pretty(prunedUrlType)}.");
            return (urlTemp, prunedUrlType);
        }

        var taskTemp = NewTemp();
        Emit(new IrInst.CreateHttpGetTask(taskTemp, urlTemp));
        return (taskTemp, CreateStringTaskType(new TypeRef.TStr()));
    }

    private (int, TypeRef) LowerHttpPost(Expr urlArg, Expr bodyArg)
    {
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
            ReportDiagnostic(GetSpan(urlArg), $"Ashes.Http.post() expects Str for url but got {Pretty(prunedUrlType)}.");
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
            ReportDiagnostic(GetSpan(bodyArg), $"Ashes.Http.post() expects Str for body but got {Pretty(prunedBodyType)}.");
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
    /// Ashes.Async.run(task) — synchronous execution.
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
    /// Ashes.Async.task(value) — creates a pre-completed successful task.
    /// Convenience form of creating a successful task with error type Str.
    /// </summary>
    private (int, TypeRef) LowerAsyncTask(Expr valueArg)
    {
        using var diagnosticSpan = PushDiagnosticSpan(valueArg);
        _usesAsync = true;

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
    /// Ashes.Async.fromResult(result) — creates a pre-completed task.
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
    /// Ashes.Async.sleep(ms) — creates a sleep task.
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
    /// Ashes.Async.all(tasks) — runs all tasks and collects results.
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
    /// Ashes.Async.race(tasks) — runs the first task to completion.
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

    private Binding.Intrinsic CreateTextToHexBinding()
    {
        return new Binding.Intrinsic(
            IntrinsicKind.TextToHex,
            new TypeScheme([], new TypeRef.TFun(new TypeRef.TInt(), new TypeRef.TStr()))
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
            new TypeScheme([], new TypeRef.TFun(new TypeRef.TStr(), CreateStringTaskType(new TypeRef.TStr())))
        );
    }

    private Binding.Intrinsic CreateHttpPostBinding()
    {
        return new Binding.Intrinsic(
            IntrinsicKind.HttpPost,
            new TypeScheme([], new TypeRef.TFun(new TypeRef.TStr(), new TypeRef.TFun(new TypeRef.TStr(), CreateStringTaskType(new TypeRef.TStr()))))
        );
    }

    private Binding.Intrinsic CreateNetTcpConnectBinding()
    {
        return new Binding.Intrinsic(
            IntrinsicKind.NetTcpConnect,
            new TypeScheme([], new TypeRef.TFun(new TypeRef.TStr(), new TypeRef.TFun(new TypeRef.TInt(), CreateStringTaskType(_resolvedTypes["Socket"]))))
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

    private Binding.Intrinsic CreateNetTlsConnectBinding()
    {
        return new Binding.Intrinsic(
            IntrinsicKind.NetTlsConnect,
            new TypeScheme([], new TypeRef.TFun(new TypeRef.TStr(), new TypeRef.TFun(new TypeRef.TInt(), CreateStringTaskType(_resolvedTypes["TlsSocket"]))))
        );
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

    // Ashes.Async.run : Task(E, A) -> Result(E, A)
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

    // Ashes.Async.task : A -> Task(Str, A)
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

    // Ashes.Async.fromResult : Result(E, A) -> Task(E, A)
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

    // Ashes.Async.sleep : Int -> Task(Str, Int)
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

    // Ashes.Async.all : List(Task(E, A)) -> Task(E, List(A))
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

    // Ashes.Async.race : List(Task(E, A)) -> Task(E, A)
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

    // --- Ashes.Bytes lowering methods ---

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
            ReportDiagnostic(GetSpan(byteArg), $"Ashes.Bytes.singleton() expects u8 but got {Pretty(prunedByteType)}.");
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
            ReportDiagnostic(GetSpan(bytesArg), $"Ashes.Bytes.length() expects Bytes but got {Pretty(prunedBytesType)}.");
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
            ReportDiagnostic(GetSpan(bytesArg), $"Ashes.Bytes.get() expects Bytes but got {Pretty(prunedBytesType)}.");
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
            ReportDiagnostic(GetSpan(indexArg), $"Ashes.Bytes.get() expects Int for index but got {Pretty(prunedIndexType)}.");
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
        var (bytesTemp, bytesOk) = LowerBytesArgument(bytesArg, "Ashes.Bytes.indexOf()");
        if (!bytesOk)
        {
            return (bytesTemp, new TypeRef.TInt());
        }

        var (needleTemp, needleOk) = LowerIntArgument(needleArg, "Ashes.Bytes.indexOf() needle");
        if (!needleOk)
        {
            return (needleTemp, new TypeRef.TInt());
        }

        var (fromTemp, fromOk) = LowerIntArgument(fromArg, "Ashes.Bytes.indexOf() from");
        if (!fromOk)
        {
            return (fromTemp, new TypeRef.TInt());
        }

        var target = NewTemp();
        Emit(new IrInst.BytesIndexOf(target, bytesTemp, needleTemp, fromTemp));
        return (target, new TypeRef.TInt());
    }

    private (int, TypeRef) LowerBytesSubText(Expr bytesArg, Expr startArg, Expr lenArg)
    {
        var (bytesTemp, bytesOk) = LowerBytesArgument(bytesArg, "Ashes.Bytes.subText()");
        if (!bytesOk)
        {
            return (bytesTemp, new TypeRef.TStr());
        }

        var (startTemp, startOk) = LowerIntArgument(startArg, "Ashes.Bytes.subText() start");
        if (!startOk)
        {
            return (startTemp, new TypeRef.TStr());
        }

        var (lenTemp, lenOk) = LowerIntArgument(lenArg, "Ashes.Bytes.subText() length");
        if (!lenOk)
        {
            return (lenTemp, new TypeRef.TStr());
        }

        var target = NewTemp();
        Emit(new IrInst.BytesSubText(target, bytesTemp, startTemp, lenTemp));
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
            ReportDiagnostic(GetSpan(leftArg), $"Ashes.Bytes.append() expects Bytes for first argument but got {Pretty(prunedLeftType)}.");
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
            ReportDiagnostic(GetSpan(rightArg), $"Ashes.Bytes.append() expects Bytes for second argument but got {Pretty(prunedRightType)}.");
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
            ReportDiagnostic(GetSpan(bytesArg), $"Ashes.Bytes.appendByte() expects Bytes but got {Pretty(prunedBytesType)}.");
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
            ReportDiagnostic(GetSpan(byteArg), $"Ashes.Bytes.appendByte() expects u8 for byte argument but got {Pretty(prunedByteType)}.");
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
            ReportDiagnostic(GetSpan(listArg), $"Ashes.Bytes.fromList() expects List(u8) but got {Pretty(prunedListType)}.");
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
            ReportDiagnostic(GetSpan(listArg), $"Ashes.Bytes.fromList() expects List(u8) but got {Pretty(prunedListType)}.");
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
            ReportDiagnostic(GetSpan(valueArg), $"Ashes.Bytes.u16Le() expects u16 but got {Pretty(prunedValueType)}.");
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
            ReportDiagnostic(GetSpan(valueArg), $"Ashes.Bytes.u32Le() expects u32 but got {Pretty(prunedValueType)}.");
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
            ReportDiagnostic(GetSpan(valueArg), $"Ashes.Bytes.u64Le() expects u64 but got {Pretty(prunedValueType)}.");
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
            ReportDiagnostic(GetSpan(bytesArg), $"Ashes.Bytes.{name}() expects Bytes but got {Pretty(prunedBytesType)}.");
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
            ReportDiagnostic(GetSpan(offsetArg), $"Ashes.Bytes.{name}() expects Int for offset but got {Pretty(prunedOffsetType)}.");
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
            ReportDiagnostic(GetSpan(pathArg), $"Ashes.File.writeBytes() expects Str for path but got {Pretty(prunedPathType)}.");
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
            ReportDiagnostic(GetSpan(bytesArg), $"Ashes.File.writeBytes() expects Bytes but got {Pretty(prunedBytesType)}.");
            return (bytesTemp, prunedBytesType);
        }

        var target = NewTemp();
        Emit(new IrInst.FileWriteBytes(target, pathTemp, bytesTemp));
        return (target, CreateStringResultType(_resolvedTypes["Unit"]));
    }

    // --- Ashes.Bytes binding factories ---

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

    private Binding.Intrinsic CreateBytesGetBinding()
    {
        return new Binding.Intrinsic(
            IntrinsicKind.BytesGet,
            new TypeScheme([], new TypeRef.TFun(new TypeRef.TBytes(), new TypeRef.TFun(new TypeRef.TInt(), new TypeRef.TUInt(8))))
        );
    }

    // Ashes.Bytes.indexOf : Bytes -> Int -> Int -> Int. Returns the index of the first byte
    // equal to the needle at or after `from`, or -1 if none. O(len - from), no allocation — a
    // memchr for scanning a buffer by integer position without materializing views.
    private Binding.Intrinsic CreateBytesIndexOfBinding()
    {
        return new Binding.Intrinsic(
            IntrinsicKind.BytesIndexOf,
            new TypeScheme([], new TypeRef.TFun(new TypeRef.TBytes(), new TypeRef.TFun(new TypeRef.TInt(), new TypeRef.TFun(new TypeRef.TInt(), new TypeRef.TInt()))))
        );
    }

    // Ashes.Bytes.subText : Bytes -> Int -> Int -> Str. Copies `len` bytes starting at `start`
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

    // Ashes.Bytes.fromText : Str -> Bytes. Str and Bytes share the runtime layout
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
            ReportDiagnostic(GetSpan(textArg), $"Ashes.Bytes.fromText() expects Str but got {Pretty(prunedTextType)}.");
            return (textTemp, new TypeRef.TBytes());
        }

        // Identity: the same heap value is a valid Bytes; only the static type changes.
        return (textTemp, new TypeRef.TBytes());
    }

    // Ashes.Bytes.hash : Bytes -> Int. 64-bit FNV-1a over the bytes. With Ashes.Bytes.fromText
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
            ReportDiagnostic(GetSpan(bytesArg), $"Ashes.Bytes.hash() expects Bytes but got {Pretty(prunedBytesType)}.");
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

    // Ashes.Process.spawn : Str -> List(Str) -> Result(Str, Process)
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
            ReportDiagnostic(GetSpan(exeArg), $"Ashes.Process.spawn() expects Str for exe but got {Pretty(prunedExeType)}.");
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
            ReportDiagnostic(GetSpan(argsArg), $"Ashes.Process.spawn() expects List(Str) for args but got {Pretty(prunedArgsType)}.");
            return (argsTemp, prunedArgsType);
        }

        var target = NewTemp();
        Emit(new IrInst.SpawnProcess(target, exeTemp, argsTemp));
        return (target, CreateStringResultType(_resolvedTypes["Process"]));
    }

    // Ashes.Process.writeStdin : Process -> Str -> Unit
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

        if (!TryRequireBuiltinNamedType(prunedProcType, "Process", procArg, "Ashes.Process.writeStdin() expects Process."))
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
            ReportDiagnostic(GetSpan(textArg), $"Ashes.Process.writeStdin() expects Str but got {Pretty(prunedTextType)}.");
            return (textTemp, prunedTextType);
        }

        var target = NewTemp();
        Emit(new IrInst.ProcessWriteStdin(target, procTemp, textTemp));
        return (target, _resolvedTypes["Unit"]);
    }

    // Ashes.Process.readStdoutLine : Process -> Maybe(Str)
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

        if (!TryRequireBuiltinNamedType(prunedProcType, "Process", procArg, "Ashes.Process.readStdoutLine() expects Process."))
        {
            return (procTemp, prunedProcType);
        }

        var target = NewTemp();
        Emit(new IrInst.ProcessReadStdoutLine(target, procTemp));
        return (target, CreateMaybeType(new TypeRef.TStr()));
    }

    // Ashes.Process.readStderrLine : Process -> Maybe(Str)
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

        if (!TryRequireBuiltinNamedType(prunedProcType, "Process", procArg, "Ashes.Process.readStderrLine() expects Process."))
        {
            return (procTemp, prunedProcType);
        }

        var target = NewTemp();
        Emit(new IrInst.ProcessReadStderrLine(target, procTemp));
        return (target, CreateMaybeType(new TypeRef.TStr()));
    }

    // Ashes.Process.waitForExit : Process -> Int
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

        if (!TryRequireBuiltinNamedType(prunedProcType, "Process", procArg, "Ashes.Process.waitForExit() expects Process."))
        {
            return (procTemp, prunedProcType);
        }

        var target = NewTemp();
        Emit(new IrInst.ProcessWaitForExit(target, procTemp));
        return (target, new TypeRef.TInt());
    }

    // Ashes.Process.kill : Process -> Unit
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

        if (!TryRequireBuiltinNamedType(prunedProcType, "Process", procArg, "Ashes.Process.kill() expects Process."))
        {
            return (procTemp, prunedProcType);
        }

        var target = NewTemp();
        Emit(new IrInst.ProcessKill(target, procTemp));
        return (target, _resolvedTypes["Unit"]);
    }
}
