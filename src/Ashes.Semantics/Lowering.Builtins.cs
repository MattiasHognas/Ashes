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

        if (t is TypeRef.TInt)
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
        var savedTemp = _nextTemp;
        var savedLocal = _nextLocal;
        var savedScopes = _scopes.ToArray();
        var savedOwnershipScopes = _ownershipScopes.ToArray();
        var savedArenaWatermarks = _arenaWatermarks.ToArray();
        var savedTcoCtx = _tcoCtx;
        var savedLocalNames = new Dictionary<int, string>(_localNames);
        var savedLocalTypes = new Dictionary<int, TypeRef>(_localTypes);
        _tcoCtx = null;

        _inst.Clear();
        _nextTemp = 0;
        _nextLocal = 0;
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
            LocalCount: _nextLocal,
            TempCount: Math.Max(_nextTemp, transformResult.MaxTemp + 1),
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
        _nextTemp = savedTemp;
        _nextLocal = savedLocal;
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
        IntrinsicKind.FileWriteText => 2,
        IntrinsicKind.HttpPost => 2,
        IntrinsicKind.NetTcpConnect => 2,
        IntrinsicKind.NetTcpSend => 2,
        IntrinsicKind.NetTcpReceive => 2,
        IntrinsicKind.NetTlsConnect => 2,
        IntrinsicKind.NetTlsSend => 2,
        IntrinsicKind.NetTlsReceive => 2,
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
}
