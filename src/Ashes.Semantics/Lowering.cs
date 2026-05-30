using System.Diagnostics;
using Ashes.Frontend;

namespace Ashes.Semantics;

public sealed partial class Lowering
{
    public readonly record struct HoverTypeInfo(TextSpan Span, string? Name, TypeRef Type);

    private readonly Diagnostics _diag;
    private int _nextTempSlot;
    private int _nextLocalSlot;
    private int _nextTypeVar;
    private int _nextLambdaId;
    private int _nextLabelId;

    private readonly List<IrInst> _inst = new();
    private readonly List<IrFunction> _funcs = new();
    private readonly List<IrStringLiteral> _strings = new();
    private readonly Dictionary<string, string> _stringIntern = new(StringComparer.Ordinal);
    private readonly Dictionary<int, string> _localNames = new();
    private readonly Dictionary<int, TypeRef> _localTypes = new();

    private bool _usesPrintInt;
    private bool _usesPrintStr;
    private bool _usesPrintBool;
    private bool _usesConcatStr;
    private bool _usesClosures;
    private bool _usesAsync;
    private readonly List<HoverTypeInfo> _hoverTypes = [];

    // Source location tracking for debug info
    private string? _currentFilePath;
    private int[]? _lineStarts;
    private int _sourceLength;
    private Expr? _currentSourceExpr;
    private IReadOnlyList<(string FilePath, int StartOffset, int EndOffset)>? _moduleOffsets;
    private int[][]? _moduleLineStarts;

    private readonly bool _hasAshesIO;
    private readonly IReadOnlyDictionary<string, string> _moduleAliases;
    private readonly List<string> _diagnosticContext = [];
    private readonly Stack<TextSpan> _diagnosticSpans = new();
    private readonly Stack<string> _diagnosticCodes = new();





    private TcoContext? _tcoCtx;

    // Async context tracking — true when lowering inside an async block body
    private bool _insideAsync;
    // The error type variable for the current async block; unified from each await's E.
    // Uses save/restore pattern to support nested async blocks.
    private TypeRef? _currentAsyncErrorType;




    private readonly Stack<Dictionary<string, Binding>> _scopes = new();


    // Stack of ownership scopes, parallel to _scopes.
    // Each scope level tracks owned values introduced at that level.
    private readonly Stack<Dictionary<string, OwnershipInfo>> _ownershipScopes = new();

    // Arena watermark local slot pairs (cursor, end) for each ownership scope.
    // SaveArenaState is emitted at scope entry; RestoreArenaState may be emitted
    // at scope exit when the scope's result is a copy type (no heap escapes).
    private readonly Stack<(int CursorSlot, int EndSlot)> _arenaWatermarks = new();

    // Alias map for ownership: when `let y = x` and x is owned, y → x.
    // This prevents double-Drop and propagates diagnostics through aliases.
    // Aliases are resolved transitively (y → x → z chains are followed).
    private readonly Dictionary<string, string> _ownershipAliases = new(StringComparer.Ordinal);

    // Substitution for type variables
    private readonly Dictionary<int, TypeRef> _subst = new();

    // Registered type and constructor symbols
    private readonly Dictionary<string, TypeSymbol> _typeSymbols = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ConstructorSymbol> _constructorSymbols = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TypeRef.TNamedType> _resolvedTypes = new(StringComparer.Ordinal);
    private readonly HashSet<string> _externOpaqueTypes = new(StringComparer.Ordinal);
    private readonly List<IrExternFunction> _externFunctions = new();

    public IReadOnlyDictionary<string, TypeSymbol> TypeSymbols => _typeSymbols;
    public IReadOnlyDictionary<string, ConstructorSymbol> ConstructorSymbols => _constructorSymbols;
    public IReadOnlyDictionary<string, TypeRef.TNamedType> ResolvedTypes => _resolvedTypes;
    public TypeRef? LastLoweredType { get; private set; }

    public HoverTypeInfo? GetTypeAtPosition(int position)
    {
        HoverTypeInfo? best = null;

        foreach (var hover in _hoverTypes)
        {
            if (!ContainsPosition(hover.Span, position))
            {
                continue;
            }

            if (best is null || IsBetterHoverCandidate(hover, best.Value))
            {
                best = hover;
            }
        }

        return best;
    }

    public string FormatType(TypeRef type)
    {
        return Pretty(type);
    }

    public Lowering(Diagnostics diag, IReadOnlySet<string>? importedStdModules = null, IReadOnlyDictionary<string, string>? moduleAliases = null)
    {
        _diag = diag;
        _hasAshesIO = importedStdModules?.Contains("Ashes.IO") == true;
        _moduleAliases = moduleAliases ?? new Dictionary<string, string>(StringComparer.Ordinal);
        RegisterBuiltinSymbols();
        var rootScope = new Dictionary<string, Binding>(StringComparer.Ordinal);
        if (_hasAshesIO)
        {
            AddStdIOBindings(rootScope);
        }
        _scopes.Push(rootScope);
        _ownershipScopes.Push(new Dictionary<string, OwnershipInfo>(StringComparer.Ordinal));
        // Root scope: push sentinel arena watermark (no restore will happen at program exit)
        _arenaWatermarks.Push((-1, -1));
    }





    public IrProgram Lower(Program program)
    {
        RegisterTypeDeclarations(program.TypeDecls);
        RegisterExternDeclarations(program.ExternDecls);
        return Lower(program.Body);
    }







    public IrProgram Lower(Expr expr)
    {
        // Entry function lowering (no env/arg params)
        var (resultTemp, resultType) = LowerExpr(expr);
        LastLoweredType = Prune(resultType);
        Emit(new IrInst.Return(resultTemp));

        var entry = new IrFunction(
            Label: "_start_main",
            Instructions: _inst,
            LocalCount: _nextLocalSlot,
            TempCount: _nextTempSlot,
            HasEnvAndArgParams: false,
            LocalNames: new Dictionary<int, string>(_localNames),
            LocalTypes: SnapshotLocalTypes()
        );

        return new IrProgram(
            EntryFunction: entry,
            Functions: _funcs,
            StringLiterals: _strings,
            ExternFunctions: _externFunctions,
            ExternOpaqueTypes: new HashSet<string>(_externOpaqueTypes, StringComparer.Ordinal),
            UsesPrintInt: _usesPrintInt,
            UsesPrintStr: _usesPrintStr,
            UsesPrintBool: _usesPrintBool,
            UsesConcatStr: _usesConcatStr,
            UsesClosures: _usesClosures,
            UsesAsync: _usesAsync
        );
    }

    private (int Temp, TypeRef Type) LowerExpr(Expr e)
    {
        var previousExpr = _currentSourceExpr;
        _currentSourceExpr = e;

        (int Temp, TypeRef Type) lowered = e switch
        {
            Expr.IntLit lit => LowerInt(lit),
            Expr.FloatLit lit => LowerFloat(lit),
            Expr.StrLit str => LowerStr(str),
            Expr.BoolLit b => LowerBool(b),
            Expr.Var v => LowerVar(v),
            Expr.QualifiedVar qv => LowerQualifiedVar(qv),
            Expr.Add add => LowerAdd(add),
            Expr.Subtract sub => LowerSubtract(sub),
            Expr.Multiply mul => LowerMultiply(mul),
            Expr.Divide div => LowerDivide(div),
            Expr.GreaterOrEqual ge => LowerGreaterOrEqual(ge),
            Expr.LessOrEqual le => LowerLessOrEqual(le),
            Expr.Equal eq => LowerEqual(eq),
            Expr.NotEqual ne => LowerNotEqual(ne),
            Expr.ResultPipe pipe => LowerResultPipe(pipe),
            Expr.ResultMapErrorPipe pipe => LowerResultMapErrorPipe(pipe),
            Expr.Let let => LowerLet(let),
            Expr.LetResult letResult => LowerLetResult(letResult),
            Expr.LetRec letRec => LowerLetRec(letRec),
            Expr.If iff => LowerIf(iff),
            Expr.Lambda lam => LowerLambda(lam),
            Expr.Call call => LowerCall(call),
            Expr.TupleLit tuple => LowerTupleLit(tuple),
            Expr.ListLit list => LowerListLit(list),
            Expr.Cons cons => LowerCons(cons),
            Expr.Match match => LowerMatch(match),
            Expr.Async asyncExpr => LowerAsync(asyncExpr),
            Expr.Await awaitExpr => LowerAwait(awaitExpr),
            _ => throw new NotSupportedException($"Unknown expr: {e.GetType().Name}")
        };

        RecordExprHoverType(e, lowered.Type);
        _currentSourceExpr = previousExpr;
        return (lowered.Temp, Prune(lowered.Type));
    }

    private (int, TypeRef) LowerInt(Expr.IntLit lit)
    {
        int t = NewTemp();
        Emit(new IrInst.LoadConstInt(t, lit.Value));
        return (t, new TypeRef.TInt());
    }

    private (int, TypeRef) LowerFloat(Expr.FloatLit lit)
    {
        int t = NewTemp();
        Emit(new IrInst.LoadConstFloat(t, lit.Value));
        return (t, new TypeRef.TFloat());
    }

    private (int, TypeRef) LowerBool(Expr.BoolLit lit)
    {
        int t = NewTemp();
        Emit(new IrInst.LoadConstBool(t, lit.Value));
        return (t, new TypeRef.TBool());
    }

    private (int, TypeRef) LowerStr(Expr.StrLit str)
    {
        var label = InternString(str.Value);
        int t = NewTemp();
        Emit(new IrInst.LoadConstStr(t, label));
        return (t, new TypeRef.TStr());
    }

    private (int, TypeRef) LowerVar(Expr.Var v)
    {
        var b = Lookup(v.Name);
        if (b is null)
        {
            if (_constructorSymbols.TryGetValue(v.Name, out var ctorSym))
            {
                if (ctorSym.Arity == 0)
                {
                    return LowerNullaryConstructor(ctorSym);
                }

                return LowerExpr(BuildConstructorLambda(ctorSym));
            }

            if (v.Name.Length > 0 && char.IsUpper(v.Name[0]))
            {
                ReportDiagnostic(GetSpan(v), $"Unknown constructor '{v.Name}'.{BuildUnknownConstructorHint(v.Name)}");
            }
            else
            {
                ReportDiagnostic(GetSpan(v), $"Undefined variable '{v.Name}'.{BuildUnknownVariableHint(v.Name)}", DiagnosticCodes.UnknownIdentifier);
            }

            return ReturnNeverWithDummyTemp();
        }

        int temp = NewTemp();
        (int Temp, TypeRef Type) result;

        switch (b)
        {
            case Binding.Local loc:
                Emit(new IrInst.LoadLocal(temp, loc.Slot));
                result = (temp, loc.Type);
                break;

            case Binding.Env env:
                Emit(new IrInst.LoadEnv(temp, env.Index));
                result = (temp, env.Type);
                break;

            case Binding.EnvScheme envSch:
                Emit(new IrInst.LoadEnv(temp, envSch.Index));
                result = (temp, Instantiate(envSch.S));
                break;

            case Binding.Self self:
                int envTemp = NewTemp();
                Emit(new IrInst.LoadLocal(envTemp, 0));
                Emit(new IrInst.MakeClosure(temp, self.FuncLabel, envTemp, self.EnvSizeBytes));
                result = (temp, self.Type);
                break;

            case Binding.Intrinsic intrinsic:
                ReportDiagnostic(GetSpan(v), $"Intrinsic '{v.Name}' must be called directly.");
                Emit(new IrInst.LoadConstInt(temp, 0));
                result = (temp, intrinsic.Type);
                break;

            case Binding.ExternFunction externFunction:
                ReportDiagnostic(GetSpan(v), $"Extern function '{v.Name}' must be called directly.");
                Emit(new IrInst.LoadConstInt(temp, 0));
                result = (temp, externFunction.Type);
                break;

            case Binding.PreludeValue value:
                result = value.Kind switch
                {
                    PreludeValueKind.Args => LowerProgramArgs(temp, Instantiate(value.S)),
                    _ => throw new InvalidOperationException()
                };
                break;

            case Binding.Scheme sch:
                Emit(new IrInst.LoadLocal(temp, sch.Slot));
                result = (temp, Instantiate(sch.S));
                break;

            default:
                throw new InvalidOperationException();
        }

        RecordHoverType(GetSpan(v), v.Name, result.Type);

        // Compiler-inferred borrowing.
        // When an owned binding is accessed, emit a Borrow instruction.
        // This tells the IR that we're taking a non-owning reference — the
        // owning scope is still responsible for the Drop.
        var ownerInfo = LookupOwnedValue(v.Name);
        if (ownerInfo is not null && !ownerInfo.IsDropped)
        {
            int borrowTemp = NewTemp();
            Emit(new IrInst.Borrow(borrowTemp, result.Temp));
            ownerInfo.ActiveBorrows++;
            result = (borrowTemp, result.Type);
        }

        return result;
    }

    private (int, TypeRef) LowerProgramArgs(int target, TypeRef type)
    {
        Emit(new IrInst.LoadProgramArgs(target));
        return (target, type);
    }

    private string ResolveModuleAlias(string moduleName)
    {
        return _moduleAliases.TryGetValue(moduleName, out var resolved) ? resolved : moduleName;
    }

    private (int, TypeRef) LowerQualifiedVar(Expr.QualifiedVar qv)
    {
        var resolvedModule = ResolveModuleAlias(qv.Module);

        if (BuiltinRegistry.TryGetModule(resolvedModule, out var builtinModule))
        {
            if (builtinModule.Members.ContainsKey(qv.Name))
            {
                var resolvedStdMember = ResolveBuiltinModuleMember(builtinModule, qv.Name);
                RecordHoverType(GetSpan(qv), $"{resolvedModule}.{qv.Name}", resolvedStdMember.Item2);
                return resolvedStdMember;
            }

            if (builtinModule.ResourceName is null)
            {
                return StdMemberNotFound(resolvedModule, qv.Name, GetSpan(qv));
            }
        }

        var sanitizedModuleName = ProjectSupport.SanitizeModuleBindingName(resolvedModule);
        var exportedBindingName = $"{sanitizedModuleName}_{qv.Name}";
        if (Lookup(exportedBindingName) is not null)
        {
            var resolvedQualifiedBinding = LowerVar(new Expr.Var(exportedBindingName));
            RecordHoverType(GetSpan(qv), $"{resolvedModule}.{qv.Name}", resolvedQualifiedBinding.Item2);
            return resolvedQualifiedBinding;
        }

        // User module: resolve to the sanitized module binding if it exists.
        var binding = Lookup(resolvedModule) ?? Lookup(sanitizedModuleName);
        if (binding is null)
        {
            ReportDiagnostic(GetSpan(qv), $"Unknown module '{qv.Module}'.");
            return ReturnNeverWithDummyTemp();
        }

        ReportDiagnostic(GetSpan(qv), $"Module '{qv.Module}' does not export '{qv.Name}'.");
        return ReturnNeverWithDummyTemp();
    }

    private (int, TypeRef) ResolveBuiltinModuleMember(BuiltinRegistry.BuiltinModule module, string name)
    {
        var member = module.Members[name];
        return member.Kind switch
        {
            BuiltinRegistry.BuiltinValueKind.Print => LowerQualifiedBuiltinFunctionReference(name, CreatePrintBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.Panic => LowerQualifiedBuiltinFunctionReference(name, CreatePanicBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.Args => LowerProgramArgs(NewTemp(), CreateArgsBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.Write => LowerQualifiedBuiltinFunctionReference(name, CreateWriteBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.WriteLine => LowerQualifiedBuiltinFunctionReference(name, CreateWriteLineBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.ReadLine => LowerQualifiedBuiltinFunctionReference(name, CreateReadLineBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.FileReadText => LowerQualifiedBuiltinFunctionReference(name, CreateFileReadTextBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.FileWriteText => LowerQualifiedBuiltinFunctionReference(name, CreateFileWriteTextBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.FileExists => LowerQualifiedBuiltinFunctionReference(name, CreateFileExistsBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.TextUncons => LowerQualifiedBuiltinFunctionReference(name, CreateTextUnconsBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.TextParseInt => LowerQualifiedBuiltinFunctionReference(name, CreateTextParseIntBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.TextParseFloat => LowerQualifiedBuiltinFunctionReference(name, CreateTextParseFloatBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.HttpGet => LowerQualifiedBuiltinFunctionReference(name, CreateHttpGetBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.HttpPost => LowerQualifiedBuiltinFunctionReference(name, CreateHttpPostBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.NetTcpConnect => LowerQualifiedBuiltinFunctionReference(name, CreateNetTcpConnectBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.NetTcpSend => LowerQualifiedBuiltinFunctionReference(name, CreateNetTcpSendBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.NetTcpReceive => LowerQualifiedBuiltinFunctionReference(name, CreateNetTcpReceiveBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.NetTcpClose => LowerQualifiedBuiltinFunctionReference(name, CreateNetTcpCloseBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.NetTlsConnect => LowerQualifiedBuiltinFunctionReference(name, CreateNetTlsConnectBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.NetTlsSend => LowerQualifiedBuiltinFunctionReference(name, CreateNetTlsSendBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.NetTlsReceive => LowerQualifiedBuiltinFunctionReference(name, CreateNetTlsReceiveBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.NetTlsClose => LowerQualifiedBuiltinFunctionReference(name, CreateNetTlsCloseBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.AsyncRun => LowerQualifiedBuiltinFunctionReference(name, CreateAsyncRunBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.AsyncFromResult => LowerQualifiedBuiltinFunctionReference(name, CreateAsyncFromResultBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.AsyncSleep => LowerQualifiedBuiltinFunctionReference(name, CreateAsyncSleepBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.AsyncAll => LowerQualifiedBuiltinFunctionReference(name, CreateAsyncAllBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.AsyncRace => LowerQualifiedBuiltinFunctionReference(name, CreateAsyncRaceBinding().S.Body),
            _ => StdMemberNotFound(module.Name, name)
        };
    }

    private (int, TypeRef) LowerQualifiedBuiltinFunctionReference(string name, TypeRef type)
    {
        var temp = NewTemp();
        ReportDiagnostic(0, $"Intrinsic '{name}' must be called directly.");
        Emit(new IrInst.LoadConstInt(temp, 0));
        return (temp, type);
    }

    private (int, TypeRef) StdMemberNotFound(string module, string name)
    {
        return StdMemberNotFound(module, name, TextSpan.FromBounds(0, 1));
    }

    private (int, TypeRef) StdMemberNotFound(string module, string name, TextSpan span)
    {
        ReportDiagnostic(span, $"Unknown member '{name}' in module {module}.");
        return ReturnNeverWithDummyTemp();
    }

    private (int, TypeRef) LowerAdd(Expr.Add add)
    {
        using var diagnosticSpan = PushDiagnosticSpan(add);
        var (leftTemp, leftType) = LowerExpr(add.Left);
        var (rightTemp, rightType) = LowerExpr(add.Right);

        var leftPruned = Prune(leftType);
        var rightPruned = Prune(rightType);

        // Resolve type variables: unify with the other side's concrete type, defaulting to Int
        if (leftPruned is TypeRef.TVar)
        {
            var resolved = rightPruned is TypeRef.TStr ? (TypeRef)new TypeRef.TStr() : new TypeRef.TInt();
            Unify(leftPruned, resolved);
            leftPruned = resolved;
        }
        if (rightPruned is TypeRef.TVar)
        {
            var resolved = leftPruned switch
            {
                TypeRef.TStr => (TypeRef)new TypeRef.TStr(),
                TypeRef.TFloat => new TypeRef.TFloat(),
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

        if (leftPruned is TypeRef.TFloat && rightPruned is TypeRef.TFloat)
        {
            int target = NewTemp();
            Emit(new IrInst.AddFloat(target, leftTemp, rightTemp));
            return (target, new TypeRef.TFloat());
        }

        if (leftPruned is TypeRef.TStr && rightPruned is TypeRef.TStr)
        {
            _usesConcatStr = true;
            int target = NewTemp();
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

        return LowerNumericBinaryOp(sub, leftTemp, leftType, rightTemp, rightType, (target, left, right) => new IrInst.SubInt(target, left, right), (target, left, right) => new IrInst.SubFloat(target, left, right), "'-'");
    }

    private (int, TypeRef) LowerMultiply(Expr.Multiply mul)
    {
        using var diagnosticSpan = PushDiagnosticSpan(mul);
        var (leftTemp, leftType) = LowerExpr(mul.Left);
        var (rightTemp, rightType) = LowerExpr(mul.Right);

        return LowerNumericBinaryOp(mul, leftTemp, leftType, rightTemp, rightType, (target, left, right) => new IrInst.MulInt(target, left, right), (target, left, right) => new IrInst.MulFloat(target, left, right), "'*'");
    }

    private (int, TypeRef) LowerDivide(Expr.Divide div)
    {
        using var diagnosticSpan = PushDiagnosticSpan(div);
        var (leftTemp, leftType) = LowerExpr(div.Left);
        var (rightTemp, rightType) = LowerExpr(div.Right);

        return LowerNumericBinaryOp(div, leftTemp, leftType, rightTemp, rightType, (target, left, right) => new IrInst.DivInt(target, left, right), (target, left, right) => new IrInst.DivFloat(target, left, right), "'/'");
    }

    private (int, TypeRef) LowerGreaterOrEqual(Expr.GreaterOrEqual ge)
    {
        using var diagnosticSpan = PushDiagnosticSpan(ge);
        var (leftTemp, leftType) = LowerExpr(ge.Left);
        var (rightTemp, rightType) = LowerExpr(ge.Right);

        return LowerNumericComparisonOp(ge, leftTemp, leftType, rightTemp, rightType, (target, left, right) => new IrInst.CmpIntGe(target, left, right), (target, left, right) => new IrInst.CmpFloatGe(target, left, right), "'>='");
    }

    private (int, TypeRef) LowerLessOrEqual(Expr.LessOrEqual le)
    {
        using var diagnosticSpan = PushDiagnosticSpan(le);
        var (leftTemp, leftType) = LowerExpr(le.Left);
        var (rightTemp, rightType) = LowerExpr(le.Right);

        return LowerNumericComparisonOp(le, leftTemp, leftType, rightTemp, rightType, (target, left, right) => new IrInst.CmpIntLe(target, left, right), (target, left, right) => new IrInst.CmpFloatLe(target, left, right), "'<='");
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

        // Resolve type variables: unify with the other side's concrete type, defaulting to Int
        if (leftPruned is TypeRef.TVar)
        {
            var resolved = rightPruned is TypeRef.TStr ? (TypeRef)new TypeRef.TStr() : new TypeRef.TInt();
            Unify(leftPruned, resolved);
            leftPruned = resolved;
        }
        if (rightPruned is TypeRef.TVar)
        {
            var resolved = leftPruned switch
            {
                TypeRef.TStr => (TypeRef)new TypeRef.TStr(),
                TypeRef.TFloat => new TypeRef.TFloat(),
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
        string op)
    {
        var (resolvedLeft, resolvedRight) = ResolveNumericOperandTypes(leftType, rightType);

        if (resolvedLeft is TypeRef.TInt && resolvedRight is TypeRef.TInt)
        {
            int target = NewTemp();
            Emit(intFactory(target, leftTemp, rightTemp));
            return (target, new TypeRef.TInt());
        }

        if (resolvedLeft is TypeRef.TFloat && resolvedRight is TypeRef.TFloat)
        {
            int target = NewTemp();
            Emit(floatFactory(target, leftTemp, rightTemp));
            return (target, new TypeRef.TFloat());
        }

        var types = PrettyPair(resolvedLeft, resolvedRight);
        ReportDiagnostic(GetSpan(expr), $"{op} requires Int{op}Int or Float{op}Float, got {types.Left} and {types.Right}.", DiagnosticCodes.TypeMismatch);
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
        string op)
    {
        var (resolvedLeft, resolvedRight) = ResolveNumericOperandTypes(leftType, rightType);

        if (resolvedLeft is TypeRef.TInt && resolvedRight is TypeRef.TInt)
        {
            int target = NewTemp();
            Emit(intFactory(target, leftTemp, rightTemp));
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
            var resolved = right is TypeRef.TFloat ? (TypeRef)new TypeRef.TFloat() : new TypeRef.TInt();
            Unify(left, resolved);
            left = resolved;
        }

        if (right is TypeRef.TVar)
        {
            var resolved = left is TypeRef.TFloat ? (TypeRef)new TypeRef.TFloat() : new TypeRef.TInt();
            Unify(right, resolved);
            right = resolved;
        }

        return (left, right);
    }

    private bool TryGetStandardResultParts(out TypeSymbol resultSymbol, out ConstructorSymbol okConstructor, out ConstructorSymbol errorConstructor)
    {
        resultSymbol = null!;
        okConstructor = null!;
        errorConstructor = null!;

        if (!_typeSymbols.TryGetValue("Result", out var resolvedResultSymbol))
        {
            ReportDiagnostic(0, "Result-aware pipeline operators require a type named 'Result' in scope.");
            return false;
        }

        resultSymbol = resolvedResultSymbol;

        if (resultSymbol.TypeParameters.Count != 2)
        {
            ReportDiagnostic(0, "Result-aware pipeline operators require Result to declare exactly two type parameters.");
            return false;
        }

        okConstructor = resultSymbol.Constructors.FirstOrDefault(c => string.Equals(c.Name, "Ok", StringComparison.Ordinal))!;
        errorConstructor = resultSymbol.Constructors.FirstOrDefault(c => string.Equals(c.Name, "Error", StringComparison.Ordinal))!;
        if (okConstructor is null || errorConstructor is null || okConstructor.Arity != 1 || errorConstructor.Arity != 1)
        {
            ReportDiagnostic(0, "Result-aware pipeline operators require Result(E, A) = | Ok(A) | Error(E).");
            return false;
        }

        return true;
    }

    private static bool TryGetResultTypeArgs(TypeRef type, TypeSymbol resultSymbol, out TypeRef errorType, out TypeRef successType)
    {
        if (type is TypeRef.TNamedType named
            && string.Equals(named.Symbol.Name, resultSymbol.Name, StringComparison.Ordinal)
            && named.TypeArgs.Count == 2)
        {
            errorType = named.TypeArgs[0];
            successType = named.TypeArgs[1];
            return true;
        }

        errorType = new TypeRef.TNever();
        successType = new TypeRef.TNever();
        return false;
    }

    private static bool TryBuildMissingResultDiagnostic(TypeRef type, IReadOnlyList<string> missingConstructors, out string diagnostic)
    {
        if (type is TypeRef.TNamedType named
            && string.Equals(named.Symbol.Name, "Result", StringComparison.Ordinal)
            && missingConstructors.Count > 0)
        {
            diagnostic = missingConstructors.Count == 1
                ? $"Non-exhaustive match on Result: missing {missingConstructors[0]}."
                : $"Non-exhaustive match on Result: missing {string.Join(" and ", missingConstructors)}.";
            return true;
        }

        diagnostic = string.Empty;
        return false;
    }

    private bool TryRequireResultType(TypeRef type, TypeSymbol resultSymbol, Expr origin, string diagnosticMessage, out TypeRef errorType, out TypeRef successType)
    {
        var prunedType = Prune(type);
        if (prunedType is TypeRef.TVar)
        {
            errorType = NewTypeVar();
            successType = NewTypeVar();
            var expectedType = new TypeRef.TNamedType(resultSymbol, [errorType, successType]);
            Unify(prunedType, expectedType);
            return TryGetResultTypeArgs(Prune(expectedType), resultSymbol, out errorType, out successType);
        }

        if (TryGetResultTypeArgs(prunedType, resultSymbol, out errorType, out successType))
        {
            return true;
        }

        ReportDiagnostic(GetSpan(origin), $"{diagnosticMessage} Got {Pretty(prunedType)}.");
        errorType = new TypeRef.TNever();
        successType = new TypeRef.TNever();
        return false;
    }

    private int LowerSingleFieldConstructorValue(ConstructorSymbol constructor, int payloadTemp)
    {
        int ptrTemp = NewTemp();
        Emit(new IrInst.AllocAdt(ptrTemp, GetConstructorTag(constructor), constructor.Arity));
        Emit(new IrInst.SetAdtField(ptrTemp, 0, payloadTemp));
        return ptrTemp;
    }

    private (int, TypeRef) LowerLet(Expr.Let let)
    {
        var savedTailPos = _tcoCtx?.InTailPosition ?? false;
        if (_tcoCtx is not null) _tcoCtx.InTailPosition = false;

        // Save the arena watermark before the bound value so allocations from
        // both value and body belong to this let scope.
        EmitArenaWatermark();

        var (valueTemp, valueType) = LowerLetValue(let);

        int slot = NewLocal();
        Emit(new IrInst.StoreLocal(slot, valueTemp));
        RecordLocalDebugInfo(slot, let.Name, valueType);
        var scheme = Generalize(Prune(valueType));
        RecordHoverType(AstSpans.GetLetNameOrDefault(let), let.Name, scheme.Body);

        PushLetScope(let, slot, scheme);
        PushOwnershipScope();
        TrackLetOwnership(let, slot, valueType);

        if (_tcoCtx is not null) _tcoCtx.InTailPosition = savedTailPos;
        var (bodyTemp, bodyType) = LowerExpr(let.Body);

        return PopLetScope(bodyTemp, bodyType);
    }

    private (int Temp, TypeRef Type) LowerLetValue(Expr.Let let)
    {
        var stackAllocateClosure = let.Value is Expr.Lambda && UsesNameOnlyAsDirectCallee(let.Body, let.Name);
        if (stackAllocateClosure && let.Value is Expr.Lambda lambda)
        {
            return LowerLambda(lambda, stackAllocateClosure: true);
        }

        var stackAllocateAdt = IsConstructorExpression(let.Value)
            && IsImmediateSingleArmAdtDestructuringMatch(let.Name, let.Body);
        if (stackAllocateAdt && TryLowerConstructorExpression(let.Value, stackAllocate: true, out var loweredAdt))
        {
            return loweredAdt;
        }

        return LowerExpr(let.Value);
    }

    private void PushLetScope(Expr.Let let, int slot, TypeScheme scheme)
    {
        var parent = _scopes.Peek();
        _scopes.Push(new Dictionary<string, Binding>(parent, StringComparer.Ordinal)
        {
            [let.Name] = new Binding.Scheme(slot, scheme, AstSpans.GetLetNameOrDefault(let))
        });
    }

    private void TrackLetOwnership(Expr.Let let, int slot, TypeRef valueType)
    {
        var prunedValueType = Prune(valueType);
        var ownedTypeName = GetOwnedTypeName(prunedValueType);
        if (ownedTypeName is not null)
        {
            // Alias detection: when `let y = x` and x is already tracked as owned,
            // record y as an alias of x instead of tracking it independently.
            // This prevents double-Drop: only the original owner emits Drop.
            // Only simple Expr.Var references are recognized as aliases. More complex
            // expressions (function calls, constructors, if/match) produce fresh
            // values that are tracked as new owners.
            if (let.Value is Expr.Var aliasSource && LookupOwnedValue(aliasSource.Name) is not null)
            {
                var resolvedSource = ResolveOwnershipAlias(aliasSource.Name);
                _ownershipAliases[let.Name] = resolvedSource;
            }
            else
            {
                var isResource = GetResourceTypeName(prunedValueType) is not null;
                TrackOwnedValue(let.Name, slot, ownedTypeName, isResource, AstSpans.GetLetNameOrDefault(let));
            }
        }
    }

    private (int Temp, TypeRef Type) PopLetScope(int bodyTemp, TypeRef bodyType)
    {
        // Preserve the result only when the scope has drops that could otherwise
        // invalidate or overwrite the temp holding the body result.
        if (HasAliveOwnedValuesInCurrentScope())
        {
            int resultSlot = NewLocal();
            Emit(new IrInst.StoreLocal(resultSlot, bodyTemp));
            int finalTemp = PopOwnershipScope(bodyType, bodyTemp);
            _scopes.Pop();
            if (finalTemp != bodyTemp)
            {
                // Copy-out occurred: finalTemp is the freshly allocated copy.
                return (finalTemp, bodyType);
            }

            int resultTemp = NewTemp();
            Emit(new IrInst.LoadLocal(resultTemp, resultSlot));
            return (resultTemp, bodyType);
        }

        int finalScopeTemp = PopOwnershipScope(bodyType, bodyTemp);
        _scopes.Pop();
        return (finalScopeTemp, bodyType);
    }

    private (int, TypeRef) LowerLetResult(Expr.LetResult letResult)
    {
        using var diagnosticSpan = PushDiagnosticSpan(letResult);
        if (!TryGetStandardResultParts(out var resultSymbol, out var okConstructor, out _))
        {
            return ReturnNeverWithDummyTemp();
        }

        var savedTailPos = _tcoCtx?.InTailPosition ?? false;
        if (_tcoCtx is not null) _tcoCtx.InTailPosition = false;

        var (valueTemp, valueType) = LowerExpr(letResult.Value);
        if (!TryRequireResultType(valueType, resultSymbol, letResult.Value, "let? requires a Result(E, A) expression.", out var errorType, out var successType))
        {
            if (_tcoCtx is not null) _tcoCtx.InTailPosition = savedTailPos;
            return ReturnNeverWithDummyTemp();
        }

        var resultSlot = NewLocal();
        var errorLabel = NewLabel("let_result_error");
        var endLabel = NewLabel("let_result_end");

        var tagTemp = NewTemp();
        var expectedOkTagTemp = NewTemp();
        var isOkTemp = NewTemp();
        Emit(new IrInst.GetAdtTag(tagTemp, valueTemp));
        Emit(new IrInst.LoadConstInt(expectedOkTagTemp, GetConstructorTag(okConstructor)));
        Emit(new IrInst.CmpIntEq(isOkTemp, tagTemp, expectedOkTagTemp));
        Emit(new IrInst.JumpIfFalse(isOkTemp, errorLabel));

        var payloadTemp = NewTemp();
        Emit(new IrInst.GetAdtField(payloadTemp, valueTemp, 0));

        var boundSlot = NewLocal();
        Emit(new IrInst.StoreLocal(boundSlot, payloadTemp));
        RecordLocalDebugInfo(boundSlot, letResult.Name, successType);
        var child = new Dictionary<string, Binding>(_scopes.Peek(), StringComparer.Ordinal)
        {
            [letResult.Name] = new Binding.Local(boundSlot, Prune(successType), AstSpans.GetLetResultNameOrDefault(letResult))
        };
        _scopes.Push(child);
        RecordHoverType(AstSpans.GetLetResultNameOrDefault(letResult), letResult.Name, successType);

        if (_tcoCtx is not null) _tcoCtx.InTailPosition = savedTailPos;
        var (bodyTemp, bodyType) = LowerExpr(letResult.Body);
        _scopes.Pop();
        if (_tcoCtx is not null) _tcoCtx.InTailPosition = false;

        TypeRef resultType;
        if (!TryRequireResultType(bodyType, resultSymbol, letResult.Body, "let? body must produce a Result(E, A) expression.", out var bodyErrorType, out var bodySuccessType))
        {
            resultType = new TypeRef.TNamedType(resultSymbol, [Prune(errorType), NewTypeVar()]);
        }
        else
        {
            Unify(errorType, bodyErrorType);
            resultType = new TypeRef.TNamedType(resultSymbol, [Prune(errorType), Prune(bodySuccessType)]);
        }

        Emit(new IrInst.StoreLocal(resultSlot, bodyTemp));
        Emit(new IrInst.Jump(endLabel));
        Emit(new IrInst.Label(errorLabel));
        Emit(new IrInst.StoreLocal(resultSlot, valueTemp));
        Emit(new IrInst.Label(endLabel));

        var resultTemp = NewTemp();
        Emit(new IrInst.LoadLocal(resultTemp, resultSlot));
        return (resultTemp, Prune(resultType));
    }

    private (int, TypeRef) LowerLetRec(Expr.LetRec letRec)
    {
        int slot = NewLocal();
        var recType = letRec.Value is Expr.Lambda
            ? (TypeRef)new TypeRef.TFun(NewTypeVar(), NewTypeVar())
            : NewTypeVar();
        RecordLocalDebugInfo(slot, letRec.Name, recType);

        var parent = _scopes.Peek();
        var child = new Dictionary<string, Binding>(parent, StringComparer.Ordinal)
        {
            [letRec.Name] = new Binding.Local(slot, recType, AstSpans.GetLetRecNameOrDefault(letRec))
        };
        _scopes.Push(child);

        (int valTemp, TypeRef valType) valueAndType;
        if (letRec.Value is Expr.Lambda lam)
        {
            // Detect lambda chain for TCO: fun (x) -> fun (y) -> body
            var paramCount = CountLambdaChain(lam);
            var innermostBody = GetInnermostBody(lam);
            var hasTailSelfCalls = HasTailSelfCalls(innermostBody, letRec.Name, paramCount);

            var savedTcoCtx = _tcoCtx;
            if (hasTailSelfCalls)
            {
                _tcoCtx = new TcoContext
                {
                    SelfName = letRec.Name,
                    ParamCount = paramCount,
                    ParamNames = CollectLambdaParams(lam),
                    InTailPosition = false
                };
            }
            else
            {
                _tcoCtx = null;
            }

            valueAndType = LowerLambdaRecursive(letRec.Name, recType, lam);

            _tcoCtx = savedTcoCtx;
        }
        else
        {
            ReportDiagnostic(GetSpan(letRec.Value), "let rec currently requires a function value.");
            valueAndType = LowerExpr(letRec.Value);
        }

        Unify(recType, valueAndType.valType);
        RecordHoverType(AstSpans.GetLetRecNameOrDefault(letRec), letRec.Name, recType);
        Emit(new IrInst.StoreLocal(slot, valueAndType.valTemp));

        var (bodyTemp, bodyType) = LowerExpr(letRec.Body);
        _scopes.Pop();
        return (bodyTemp, bodyType);
    }

    private static int CountLambdaChain(Expr.Lambda lam)
    {
        int count = 1;
        var body = lam.Body;
        while (body is Expr.Lambda inner)
        {
            count++;
            body = inner.Body;
        }
        return count;
    }

    private static List<string> CollectLambdaParams(Expr.Lambda lam)
    {
        var names = new List<string> { lam.ParamName };
        var body = lam.Body;
        while (body is Expr.Lambda inner)
        {
            names.Add(inner.ParamName);
            body = inner.Body;
        }
        return names;
    }

    private static Expr GetInnermostBody(Expr.Lambda lam)
    {
        var body = lam.Body;
        while (body is Expr.Lambda inner)
        {
            body = inner.Body;
        }
        return body;
    }

    /// <summary>Check if an expression has any tail-position calls to the named function with the expected arg count.</summary>
    private static bool HasTailSelfCalls(Expr body, string selfName, int paramCount)
    {
        return body switch
        {
            Expr.If iff => HasTailSelfCalls(iff.Then, selfName, paramCount) || HasTailSelfCalls(iff.Else, selfName, paramCount),
            Expr.Match m => m.Cases.Any(c => HasTailSelfCalls(c.Body, selfName, paramCount)),
            Expr.Let l => HasTailSelfCalls(l.Body, selfName, paramCount),
            Expr.LetResult l => HasTailSelfCalls(l.Body, selfName, paramCount),
            Expr.LetRec l => HasTailSelfCalls(l.Body, selfName, paramCount),
            Expr.Call c => IsSelfCallChain(c, selfName, paramCount),
            _ => false
        };
    }

    /// <summary>Check if a call expression is a full self-call chain: f(a1)(a2)...(aN)</summary>
    private static bool IsSelfCallChain(Expr.Call call, string selfName, int expectedArgs)
    {
        var args = new List<Expr>();
        var root = CollectCallArgs(call, args);
        return root is Expr.Var v && string.Equals(v.Name, selfName, StringComparison.Ordinal) && args.Count == expectedArgs;
    }



































    private (int, TypeRef) LowerIf(Expr.If iff)
    {
        using var diagnosticSpan = PushDiagnosticSpan(iff);
        // Condition is NOT in tail position
        var savedTailPos = _tcoCtx?.InTailPosition ?? false;
        if (_tcoCtx is not null) _tcoCtx.InTailPosition = false;

        var (cTemp, cType) = LowerExpr(iff.Cond);
        var ct = Prune(cType);
        Unify(ct, new TypeRef.TBool());

        var elseLabel = NewLabel("else");
        var endLabel = NewLabel("endif");

        Emit(new IrInst.JumpIfFalse(cTemp, elseLabel));

        // Both branches ARE in tail position (if the if itself is)
        if (_tcoCtx is not null) _tcoCtx.InTailPosition = savedTailPos;

        int slot = NewLocal();
        var (tTemp, tType) = LowerExpr(iff.Then);
        var thenType = Prune(tType);
        Emit(new IrInst.StoreLocal(slot, tTemp));

        Emit(new IrInst.Jump(endLabel));
        Emit(new IrInst.Label(elseLabel));

        if (_tcoCtx is not null) _tcoCtx.InTailPosition = savedTailPos;
        var (eTemp, eType) = LowerExpr(iff.Else);
        var elseType = Prune(eType);
        Emit(new IrInst.StoreLocal(slot, eTemp));

        // unify branch types
        using (PushDiagnosticContext("in if branches"))
        {
            Unify(thenType, elseType);
        }

        // if expression result: put into a temp (phi) by storing chosen into target
        int target = NewTemp();
        Emit(new IrInst.Label(endLabel));
        Emit(new IrInst.LoadLocal(target, slot));

        if (_tcoCtx is not null) _tcoCtx.InTailPosition = false;

        var resultType = thenType is TypeRef.TNever ? elseType : thenType;
        return (target, Prune(resultType));
    }

    private (int, TypeRef) LowerLambda(Expr.Lambda lam, bool stackAllocateClosure = false)
    {
        return LowerLambdaCore(lam, null, null, stackAllocateClosure);
    }

    private (int, TypeRef) LowerLambdaRecursive(string selfName, TypeRef selfType, Expr.Lambda lam, bool stackAllocateClosure = false)
    {
        return LowerLambdaCore(lam, selfName, selfType, stackAllocateClosure);
    }

    private (int, TypeRef) LowerLambdaCore(Expr.Lambda lam, string? selfName, TypeRef? selfType, bool stackAllocateClosure)
    {
        _usesClosures = true;

        // Create type variables for param and return
        var paramTy = NewTypeVar();
        var retTy = NewTypeVar();
        var funTy = new TypeRef.TFun(paramTy, retTy);

        // Compute free variables for capture
        var bound = new HashSet<string>(StringComparer.Ordinal) { lam.ParamName };
        if (selfName is not null)
        {
            bound.Add(selfName);
        }

        var free = FreeVars(lam.Body, bound);

        // Remove vars that are not in scope (should not happen if earlier checks)
        var captures = free.Where(n => Lookup(n) is Binding.Local or Binding.Env or Binding.Self or Binding.Scheme).Distinct().ToList();

        // At lambda creation site: allocate env if needed
        int envPtrTemp;
        if (captures.Count == 0)
        {
            envPtrTemp = NewTemp();
            Emit(new IrInst.LoadConstInt(envPtrTemp, 0)); // null env
        }
        else
        {
            // alloc env: captures.Count * 8
            envPtrTemp = NewTemp();
            if (stackAllocateClosure)
            {
                Emit(new IrInst.AllocStack(envPtrTemp, captures.Count * 8));
            }
            else
            {
                Emit(new IrInst.Alloc(envPtrTemp, captures.Count * 8));
            }

            for (int i = 0; i < captures.Count; i++)
            {
                var (capTemp, capTy) = LowerVar(new Expr.Var(captures[i]));
                // store capTemp into [envPtr + i*8]
                Emit(new IrInst.StoreMemOffset(envPtrTemp, i * 8, capTemp));
                // Constrain types: the captured binding type should match capTy; already does.
            }
        }

        // Create lambda function label
        string label = $"lambda_{_nextLambdaId++}";

        // Build function body IR in isolation
        var savedInst = new List<IrInst>(_inst);
        var savedTemp = _nextTempSlot;
        var savedLocal = _nextLocalSlot;
        var savedScopes = _scopes.ToArray();
        var savedLocalNames = new Dictionary<int, string>(_localNames);
        var savedLocalTypes = new Dictionary<int, TypeRef>(_localTypes);

        // new function state
        _inst.Clear();
        _nextTempSlot = 0;
        _nextLocalSlot = 0;
        _localNames.Clear();
        _localTypes.Clear();

        // Lambda function gets implicit locals for env and arg at slots 0 and 1
        int envSlot = NewLocal(); // 0
        int argSlot = NewLocal(); // 1
        RecordLocalDebugInfo(argSlot, lam.ParamName, paramTy);

        // Bind param name as local slot
        var scope = new Dictionary<string, Binding>(StringComparer.Ordinal);
        if (_hasAshesIO)
        {
            AddStdIOBindings(scope);
        }
        var paramSpan = AstSpans.GetLambdaParameterOrDefault(lam);
        RecordHoverType(paramSpan, lam.ParamName, paramTy);
        scope[lam.ParamName] = new Binding.Local(argSlot, paramTy, paramSpan);
        for (int i = 0; i < captures.Count; i++)
        {
            var capBinding = Lookup(captures[i]);
            if (capBinding is null)
            {
                continue;
            }

            if (capBinding is Binding.Scheme capScheme)
            {
                scope[captures[i]] = new Binding.EnvScheme(i, capScheme.S, capScheme.DefinitionSpan);
            }
            else
            {
                scope[captures[i]] = new Binding.Env(i, capBinding.Type, capBinding.DefinitionSpan);
            }
        }
        if (selfName is not null && selfType is not null)
        {
            scope[selfName] = new Binding.Self(label, selfType, captures.Count * 8, Lookup(selfName)?.DefinitionSpan);
        }

        _scopes.Clear();
        _scopes.Push(scope);

        // In function prologue, backend will store RDI(env) to envSlot and RSI(arg) to argSlot.
        // Our LoadEnv instruction implicitly uses envSlot; backend knows envSlot is 0.
        // We'll enforce envSlot==0.
        if (envSlot != 0)
        {
            throw new InvalidOperationException("envSlot must be 0");
        }

        // TCO: For the innermost lambda in a recursive chain, create local copies
        // of captured params and emit a loop start label so tail self-calls can jump back.
        var isInnermostTco = _tcoCtx is not null && lam.Body is not Expr.Lambda;
        if (isInnermostTco)
        {
            var tco = _tcoCtx!;
            tco.ParamSlots.Clear();

            // Only create mutable local copies for captured params that are PART OF
            // the recursive function's lambda chain (not arbitrary outer captures).
            var tcoParamNames = new HashSet<string>(tco.ParamNames, StringComparer.Ordinal);
            tcoParamNames.Remove(lam.ParamName); // the current param is already in argSlot

            for (int i = 0; i < captures.Count; i++)
            {
                var capName = captures[i];
                if (!tcoParamNames.Contains(capName))
                {
                    continue;
                }

                var envIdx = -1;
                foreach (var (name, binding) in scope)
                {
                    if (string.Equals(name, capName, StringComparison.Ordinal) && binding is Binding.Env env)
                    {
                        envIdx = env.Index;
                        break;
                    }
                }
                if (envIdx >= 0)
                {
                    var localSlot = NewLocal();
                    // Load from env into local at function start
                    int loadTemp = NewTemp();
                    Emit(new IrInst.LoadEnv(loadTemp, envIdx));
                    Emit(new IrInst.StoreLocal(localSlot, loadTemp));
                    RecordLocalDebugInfo(localSlot, capName, scope[capName].Type);
                    // Override binding to use local slot
                    scope[capName] = new Binding.Local(localSlot, scope[capName].Type, scope[capName].DefinitionSpan);
                    tco.ParamSlots.Add(localSlot);
                }
            }

            // The arg slot is also a TCO param (last in the chain)
            tco.ParamSlots.Add(argSlot);

            // Emit loop start label
            tco.BodyLabel = $"{label}_body";
            Emit(new IrInst.Label(tco.BodyLabel));

            // Save arena watermark at loop body start so per-iteration heap
            // allocations can be reclaimed before jumping back to the next iteration.
            tco.ArenaCursorSlot = NewLocal();
            tco.ArenaEndSlot = NewLocal();
            Emit(new IrInst.SaveArenaState(tco.ArenaCursorSlot, tco.ArenaEndSlot));

            tco.InTailPosition = true;
        }

        var savedTcoCtx = isInnermostTco ? _tcoCtx : null;
        var (bodyTemp, bodyType) = LowerExpr(lam.Body);
        if (isInnermostTco && savedTcoCtx is not null)
        {
            savedTcoCtx.InTailPosition = false;
        }
        Unify(bodyType, retTy);
        Emit(new IrInst.Return(bodyTemp));

        var func = new IrFunction(
            Label: label,
            Instructions: new List<IrInst>(_inst),
            LocalCount: _nextLocalSlot,
            TempCount: _nextTempSlot,
            HasEnvAndArgParams: true,
            LocalNames: new Dictionary<int, string>(_localNames),
            LocalTypes: SnapshotLocalTypes()
        );

        // restore state
        _funcs.Add(func);

        _inst.Clear();
        _inst.AddRange(savedInst);
        _nextTempSlot = savedTemp;
        _nextLocalSlot = savedLocal;
        _localNames.Clear();
        _localTypes.Clear();
        foreach (var kv in savedLocalNames) _localNames[kv.Key] = kv.Value;
        foreach (var kv in savedLocalTypes) _localTypes[kv.Key] = kv.Value;
        _scopes.Clear();
        foreach (var s in savedScopes.Reverse())
        {
            _scopes.Push(new Dictionary<string, Binding>(s, StringComparer.Ordinal));
        }

        // Produce closure object: alloc 24 bytes and store (code_ptr, env_ptr, env_size)
        int closureTemp = NewTemp();
        int envSizeBytes = captures.Count * 8;
        if (stackAllocateClosure)
        {
            Emit(new IrInst.MakeClosureStack(closureTemp, label, envPtrTemp, envSizeBytes));
        }
        else
        {
            Emit(new IrInst.MakeClosure(closureTemp, label, envPtrTemp, envSizeBytes));
        }
        return (closureTemp, funTy);
    }

    // Walk a left-recursive call chain and collect args in application order.
    // Handles multi-argument constructor calls desugared by the parser:
    //   Pair(1, 2) → Call(Call(Var("Pair"), 1), 2) → root=Var("Pair"), args=[1, 2]
    private static Expr CollectCallArgs(Expr expr, List<Expr> args)
    {
        if (expr is Expr.Call c)
        {
            var root = CollectCallArgs(c.Func, args);
            args.Add(c.Arg);
            return root;
        }

        return expr;
    }

    private bool TryGetExactFunctionArity(TypeRef type, out int arity)
    {
        arity = 0;
        var current = Prune(type);

        while (current is TypeRef.TFun fun)
        {
            arity++;
            current = Prune(fun.Ret);
        }

        if (current is TypeRef.TVar)
        {
            arity = 0;
            return false;
        }

        return true;
    }

    private static string? TryGetCalleeDisplayName(Expr expr)
    {
        return expr switch
        {
            Expr.Var v => v.Name,
            Expr.QualifiedVar qv => $"{qv.Module}.{qv.Name}",
            _ => null
        };
    }


















    private (int, TypeRef) LowerCall(Expr.Call call)
    {
        using var diagnosticSpan = PushDiagnosticSpan(call);
        // Collect all args from the call chain to support multi-arg constructor application:
        //   Pair(1, 2) is parsed as Call(Call(Var("Pair"), 1), 2) — collect [1, 2] with root Var("Pair")
        var collectedArgs = new List<Expr>();
        var rootExpr = CollectCallArgs(call, collectedArgs);
        if (rootExpr is Expr.Var varCtor && _constructorSymbols.TryGetValue(varCtor.Name, out var ctorSym))
        {
            return LowerConstructorApplication(ctorSym, collectedArgs);
        }

        // TCO: detect tail-position self-call chain: f(a1)(a2)...(aN)
        if (_tcoCtx is { InTailPosition: true } tco
            && rootExpr is Expr.Var selfVar
            && string.Equals(selfVar.Name, tco.SelfName, StringComparison.Ordinal)
            && collectedArgs.Count == tco.ParamCount)
        {
            // Evaluate all new arg values first (into temps), BEFORE storing any
            var savedTail = tco.InTailPosition;
            tco.InTailPosition = false;

            var newArgTemps = new int[collectedArgs.Count];
            var newArgTypes = new TypeRef[collectedArgs.Count];
            // Type-check: resolve self binding and unify arg types with param types
            var selfBinding = Lookup(tco.SelfName);
            var curType = selfBinding is not null ? Prune(selfBinding.Type) : null;
            for (int i = 0; i < collectedArgs.Count; i++)
            {
                var (argTemp, argType) = LowerExpr(collectedArgs[i]);
                newArgTemps[i] = argTemp;
                newArgTypes[i] = argType;
                if (curType is TypeRef.TFun funType)
                {
                    Unify(funType.Arg, argType);
                    curType = Prune(funType.Ret);
                }
            }

            // Store new values into TCO param slots
            for (int i = 0; i < tco.ParamSlots.Count; i++)
            {
                Emit(new IrInst.StoreLocal(tco.ParamSlots[i], newArgTemps[i]));
            }

            // Arena reset: restore heap state to loop-iteration watermark before
            // jumping back.
            //
            // All args are copy types (Int, Float, Bool) → plain reset.
            // No heap pointers escape, so reclaiming the iteration's allocations is safe.
            //
            // Some args are heap types but all heap-type args can be copied out
            // (TStr, or TList with copy-type element).  After the reset we copy each such
            // argument out to the fresh watermark position, then overwrite its param slot
            // with the copy pointer.  The previous iteration's cells lie BELOW the saved
            // watermark and are therefore never reclaimed.
            if (tco.ArenaCursorSlot >= 0)
            {
                int tcoPreRestoreEndSlot = NewLocal();

                if (newArgTypes.All(CanArenaReset))
                {
                    // All copy types.
                    Emit(new IrInst.RestoreArenaState(tco.ArenaCursorSlot, tco.ArenaEndSlot, tcoPreRestoreEndSlot));
                    Emit(new IrInst.ReclaimArenaChunks(tco.ArenaEndSlot, tcoPreRestoreEndSlot));
                }
                else
                {
                    // Check whether every heap-type arg can be copy-outed.
                    bool allCopyable = true;
                    for (int i = 0; i < newArgTypes.Length; i++)
                    {
                        if (!CanArenaReset(newArgTypes[i])
                            && GetTcoCopyOutKind(newArgTypes[i], out _, out _) == CopyOutKind.None)
                        {
                            allCopyable = false;
                            break;
                        }
                    }

                    if (allCopyable)
                    {
                        // Emit arena reset (pointer reset only, no chunk freeing), then
                        // copy-out for each heap-type arg (source still readable because
                        // chunks haven't been freed yet), then reclaim abandoned chunks.
                        Emit(new IrInst.RestoreArenaState(tco.ArenaCursorSlot, tco.ArenaEndSlot, tcoPreRestoreEndSlot));
                        for (int i = 0; i < newArgTypes.Length; i++)
                        {
                            if (CanArenaReset(newArgTypes[i]))
                                continue;

                            var kind = GetTcoCopyOutKind(newArgTypes[i], out int sizeBytes, out var headCopy);
                            if (kind == CopyOutKind.None)
                                continue;

                            int copyDest = NewTemp();
                            switch (kind)
                            {
                                case CopyOutKind.Shallow:
                                    Emit(new IrInst.CopyOutArena(copyDest, newArgTemps[i], sizeBytes));
                                    break;
                                case CopyOutKind.List:
                                    Emit(new IrInst.CopyOutList(copyDest, newArgTemps[i], headCopy));
                                    break;
                                case CopyOutKind.Closure:
                                    Emit(new IrInst.CopyOutClosure(copyDest, newArgTemps[i]));
                                    break;
                                case CopyOutKind.TcoListCell:
                                    Emit(new IrInst.CopyOutTcoListCell(copyDest, newArgTemps[i], headCopy));
                                    break;
                            }
                            Emit(new IrInst.StoreLocal(tco.ParamSlots[i], copyDest));
                        }
                        Emit(new IrInst.ReclaimArenaChunks(tco.ArenaEndSlot, tcoPreRestoreEndSlot));
                    }
                    // else: complex heap types — no arena reset.
                }
            }

            // Jump back to loop start
            Emit(new IrInst.Jump(tco.BodyLabel));

            tco.InTailPosition = savedTail;

            // Return a dummy value — this code path won't execute at runtime
            int dummy = NewTemp();
            Emit(new IrInst.LoadConstInt(dummy, 0));
            return (dummy, NewTypeVar());
        }

        if (rootExpr is Expr.Var varFunc && Lookup(varFunc.Name) is Binding.Intrinsic intrinsic)
        {
            int expectedArity = GetIntrinsicArity(intrinsic.Kind);
            if (collectedArgs.Count != expectedArity)
            {
                return ReportArityMismatch(rootExpr, expectedArity, collectedArgs.Count);
            }

            if (!_insideAsync && IsAsyncOnlyNetworkingIntrinsic(intrinsic.Kind))
            {
                ReportDiagnostic(
                    GetSpan(rootExpr),
                    $"'{varFunc.Name}' returns Task and can only be called inside an 'async' block.",
                    DiagnosticCodes.AsyncOnlyNetworkingApi);
                return ReturnNeverWithDummyTemp();
            }

            return intrinsic.Kind switch
            {
                IntrinsicKind.Print => LowerPrint(collectedArgs[0]),
                IntrinsicKind.Write => LowerWrite(collectedArgs[0], appendNewline: false),
                IntrinsicKind.WriteLine => LowerWrite(collectedArgs[0], appendNewline: true),
                IntrinsicKind.ReadLine => LowerReadLine(collectedArgs[0]),
                IntrinsicKind.FileReadText => LowerFileReadText(collectedArgs[0]),
                IntrinsicKind.FileWriteText => LowerFileWriteText(collectedArgs[0], collectedArgs[1]),
                IntrinsicKind.FileExists => LowerFileExists(collectedArgs[0]),
                IntrinsicKind.TextUncons => LowerTextUncons(collectedArgs[0]),
                IntrinsicKind.TextParseInt => LowerTextParseInt(collectedArgs[0]),
                IntrinsicKind.TextParseFloat => LowerTextParseFloat(collectedArgs[0]),
                IntrinsicKind.HttpGet => LowerHttpGet(collectedArgs[0]),
                IntrinsicKind.HttpPost => LowerHttpPost(collectedArgs[0], collectedArgs[1]),
                IntrinsicKind.NetTcpConnect => LowerNetTcpConnect(collectedArgs[0], collectedArgs[1]),
                IntrinsicKind.NetTcpSend => LowerNetTcpSend(collectedArgs[0], collectedArgs[1]),
                IntrinsicKind.NetTcpReceive => LowerNetTcpReceive(collectedArgs[0], collectedArgs[1]),
                IntrinsicKind.NetTcpClose => LowerNetTcpClose(collectedArgs[0]),
                IntrinsicKind.NetTlsConnect => LowerNetTlsConnect(collectedArgs[0], collectedArgs[1]),
                IntrinsicKind.NetTlsSend => LowerNetTlsSend(collectedArgs[0], collectedArgs[1]),
                IntrinsicKind.NetTlsReceive => LowerNetTlsReceive(collectedArgs[0], collectedArgs[1]),
                IntrinsicKind.NetTlsClose => LowerNetTlsClose(collectedArgs[0]),
                IntrinsicKind.Panic => LowerPanic(collectedArgs[0]),
                IntrinsicKind.AsyncRun => LowerAsyncRun(collectedArgs[0]),
                IntrinsicKind.AsyncFromResult => LowerAsyncFromResult(collectedArgs[0]),
                IntrinsicKind.AsyncSleep => LowerAsyncSleep(collectedArgs[0]),
                IntrinsicKind.AsyncAll => LowerAsyncAll(collectedArgs[0]),
                IntrinsicKind.AsyncRace => LowerAsyncRace(collectedArgs[0]),
                _ => throw new NotSupportedException($"Unknown intrinsic: {intrinsic.Kind}")
            };
        }

        if (rootExpr is Expr.Var externVar && Lookup(externVar.Name) is Binding.ExternFunction externFunction)
        {
            return LowerExternCall(rootExpr, externFunction.Function, collectedArgs);
        }

        // Qualified intrinsic call: Ashes.IO.print(...), Ashes.IO.panic(...)
        if (rootExpr is Expr.QualifiedVar qv)
        {
            var resolvedModule = ResolveModuleAlias(qv.Module);
            if (BuiltinRegistry.TryGetModule(resolvedModule, out var builtinModule)
                && builtinModule.Members.TryGetValue(qv.Name, out var builtinMember))
            {
                if (!builtinMember.IsCallable)
                {
                    ReportDiagnostic(GetSpan(qv), $"'{resolvedModule}.{qv.Name}' is not callable.");
                    return ReturnNeverWithDummyTemp();
                }

                if (collectedArgs.Count != builtinMember.Arity)
                {
                    return ReportArityMismatch(rootExpr, builtinMember.Arity, collectedArgs.Count);
                }

                if (!_insideAsync && IsAsyncOnlyNetworkingBuiltin(builtinMember.Kind))
                {
                    ReportDiagnostic(
                        GetSpan(qv),
                        $"'{resolvedModule}.{qv.Name}' returns Task and can only be called inside an 'async' block.",
                        DiagnosticCodes.AsyncOnlyNetworkingApi);
                    return ReturnNeverWithDummyTemp();
                }

                return builtinMember.Kind switch
                {
                    BuiltinRegistry.BuiltinValueKind.Print => LowerPrint(collectedArgs[0]),
                    BuiltinRegistry.BuiltinValueKind.Panic => LowerPanic(collectedArgs[0]),
                    BuiltinRegistry.BuiltinValueKind.Write => LowerWrite(collectedArgs[0], appendNewline: false),
                    BuiltinRegistry.BuiltinValueKind.WriteLine => LowerWrite(collectedArgs[0], appendNewline: true),
                    BuiltinRegistry.BuiltinValueKind.ReadLine => LowerReadLine(collectedArgs[0]),
                    BuiltinRegistry.BuiltinValueKind.FileReadText => LowerFileReadText(collectedArgs[0]),
                    BuiltinRegistry.BuiltinValueKind.FileWriteText => LowerFileWriteText(collectedArgs[0], collectedArgs[1]),
                    BuiltinRegistry.BuiltinValueKind.FileExists => LowerFileExists(collectedArgs[0]),
                    BuiltinRegistry.BuiltinValueKind.TextUncons => LowerTextUncons(collectedArgs[0]),
                    BuiltinRegistry.BuiltinValueKind.TextParseInt => LowerTextParseInt(collectedArgs[0]),
                    BuiltinRegistry.BuiltinValueKind.TextParseFloat => LowerTextParseFloat(collectedArgs[0]),
                    BuiltinRegistry.BuiltinValueKind.HttpGet => LowerHttpGet(collectedArgs[0]),
                    BuiltinRegistry.BuiltinValueKind.HttpPost => LowerHttpPost(collectedArgs[0], collectedArgs[1]),
                    BuiltinRegistry.BuiltinValueKind.NetTcpConnect => LowerNetTcpConnect(collectedArgs[0], collectedArgs[1]),
                    BuiltinRegistry.BuiltinValueKind.NetTcpSend => LowerNetTcpSend(collectedArgs[0], collectedArgs[1]),
                    BuiltinRegistry.BuiltinValueKind.NetTcpReceive => LowerNetTcpReceive(collectedArgs[0], collectedArgs[1]),
                    BuiltinRegistry.BuiltinValueKind.NetTcpClose => LowerNetTcpClose(collectedArgs[0]),
                    BuiltinRegistry.BuiltinValueKind.NetTlsConnect => LowerNetTlsConnect(collectedArgs[0], collectedArgs[1]),
                    BuiltinRegistry.BuiltinValueKind.NetTlsSend => LowerNetTlsSend(collectedArgs[0], collectedArgs[1]),
                    BuiltinRegistry.BuiltinValueKind.NetTlsReceive => LowerNetTlsReceive(collectedArgs[0], collectedArgs[1]),
                    BuiltinRegistry.BuiltinValueKind.NetTlsClose => LowerNetTlsClose(collectedArgs[0]),
                    BuiltinRegistry.BuiltinValueKind.AsyncRun => LowerAsyncRun(collectedArgs[0]),
                    BuiltinRegistry.BuiltinValueKind.AsyncFromResult => LowerAsyncFromResult(collectedArgs[0]),
                    BuiltinRegistry.BuiltinValueKind.AsyncSleep => LowerAsyncSleep(collectedArgs[0]),
                    BuiltinRegistry.BuiltinValueKind.AsyncAll => LowerAsyncAll(collectedArgs[0]),
                    BuiltinRegistry.BuiltinValueKind.AsyncRace => LowerAsyncRace(collectedArgs[0]),
                    _ => StdMemberNotFound(resolvedModule, qv.Name)
                };
            }
        }

        // Per-call arena watermark — save the heap cursor/end before
        // evaluating the callee and arguments so that intermediate allocations
        // (closures from partial application, temporary data structures inside
        // the callee, argument construction) can be reclaimed after the call
        // chain completes.  The watermark is managed independently of the
        // _arenaWatermarks / _ownershipScopes stacks to avoid unbalancing them.
        int callWmCursorSlot = NewLocal();
        int callWmEndSlot = NewLocal();
        Emit(new IrInst.SaveArenaState(callWmCursorSlot, callWmEndSlot));

        // For non-TCO calls, sub-expressions are NOT in tail position
        var savedTailPos = _tcoCtx?.InTailPosition ?? false;
        if (_tcoCtx is not null) _tcoCtx.InTailPosition = false;

        var (currentTemp, currentType) = rootExpr is Expr.Lambda lam
            ? LowerLambda(lam, stackAllocateClosure: true)
            : LowerExpr(rootExpr);

        if (_tcoCtx is not null) _tcoCtx.InTailPosition = savedTailPos;

        currentType = Prune(currentType);
        if (currentType is TypeRef.TNever)
        {
            // Variable already diagnosed as unknown; suppress cascading type error.
            return ReturnNeverWithDummyTemp();
        }

        if (TryGetExactFunctionArity(currentType, out var expectedArgs)
            && expectedArgs > 0
            && expectedArgs < collectedArgs.Count)
        {
            return ReportArityMismatch(rootExpr, expectedArgs, collectedArgs.Count);
        }

        for (int i = 0; i < collectedArgs.Count; i++)
        {
            var (argTemp, argType) = LowerExpr(collectedArgs[i]);
            currentType = Prune(currentType);

            if (currentType is TypeRef.TNever)
            {
                return ReturnNeverWithDummyTemp();
            }

            if (currentType is TypeRef.TVar)
            {
                // Callee type is an unresolved type variable: constrain it to a function type
                // so that the occurs check can fire if the argument is the same variable.
                Unify(currentType, new TypeRef.TFun(NewTypeVar(), NewTypeVar()));
                currentType = Prune(currentType);
            }

            if (currentType is not TypeRef.TFun fun)
            {
                return ReportNonFunctionCall(rootExpr, currentType, i + 1);
            }

            var calleeName = TryGetCalleeDisplayName(rootExpr);
            var callContext = calleeName is not null
                ? $"in argument #{i + 1} of call to '{calleeName}'"
                : $"in argument #{i + 1} of function call";
            using (PushDiagnosticContext(callContext))
            {
                Unify(fun.Arg, argType);
            }

            int target = NewTemp();
            Emit(new IrInst.CallClosure(target, currentTemp, argTemp));
            currentTemp = target;
            currentType = Prune(fun.Ret);
        }

        // Restore arena after the call chain completes.
        // - Copy-type result (Int, Float, Bool): all allocations from the call
        //   chain are unreachable → reclaim via RestoreArenaState + ReclaimArenaChunks.
        // - Self-contained heap result (String, List with safe element, Closure,
        //   ADT with copy-type fields): restore pointer → copy-out → reclaim chunks
        //   (source stays readable until ReclaimArenaChunks frees the old OS chunks).
        var callResultType = Prune(currentType);
        int callPreRestoreEndSlot = NewLocal();
        if (CanArenaReset(callResultType))
        {
            Emit(new IrInst.RestoreArenaState(callWmCursorSlot, callWmEndSlot, callPreRestoreEndSlot));
            Emit(new IrInst.ReclaimArenaChunks(callWmEndSlot, callPreRestoreEndSlot));
        }
        else
        {
            var callCopyOutKind = GetCopyOutKind(callResultType, out int callCopySize);
            if (callCopyOutKind != CopyOutKind.None)
            {
                Emit(new IrInst.RestoreArenaState(callWmCursorSlot, callWmEndSlot, callPreRestoreEndSlot));
                int copyDest = NewTemp();
                switch (callCopyOutKind)
                {
                    case CopyOutKind.Shallow:
                        Emit(new IrInst.CopyOutArena(copyDest, currentTemp, callCopySize));
                        break;
                    case CopyOutKind.List:
                        Emit(new IrInst.CopyOutList(copyDest, currentTemp));
                        break;
                    case CopyOutKind.Closure:
                        Emit(new IrInst.CopyOutClosure(copyDest, currentTemp));
                        break;
                }
                Emit(new IrInst.ReclaimArenaChunks(callWmEndSlot, callPreRestoreEndSlot));
                currentTemp = copyDest;
            }
        }

        return (currentTemp, currentType);
    }

    private (int, TypeRef) LowerExternCall(Expr rootExpr, IrExternFunction externFunction, List<Expr> args)
    {
        if (args.Count != externFunction.ParameterTypes.Count)
        {
            return ReportArityMismatch(rootExpr, externFunction.ParameterTypes.Count, args.Count);
        }

        var loweredArgTemps = new List<int>(args.Count);
        for (int i = 0; i < args.Count; i++)
        {
            var (argTemp, argType) = LowerExpr(args[i]);
            var expectedType = FromFfiType(externFunction.ParameterTypes[i]);
            using (PushDiagnosticContext($"in argument #{i + 1} of extern call to '{externFunction.Name}'"))
            {
                Unify(expectedType, argType);
            }

            if (externFunction.ParameterTypes[i] is FfiType.Str)
            {
                int cStringTemp = NewTemp();
                Emit(new IrInst.ToCString(cStringTemp, argTemp));
                loweredArgTemps.Add(cStringTemp);
            }
            else
            {
                loweredArgTemps.Add(argTemp);
            }
        }

        int target = NewTemp();
        Emit(new IrInst.CallExtern(target, externFunction.SymbolName, externFunction.LibraryName, loweredArgTemps, externFunction.ParameterTypes, externFunction.ReturnType));
        return (target, FromFfiType(externFunction.ReturnType));
    }

    private static TypeRef FromFfiType(FfiType ffiType)
    {
        return ffiType switch
        {
            FfiType.Int => new TypeRef.TInt(),
            FfiType.Float => new TypeRef.TFloat(),
            FfiType.Bool => new TypeRef.TBool(),
            FfiType.Str => new TypeRef.TStr(),
            FfiType.Opaque opaque => new TypeRef.TOpaque(opaque.Name),
            _ => throw new InvalidOperationException($"Unknown FFI type '{ffiType.GetType().Name}'.")
        };
    }







    private (int, TypeRef) LowerListLit(Expr.ListLit list)
    {
        using var diagnosticSpan = PushDiagnosticSpan(list);
        var savedTailPos = _tcoCtx?.InTailPosition ?? false;
        if (_tcoCtx is not null) _tcoCtx.InTailPosition = false;

        var elemType = NewTypeVar();
        var (tailTemp, tailType) = LowerEmptyList();

        for (int i = list.Elements.Count - 1; i >= 0; i--)
        {
            var (headTemp, headType) = LowerExpr(list.Elements[i]);
            using (PushDiagnosticCode(DiagnosticCodes.ListElementTypeMismatch))
            {
                Unify(headType, elemType);
            }
            (tailTemp, tailType) = LowerConsCell(headTemp, tailTemp, elemType, tailType);
        }

        if (_tcoCtx is not null) _tcoCtx.InTailPosition = savedTailPos;

        return (tailTemp, Prune(tailType));
    }

    private (int, TypeRef) LowerTupleLit(Expr.TupleLit tuple)
    {
        var savedTailPos = _tcoCtx?.InTailPosition ?? false;
        if (_tcoCtx is not null) _tcoCtx.InTailPosition = false;

        var elementTypes = new List<TypeRef>(tuple.Elements.Count);
        var elementTemps = new List<int>(tuple.Elements.Count);
        for (int i = 0; i < tuple.Elements.Count; i++)
        {
            var (temp, type) = LowerExpr(tuple.Elements[i]);
            elementTemps.Add(temp);
            elementTypes.Add(type);
        }

        int tupleTemp = NewTemp();
        Emit(new IrInst.Alloc(tupleTemp, tuple.Elements.Count * 8));
        for (int i = 0; i < elementTemps.Count; i++)
        {
            Emit(new IrInst.StoreMemOffset(tupleTemp, i * 8, elementTemps[i]));
        }

        if (_tcoCtx is not null) _tcoCtx.InTailPosition = savedTailPos;

        return (tupleTemp, new TypeRef.TTuple(elementTypes));
    }

    private (int, TypeRef) LowerCons(Expr.Cons cons)
    {
        using var diagnosticSpan = PushDiagnosticSpan(cons);
        var savedTailPos = _tcoCtx?.InTailPosition ?? false;
        if (_tcoCtx is not null) _tcoCtx.InTailPosition = false;

        var (headTemp, headType) = LowerExpr(cons.Head);
        var (tailTemp, tailType) = LowerExpr(cons.Tail);

        if (_tcoCtx is not null) _tcoCtx.InTailPosition = savedTailPos;

        return LowerConsCell(headTemp, tailTemp, headType, tailType);
    }


    private (int, TypeRef) LowerAsync(Expr.Async asyncExpr)
    {
        _usesAsync = true;

        // Lift the async body into a separate coroutine function,
        // then create a task struct pointing to the coroutine.

        var savedInsideAsync = _insideAsync;
        var savedAsyncErrorType = _currentAsyncErrorType;
        _insideAsync = true;

        // The error type variable for this async block — unified from awaits.
        // Each await inside this block unifies its Task(E, A)'s E with this variable.
        var errorTypeVar = NewTypeVar();
        _currentAsyncErrorType = errorTypeVar;

        // --- Capture computation (same as lambda lifting) ---
        var bound = new HashSet<string>(StringComparer.Ordinal);
        var free = FreeVars(asyncExpr.Body, bound);
        var captures = free.Where(n => Lookup(n) is Binding.Local or Binding.Env or Binding.Self or Binding.Scheme)
                           .Distinct().ToList();

        // Module-aliased qualified vars (e.g., list.map where list → Ashes.List)
        // resolve to inlined bindings like Ashes_List_map. These must also be captured
        // because the coroutine scope is isolated from the outer let-binding chain.
        var qualifiedRefs = CollectQualifiedVars(asyncExpr.Body);
        foreach (var qv in qualifiedRefs)
        {
            var resolvedModule = ResolveModuleAlias(qv.Module);
            var isInlinedStdModule = BuiltinRegistry.TryGetModule(resolvedModule, out var bm)
                && bm.ResourceName is not null
                && !bm.Members.ContainsKey(qv.Name);
            if (isInlinedStdModule)
            {
                var exportedBindingName = $"{ProjectSupport.SanitizeModuleBindingName(resolvedModule)}_{qv.Name}";
                if (Lookup(exportedBindingName) is not null && !captures.Contains(exportedBindingName))
                {
                    captures.Add(exportedBindingName);
                }
            }
        }

        // --- Allocate environment for captured variables (in outer context) ---
        int envPtrTemp;
        if (captures.Count == 0)
        {
            envPtrTemp = NewTemp();
            Emit(new IrInst.LoadConstInt(envPtrTemp, 0));
        }
        else
        {
            envPtrTemp = NewTemp();
            Emit(new IrInst.Alloc(envPtrTemp, captures.Count * 8));
            for (int i = 0; i < captures.Count; i++)
            {
                var (capTemp, _) = LowerVar(new Expr.Var(captures[i]));
                Emit(new IrInst.StoreMemOffset(envPtrTemp, i * 8, capTemp));
            }
        }

        // --- Generate coroutine function label ---
        string coroutineLabel = $"coroutine_{_nextLambdaId++}";

        // --- Save outer lowering state ---
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

        // --- Reset state for coroutine function ---
        _inst.Clear();
        _nextTempSlot = 0;
        _nextLocalSlot = 0;
        _localNames.Clear();
        _localTypes.Clear();

        // Coroutine function prologue: local[0] = state struct pointer, local[1] = dummy arg
        int stateStructSlot = NewLocal(); // → local 0
        int dummyArgSlot = NewLocal();    // → local 1
        System.Diagnostics.Debug.Assert(stateStructSlot == 0, "State struct slot must be 0");

        // --- Set up scope for coroutine body ---
        var scope = new Dictionary<string, Binding>(StringComparer.Ordinal);
        if (_hasAshesIO) AddStdIOBindings(scope);

        // Captured variables are accessed via env (state struct captures section)
        for (int i = 0; i < captures.Count; i++)
        {
            var capBinding = Lookup(captures[i])!;
            if (capBinding is Binding.Scheme capScheme)
            {
                scope[captures[i]] = new Binding.EnvScheme(i, capScheme.S, capScheme.DefinitionSpan);
            }
            else
            {
                scope[captures[i]] = new Binding.Env(i, capBinding.Type, capBinding.DefinitionSpan);
            }
        }

        _scopes.Clear();
        _scopes.Push(scope);
        _ownershipScopes.Clear();
        _ownershipScopes.Push(new Dictionary<string, OwnershipInfo>(StringComparer.Ordinal));
        // Coroutine root scope: push sentinel arena watermark
        _arenaWatermarks.Clear();
        _arenaWatermarks.Push((-1, -1));

        // --- Lower the async body ---
        var (bodyTemp, bodyType) = LowerExpr(asyncExpr.Body);
        if (!TryGetStandardResultParts(out _, out var okConstructor, out _))
        {
            return ReturnNeverWithDummyTemp();
        }

        int okResultTemp = LowerSingleFieldConstructorValue(okConstructor, bodyTemp);
        Emit(new IrInst.Return(okResultTemp));

        // --- Apply state machine transform ---
        var transformResult = StateMachineTransform.Transform(_inst, captures.Count);

        // --- Create coroutine IrFunction ---
        var coroutineFunc = new IrFunction(
            Label: coroutineLabel,
            Instructions: new List<IrInst>(transformResult.Instructions),
            LocalCount: _nextLocalSlot,
            TempCount: Math.Max(_nextTempSlot, transformResult.MaxTemp + 1),
            HasEnvAndArgParams: true,
            Coroutine: new CoroutineInfo(
                StateCount: transformResult.StateCount,
                StateStructSize: transformResult.StateStructSize,
                CaptureCount: captures.Count
            ),
            LocalNames: new Dictionary<int, string>(_localNames),
            LocalTypes: SnapshotLocalTypes()
        );
        _funcs.Add(coroutineFunc);

        // --- Restore outer state ---
        _inst.Clear();
        _inst.AddRange(savedInst);
        _nextTempSlot = savedTemp;
        _nextLocalSlot = savedLocal;
        _localNames.Clear();
        _localTypes.Clear();
        foreach (var kv in savedLocalNames) _localNames[kv.Key] = kv.Value;
        foreach (var kv in savedLocalTypes) _localTypes[kv.Key] = kv.Value;
        _scopes.Clear();
        foreach (var s in savedScopes.Reverse())
        {
            _scopes.Push(new Dictionary<string, Binding>(s, StringComparer.Ordinal));
        }
        _ownershipScopes.Clear();
        foreach (var s in savedOwnershipScopes.Reverse())
        {
            _ownershipScopes.Push(new Dictionary<string, OwnershipInfo>(s, StringComparer.Ordinal));
        }
        _arenaWatermarks.Clear();
        foreach (var w in savedArenaWatermarks.Reverse())
        {
            _arenaWatermarks.Push(w);
        }
        _tcoCtx = savedTcoCtx;
        _insideAsync = savedInsideAsync;
        _currentAsyncErrorType = savedAsyncErrorType;

        // --- Build Task(E, A) type ---
        if (!_typeSymbols.TryGetValue("Task", out var taskSymbol))
        {
            ReportDiagnostic(GetSpan(asyncExpr), "Internal error: Task type not registered.");
            return ReturnNeverWithDummyTemp();
        }

        var taskType = new TypeRef.TNamedType(taskSymbol, [errorTypeVar, bodyType]);

        // --- Emit CreateTask in outer context ---
        // Build a closure pairing the coroutine function with its captured env,
        // then wrap it in a task struct via CreateTask.
        _usesClosures = true;
        int closureTemp = NewTemp();
        Emit(new IrInst.MakeClosure(closureTemp, coroutineLabel, envPtrTemp, captures.Count * 8));
        int taskTemp = NewTemp();
        Emit(new IrInst.CreateTask(taskTemp, closureTemp, transformResult.StateStructSize, captures.Count));
        return (taskTemp, taskType);
    }

    private (int, TypeRef) LowerAwait(Expr.Await awaitExpr)
    {
        if (!_insideAsync)
        {
            ReportDiagnostic(GetSpan(awaitExpr), "'await' can only be used inside an 'async' block.", DiagnosticCodes.AwaitOutsideAsync);
            return ReturnNeverWithDummyTemp();
        }

        var (taskTemp, taskType) = LowerExpr(awaitExpr.Task);

        // Verify the operand is a Task(E, A)
        if (!_typeSymbols.TryGetValue("Task", out var taskSymbol)
            || !TryGetStandardResultParts(out _, out var okConstructor, out _))
        {
            ReportDiagnostic(GetSpan(awaitExpr), "Internal error: Task or Result type not registered.");
            return ReturnNeverWithDummyTemp();
        }

        var errorType = NewTypeVar();
        var successType = NewTypeVar();
        var expectedType = new TypeRef.TNamedType(taskSymbol, [errorType, successType]);
        Unify(taskType, expectedType);

        // Unify the awaited task's error type with the enclosing async block's error type.
        // This ensures all awaits within the same async block share a consistent error type.
        if (_currentAsyncErrorType is not null)
        {
            Unify(errorType, _currentAsyncErrorType);
        }

        // AwaitTask yields the underlying Result(E, A).
        int resultTemp = NewTemp();
        Emit(new IrInst.AwaitTask(resultTemp, taskTemp));

        int tagTemp = NewTemp();
        int expectedOkTagTemp = NewTemp();
        int isOkTemp = NewTemp();
        Emit(new IrInst.GetAdtTag(tagTemp, resultTemp));
        Emit(new IrInst.LoadConstInt(expectedOkTagTemp, GetConstructorTag(okConstructor)));
        Emit(new IrInst.CmpIntEq(isOkTemp, tagTemp, expectedOkTagTemp));

        string errorLabel = NewLabel("await_error");
        string endLabel = NewLabel("await_ok");
        int payloadSlot = NewLocal();

        Emit(new IrInst.JumpIfFalse(isOkTemp, errorLabel));
        int payloadTemp = NewTemp();
        Emit(new IrInst.GetAdtField(payloadTemp, resultTemp, 0));
        Emit(new IrInst.StoreLocal(payloadSlot, payloadTemp));
        Emit(new IrInst.Jump(endLabel));

        Emit(new IrInst.Label(errorLabel));
        Emit(new IrInst.Return(resultTemp));

        Emit(new IrInst.Label(endLabel));
        int finalTemp = NewTemp();
        Emit(new IrInst.LoadLocal(finalTemp, payloadSlot));
        return (finalTemp, Prune(successType));
    }

























































    private int NewTemp()
    {
        return _nextTempSlot++;
    }

    private int NewLocal()
    {
        return _nextLocalSlot++;
    }

    private void RecordLocalDebugInfo(int slot, string name, TypeRef type)
    {
        _localNames[slot] = name;
        _localTypes[slot] = type;
    }

    private Dictionary<int, TypeRef> SnapshotLocalTypes()
    {
        var snapshot = new Dictionary<int, TypeRef>(_localTypes.Count);
        foreach (var (slot, type) in _localTypes)
        {
            snapshot[slot] = Prune(type);
        }

        return snapshot;
    }

    private string NewLabel(string prefix)
    {
        return $"{prefix}_{_nextLabelId++}";
    }

    private string InternString(string value)
    {
        if (_stringIntern.TryGetValue(value, out var existing))
        {
            return existing;
        }

        var label = $"str_{_strings.Count}";
        _strings.Add(new IrStringLiteral(label, value));
        _stringIntern[value] = label;
        return label;
    }

    private static HashSet<string> FreeVars(Expr e, HashSet<string> bound)
    {
        var res = new HashSet<string>(StringComparer.Ordinal);

        void Visit(Expr ex, HashSet<string> bnd)
        {
            switch (ex)
            {
                case Expr.IntLit:
                case Expr.FloatLit:
                case Expr.StrLit:
                case Expr.BoolLit:
                    return;
                case Expr.Var v:
                    if (!bnd.Contains(v.Name))
                    {
                        res.Add(v.Name);
                    }

                    return;
                case Expr.QualifiedVar qv:
                    // Ashes module references don't introduce free variables
                    if (!string.Equals(qv.Module, "Ashes", StringComparison.Ordinal)
                        && !qv.Module.StartsWith("Ashes.", StringComparison.Ordinal)
                        && !bnd.Contains(qv.Module))
                    {
                        res.Add(qv.Module);
                    }

                    return;
                case Expr.Add a:
                    Visit(a.Left, bnd);
                    Visit(a.Right, bnd);
                    return;
                case Expr.Subtract sub:
                    Visit(sub.Left, bnd);
                    Visit(sub.Right, bnd);
                    return;
                case Expr.Multiply mul:
                    Visit(mul.Left, bnd);
                    Visit(mul.Right, bnd);
                    return;
                case Expr.Divide div:
                    Visit(div.Left, bnd);
                    Visit(div.Right, bnd);
                    return;
                case Expr.GreaterOrEqual ge:
                    Visit(ge.Left, bnd);
                    Visit(ge.Right, bnd);
                    return;
                case Expr.LessOrEqual le:
                    Visit(le.Left, bnd);
                    Visit(le.Right, bnd);
                    return;
                case Expr.Equal eq:
                    Visit(eq.Left, bnd);
                    Visit(eq.Right, bnd);
                    return;
                case Expr.NotEqual ne:
                    Visit(ne.Left, bnd);
                    Visit(ne.Right, bnd);
                    return;
                case Expr.ResultPipe pipe:
                    Visit(pipe.Left, bnd);
                    Visit(pipe.Right, bnd);
                    return;
                case Expr.ResultMapErrorPipe pipe:
                    Visit(pipe.Left, bnd);
                    Visit(pipe.Right, bnd);
                    return;
                case Expr.Call c:
                    Visit(c.Func, bnd);
                    Visit(c.Arg, bnd);
                    return;
                case Expr.TupleLit tuple:
                    foreach (var elem in tuple.Elements)
                    {
                        Visit(elem, bnd);
                    }
                    return;
                case Expr.ListLit list:
                    foreach (var e in list.Elements)
                    {
                        Visit(e, bnd);
                    }

                    return;
                case Expr.Cons c:
                    Visit(c.Head, bnd);
                    Visit(c.Tail, bnd);
                    return;
                case Expr.Match m:
                    Visit(m.Value, bnd);
                    foreach (var mc in m.Cases)
                    {
                        var bndCase = new HashSet<string>(bnd, StringComparer.Ordinal);
                        foreach (var name in PatternBindings(mc.Pattern))
                        {
                            bndCase.Add(name);
                        }

                        if (mc.Guard is not null)
                        {
                            Visit(mc.Guard, bndCase);
                        }
                        Visit(mc.Body, bndCase);
                    }
                    return;
                case Expr.If iff:
                    Visit(iff.Cond, bnd);
                    Visit(iff.Then, bnd);
                    Visit(iff.Else, bnd);
                    return;
                case Expr.Let l:
                    Visit(l.Value, bnd);
                    var boundWithLetVar = new HashSet<string>(bnd, StringComparer.Ordinal) { l.Name };
                    Visit(l.Body, boundWithLetVar);
                    return;
                case Expr.LetResult l:
                    Visit(l.Value, bnd);
                    var boundWithResultVar = new HashSet<string>(bnd, StringComparer.Ordinal) { l.Name };
                    Visit(l.Body, boundWithResultVar);
                    return;
                case Expr.LetRec l:
                    var boundWithRecVar = new HashSet<string>(bnd, StringComparer.Ordinal) { l.Name };
                    Visit(l.Value, boundWithRecVar);
                    Visit(l.Body, boundWithRecVar);
                    return;
                case Expr.Lambda lam:
                    var boundWithParam = new HashSet<string>(bnd, StringComparer.Ordinal) { lam.ParamName };
                    Visit(lam.Body, boundWithParam);
                    return;
                case Expr.Async asyncExpr:
                    Visit(asyncExpr.Body, bnd);
                    return;
                case Expr.Await awaitExpr:
                    Visit(awaitExpr.Task, bnd);
                    return;
                default:
                    throw new NotSupportedException(ex.GetType().Name);
            }
        }

        Visit(e, bound);
        return res;
    }

    /// <summary>
    /// Collects all <see cref="Expr.QualifiedVar"/> references from an expression tree.
    /// Used by <see cref="LowerAsync"/> to discover module-aliased references
    /// (e.g., <c>list.map</c>) that resolve to inlined std library bindings.
    /// </summary>
    private static List<Expr.QualifiedVar> CollectQualifiedVars(Expr e)
    {
        var result = new List<Expr.QualifiedVar>();
        CollectQualifiedVarsVisit(e, result);
        return result;
    }

    private static void CollectQualifiedVarsVisit(Expr e, List<Expr.QualifiedVar> result)
    {
        switch (e)
        {
            case Expr.QualifiedVar qv:
                result.Add(qv);
                break;
            case Expr.Call c:
                CollectQualifiedVarsVisit(c.Func, result);
                CollectQualifiedVarsVisit(c.Arg, result);
                break;
            case Expr.Let l:
                CollectQualifiedVarsVisit(l.Value, result);
                CollectQualifiedVarsVisit(l.Body, result);
                break;
            case Expr.LetResult l:
                CollectQualifiedVarsVisit(l.Value, result);
                CollectQualifiedVarsVisit(l.Body, result);
                break;
            case Expr.LetRec l:
                CollectQualifiedVarsVisit(l.Value, result);
                CollectQualifiedVarsVisit(l.Body, result);
                break;
            case Expr.If iff:
                CollectQualifiedVarsVisit(iff.Cond, result);
                CollectQualifiedVarsVisit(iff.Then, result);
                CollectQualifiedVarsVisit(iff.Else, result);
                break;
            case Expr.Lambda lam:
                CollectQualifiedVarsVisit(lam.Body, result);
                break;
            case Expr.Match m:
                CollectQualifiedVarsVisit(m.Value, result);
                foreach (var mc in m.Cases)
                {
                    if (mc.Guard is not null)
                        CollectQualifiedVarsVisit(mc.Guard, result);
                    CollectQualifiedVarsVisit(mc.Body, result);
                }
                break;
            case Expr.Add a:
                CollectQualifiedVarsVisit(a.Left, result);
                CollectQualifiedVarsVisit(a.Right, result);
                break;
            case Expr.Subtract s:
                CollectQualifiedVarsVisit(s.Left, result);
                CollectQualifiedVarsVisit(s.Right, result);
                break;
            case Expr.Multiply m:
                CollectQualifiedVarsVisit(m.Left, result);
                CollectQualifiedVarsVisit(m.Right, result);
                break;
            case Expr.Divide d:
                CollectQualifiedVarsVisit(d.Left, result);
                CollectQualifiedVarsVisit(d.Right, result);
                break;
            case Expr.GreaterOrEqual ge:
                CollectQualifiedVarsVisit(ge.Left, result);
                CollectQualifiedVarsVisit(ge.Right, result);
                break;
            case Expr.LessOrEqual le:
                CollectQualifiedVarsVisit(le.Left, result);
                CollectQualifiedVarsVisit(le.Right, result);
                break;
            case Expr.Equal eq:
                CollectQualifiedVarsVisit(eq.Left, result);
                CollectQualifiedVarsVisit(eq.Right, result);
                break;
            case Expr.NotEqual ne:
                CollectQualifiedVarsVisit(ne.Left, result);
                CollectQualifiedVarsVisit(ne.Right, result);
                break;
            case Expr.ResultPipe pipe:
                CollectQualifiedVarsVisit(pipe.Left, result);
                CollectQualifiedVarsVisit(pipe.Right, result);
                break;
            case Expr.ResultMapErrorPipe pipe:
                CollectQualifiedVarsVisit(pipe.Left, result);
                CollectQualifiedVarsVisit(pipe.Right, result);
                break;
            case Expr.TupleLit tuple:
                foreach (var elem in tuple.Elements)
                    CollectQualifiedVarsVisit(elem, result);
                break;
            case Expr.ListLit list:
                foreach (var elem in list.Elements)
                    CollectQualifiedVarsVisit(elem, result);
                break;
            case Expr.Cons cons:
                CollectQualifiedVarsVisit(cons.Head, result);
                CollectQualifiedVarsVisit(cons.Tail, result);
                break;
            case Expr.Async asyncExpr:
                CollectQualifiedVarsVisit(asyncExpr.Body, result);
                break;
            case Expr.Await awaitExpr:
                CollectQualifiedVarsVisit(awaitExpr.Task, result);
                break;
            default:
                // Literals, Var, etc. - no qualified vars to collect
                break;
        }
    }

    private bool TryLowerConstructorExpression(Expr expr, bool stackAllocate, out (int Temp, TypeRef Type) lowered)
    {
        if (expr is Expr.Var varCtor && _constructorSymbols.TryGetValue(varCtor.Name, out var nullaryCtor) && nullaryCtor.Arity == 0)
        {
            lowered = LowerNullaryConstructor(nullaryCtor, stackAllocate);
            return true;
        }

        var args = new List<Expr>();
        var rootExpr = CollectCallArgs(expr, args);
        if (rootExpr is Expr.Var callCtor && _constructorSymbols.TryGetValue(callCtor.Name, out var ctor))
        {
            lowered = LowerConstructorApplication(ctor, args, stackAllocate);
            return true;
        }

        lowered = default;
        return false;
    }

    private bool IsConstructorExpression(Expr expr)
    {
        if (expr is Expr.Var varCtor && _constructorSymbols.TryGetValue(varCtor.Name, out var nullaryCtor) && nullaryCtor.Arity == 0)
        {
            return true;
        }

        var args = new List<Expr>();
        var rootExpr = CollectCallArgs(expr, args);
        return rootExpr is Expr.Var callCtor && _constructorSymbols.TryGetValue(callCtor.Name, out _);
    }

    private static bool ShouldStackAllocateImmediateMatchScrutinee(Expr.Match match)
    {
        return match.Cases.Count == 1 && match.Cases[0].Pattern is Pattern.Constructor;
    }

    private static bool IsImmediateSingleArmAdtDestructuringMatch(string name, Expr body)
    {
        if (body is not Expr.Match(Expr.Var varExpr, var cases, _) || !string.Equals(varExpr.Name, name, StringComparison.Ordinal))
        {
            return false;
        }

        if (cases.Count != 1 || cases[0].Pattern is not Pattern.Constructor)
        {
            return false;
        }

        bool shadowedInArm = PatternBindings(cases[0].Pattern).Any(boundName => string.Equals(boundName, name, StringComparison.Ordinal));
        var guard = cases[0].Guard;
        return (guard is null || !ExprReferencesName(guard, name, shadowedInArm))
            && !ExprReferencesName(cases[0].Body, name, shadowedInArm);
    }

    private static bool UsesNameOnlyAsDirectCallee(Expr expr, string targetName, bool shadowed = false, bool allowDirectCallee = false)
    {
        switch (expr)
        {
            case Expr.IntLit:
            case Expr.FloatLit:
            case Expr.StrLit:
            case Expr.BoolLit:
            case Expr.QualifiedVar:
                return true;

            case Expr.Var v:
                return shadowed || !string.Equals(v.Name, targetName, StringComparison.Ordinal) || allowDirectCallee;

            case Expr.Add add:
                return UsesNameOnlyAsDirectCallee(add.Left, targetName, shadowed)
                    && UsesNameOnlyAsDirectCallee(add.Right, targetName, shadowed);
            case Expr.Subtract sub:
                return UsesNameOnlyAsDirectCallee(sub.Left, targetName, shadowed)
                    && UsesNameOnlyAsDirectCallee(sub.Right, targetName, shadowed);
            case Expr.Multiply mul:
                return UsesNameOnlyAsDirectCallee(mul.Left, targetName, shadowed)
                    && UsesNameOnlyAsDirectCallee(mul.Right, targetName, shadowed);
            case Expr.Divide div:
                return UsesNameOnlyAsDirectCallee(div.Left, targetName, shadowed)
                    && UsesNameOnlyAsDirectCallee(div.Right, targetName, shadowed);
            case Expr.GreaterOrEqual ge:
                return UsesNameOnlyAsDirectCallee(ge.Left, targetName, shadowed)
                    && UsesNameOnlyAsDirectCallee(ge.Right, targetName, shadowed);
            case Expr.LessOrEqual le:
                return UsesNameOnlyAsDirectCallee(le.Left, targetName, shadowed)
                    && UsesNameOnlyAsDirectCallee(le.Right, targetName, shadowed);
            case Expr.Equal eq:
                return UsesNameOnlyAsDirectCallee(eq.Left, targetName, shadowed)
                    && UsesNameOnlyAsDirectCallee(eq.Right, targetName, shadowed);
            case Expr.NotEqual ne:
                return UsesNameOnlyAsDirectCallee(ne.Left, targetName, shadowed)
                    && UsesNameOnlyAsDirectCallee(ne.Right, targetName, shadowed);
            case Expr.ResultPipe pipe:
                return UsesNameOnlyAsDirectCallee(pipe.Left, targetName, shadowed)
                    && UsesNameOnlyAsDirectCallee(pipe.Right, targetName, shadowed);
            case Expr.ResultMapErrorPipe pipe:
                return UsesNameOnlyAsDirectCallee(pipe.Left, targetName, shadowed)
                    && UsesNameOnlyAsDirectCallee(pipe.Right, targetName, shadowed);
            case Expr.Call call:
                return UsesNameOnlyAsDirectCallee(call.Func, targetName, shadowed, allowDirectCallee: true)
                    && UsesNameOnlyAsDirectCallee(call.Arg, targetName, shadowed);
            case Expr.TupleLit tuple:
                return tuple.Elements.All(elem => UsesNameOnlyAsDirectCallee(elem, targetName, shadowed));
            case Expr.ListLit list:
                return list.Elements.All(elem => UsesNameOnlyAsDirectCallee(elem, targetName, shadowed));
            case Expr.Cons cons:
                return UsesNameOnlyAsDirectCallee(cons.Head, targetName, shadowed)
                    && UsesNameOnlyAsDirectCallee(cons.Tail, targetName, shadowed);
            case Expr.If iff:
                return UsesNameOnlyAsDirectCallee(iff.Cond, targetName, shadowed)
                    && UsesNameOnlyAsDirectCallee(iff.Then, targetName, shadowed)
                    && UsesNameOnlyAsDirectCallee(iff.Else, targetName, shadowed);
            case Expr.Let let:
                return UsesNameOnlyAsDirectCallee(let.Value, targetName, shadowed)
                    && UsesNameOnlyAsDirectCallee(let.Body, targetName, shadowed || string.Equals(let.Name, targetName, StringComparison.Ordinal));
            case Expr.LetResult letResult:
                return UsesNameOnlyAsDirectCallee(letResult.Value, targetName, shadowed)
                    && UsesNameOnlyAsDirectCallee(letResult.Body, targetName, shadowed || string.Equals(letResult.Name, targetName, StringComparison.Ordinal));
            case Expr.LetRec letRec:
                {
                    bool nextShadowed = shadowed || string.Equals(letRec.Name, targetName, StringComparison.Ordinal);
                    return UsesNameOnlyAsDirectCallee(letRec.Value, targetName, nextShadowed)
                        && UsesNameOnlyAsDirectCallee(letRec.Body, targetName, nextShadowed);
                }
            case Expr.Lambda lam:
                return UsesNameOnlyAsDirectCallee(lam.Body, targetName, shadowed || string.Equals(lam.ParamName, targetName, StringComparison.Ordinal));
            case Expr.Match match:
                if (!UsesNameOnlyAsDirectCallee(match.Value, targetName, shadowed))
                {
                    return false;
                }

                foreach (var matchCase in match.Cases)
                {
                    bool caseShadowed = shadowed || PatternBindings(matchCase.Pattern).Any(boundName => string.Equals(boundName, targetName, StringComparison.Ordinal));
                    if (matchCase.Guard is not null && !UsesNameOnlyAsDirectCallee(matchCase.Guard, targetName, caseShadowed))
                    {
                        return false;
                    }

                    if (!UsesNameOnlyAsDirectCallee(matchCase.Body, targetName, caseShadowed))
                    {
                        return false;
                    }
                }

                return true;
            case Expr.Async asyncExpr:
                return UsesNameOnlyAsDirectCallee(asyncExpr.Body, targetName, shadowed);
            case Expr.Await awaitExpr:
                return UsesNameOnlyAsDirectCallee(awaitExpr.Task, targetName, shadowed);
            default:
                throw new NotSupportedException(expr.GetType().Name);
        }
    }

    private static bool ExprReferencesName(Expr expr, string targetName, bool shadowed = false)
    {
        switch (expr)
        {
            case Expr.IntLit:
            case Expr.FloatLit:
            case Expr.StrLit:
            case Expr.BoolLit:
            case Expr.QualifiedVar:
                return false;

            case Expr.Var v:
                return !shadowed && string.Equals(v.Name, targetName, StringComparison.Ordinal);

            case Expr.Add add:
                return ExprReferencesName(add.Left, targetName, shadowed) || ExprReferencesName(add.Right, targetName, shadowed);
            case Expr.Subtract sub:
                return ExprReferencesName(sub.Left, targetName, shadowed) || ExprReferencesName(sub.Right, targetName, shadowed);
            case Expr.Multiply mul:
                return ExprReferencesName(mul.Left, targetName, shadowed) || ExprReferencesName(mul.Right, targetName, shadowed);
            case Expr.Divide div:
                return ExprReferencesName(div.Left, targetName, shadowed) || ExprReferencesName(div.Right, targetName, shadowed);
            case Expr.GreaterOrEqual ge:
                return ExprReferencesName(ge.Left, targetName, shadowed) || ExprReferencesName(ge.Right, targetName, shadowed);
            case Expr.LessOrEqual le:
                return ExprReferencesName(le.Left, targetName, shadowed) || ExprReferencesName(le.Right, targetName, shadowed);
            case Expr.Equal eq:
                return ExprReferencesName(eq.Left, targetName, shadowed) || ExprReferencesName(eq.Right, targetName, shadowed);
            case Expr.NotEqual ne:
                return ExprReferencesName(ne.Left, targetName, shadowed) || ExprReferencesName(ne.Right, targetName, shadowed);
            case Expr.ResultPipe pipe:
                return ExprReferencesName(pipe.Left, targetName, shadowed) || ExprReferencesName(pipe.Right, targetName, shadowed);
            case Expr.ResultMapErrorPipe pipe:
                return ExprReferencesName(pipe.Left, targetName, shadowed) || ExprReferencesName(pipe.Right, targetName, shadowed);
            case Expr.Call call:
                return ExprReferencesName(call.Func, targetName, shadowed) || ExprReferencesName(call.Arg, targetName, shadowed);
            case Expr.TupleLit tuple:
                return tuple.Elements.Any(elem => ExprReferencesName(elem, targetName, shadowed));
            case Expr.ListLit list:
                return list.Elements.Any(elem => ExprReferencesName(elem, targetName, shadowed));
            case Expr.Cons cons:
                return ExprReferencesName(cons.Head, targetName, shadowed) || ExprReferencesName(cons.Tail, targetName, shadowed);
            case Expr.If iff:
                return ExprReferencesName(iff.Cond, targetName, shadowed)
                    || ExprReferencesName(iff.Then, targetName, shadowed)
                    || ExprReferencesName(iff.Else, targetName, shadowed);
            case Expr.Let let:
                return ExprReferencesName(let.Value, targetName, shadowed)
                    || ExprReferencesName(let.Body, targetName, shadowed || string.Equals(let.Name, targetName, StringComparison.Ordinal));
            case Expr.LetResult letResult:
                return ExprReferencesName(letResult.Value, targetName, shadowed)
                    || ExprReferencesName(letResult.Body, targetName, shadowed || string.Equals(letResult.Name, targetName, StringComparison.Ordinal));
            case Expr.LetRec letRec:
                {
                    bool nextShadowed = shadowed || string.Equals(letRec.Name, targetName, StringComparison.Ordinal);
                    return ExprReferencesName(letRec.Value, targetName, nextShadowed)
                        || ExprReferencesName(letRec.Body, targetName, nextShadowed);
                }
            case Expr.Lambda lam:
                return ExprReferencesName(lam.Body, targetName, shadowed || string.Equals(lam.ParamName, targetName, StringComparison.Ordinal));
            case Expr.Match match:
                if (ExprReferencesName(match.Value, targetName, shadowed))
                {
                    return true;
                }

                foreach (var matchCase in match.Cases)
                {
                    bool caseShadowed = shadowed || PatternBindings(matchCase.Pattern).Any(boundName => string.Equals(boundName, targetName, StringComparison.Ordinal));
                    if ((matchCase.Guard is not null && ExprReferencesName(matchCase.Guard, targetName, caseShadowed))
                        || ExprReferencesName(matchCase.Body, targetName, caseShadowed))
                    {
                        return true;
                    }
                }

                return false;
            case Expr.Async asyncExpr:
                return ExprReferencesName(asyncExpr.Body, targetName, shadowed);
            case Expr.Await awaitExpr:
                return ExprReferencesName(awaitExpr.Task, targetName, shadowed);
            default:
                throw new NotSupportedException(expr.GetType().Name);
        }
    }




























































}
