using System.Diagnostics;
using Ashes.Frontend;

namespace Ashes.Semantics;

public sealed class Lowering
{
    public readonly record struct HoverTypeInfo(TextSpan Span, string? Name, TypeRef Type);

    private readonly Diagnostics _diag;
    private int _nextTemp;
    private int _nextLocal;
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

    private sealed class DiagnosticContextScope(List<string> diagnosticContext) : IDisposable
    {
        public void Dispose()
        {
            diagnosticContext.RemoveAt(diagnosticContext.Count - 1);
        }
    }

    private sealed class DiagnosticSpanScope(Stack<TextSpan> diagnosticSpans) : IDisposable
    {
        public void Dispose()
        {
            diagnosticSpans.Pop();
        }
    }

    private sealed class DiagnosticCodeScope(Stack<string> diagnosticCodes) : IDisposable
    {
        public void Dispose()
        {
            diagnosticCodes.Pop();
        }
    }

    // TCO (tail call optimization) state
    private sealed class TcoContext
    {
        public string SelfName { get; init; } = "";
        public string BodyLabel { get; set; } = "";
        public int ParamCount { get; init; }
        public List<string> ParamNames { get; init; } = [];
        public List<int> ParamSlots { get; init; } = [];
        public bool InTailPosition { get; set; }

        // Arena watermark for per-iteration reset in TCO loops.
        // Saved right after the loop body label; restored before jumping back
        // when all tail-call arguments are copy types (no heap pointers escape).
        public int ArenaCursorSlot { get; set; } = -1;
        public int ArenaEndSlot { get; set; } = -1;
    }

    private TcoContext? _tcoCtx;

    // Async context tracking — true when lowering inside an async block body
    private bool _insideAsync;
    // The error type variable for the current async block; unified from each await's E.
    // Uses save/restore pattern to support nested async blocks.
    private TypeRef? _currentAsyncErrorType;

    private enum IntrinsicKind
    {
        Print,
        Write,
        WriteLine,
        ReadLine,
        FileReadText,
        FileWriteText,
        FileExists,
        HttpGet,
        HttpPost,
        NetTcpConnect,
        NetTcpSend,
        NetTcpReceive,
        NetTcpClose,
        Panic,
        AsyncRun,
        AsyncFromResult,
        AsyncSleep,
        AsyncAll,
        AsyncRace
    }

    private enum PreludeValueKind
    {
        Args
    }

    // Binding kinds: local slot or captured env index
    private abstract record Binding(TypeRef Type)
    {
        public virtual TextSpan? DefinitionSpan => null;

        public sealed record Local(int Slot, TypeRef T, TextSpan? Span = null) : Binding(T)
        {
            public override TextSpan? DefinitionSpan => Span;
        }

        public sealed record Env(int Index, TypeRef T, TextSpan? Span = null) : Binding(T)
        {
            public override TextSpan? DefinitionSpan => Span;
        }

        public sealed record EnvScheme(int Index, TypeScheme S, TextSpan? Span = null) : Binding(S.Body)
        {
            public override TextSpan? DefinitionSpan => Span;
        }

        public sealed record Self(string FuncLabel, TypeRef T, int EnvSizeBytes, TextSpan? Span = null) : Binding(T)
        {
            public override TextSpan? DefinitionSpan => Span;
        }

        public sealed record Intrinsic(IntrinsicKind Kind, TypeScheme S) : Binding(S.Body);
        public sealed record PreludeValue(PreludeValueKind Kind, TypeScheme S) : Binding(S.Body);

        public sealed record Scheme(int Slot, TypeScheme S, TextSpan? Span = null) : Binding(S.Body)
        {
            public override TextSpan? DefinitionSpan => Span;
        }
    }

    private readonly Stack<Dictionary<string, Binding>> _scopes = new();

    // --- Ownership tracking ---
    // Tracks owned bindings and their drop/borrow state.
    // Key: binding name, Value: ownership info (slot, type name, whether dropped, active borrows).
    // Copy types (Int, Float, Bool) are never tracked.
    // Owned types (String, List, ADTs, Closures, resource types) are tracked.
    private sealed class OwnershipInfo(int slot, string typeName, bool isResource, TextSpan? definitionSpan)
    {
        public int Slot { get; } = slot;
        public string TypeName { get; } = typeName;
        public bool IsResource { get; } = isResource;
        public TextSpan? DefinitionSpan { get; } = definitionSpan;
        public bool IsDropped { get; set; }
        /// <summary>
        /// Number of live borrows of this value. The compiler infers borrows when
        /// an owned value is used without consuming ownership. By scope structure,
        /// all borrows are consumed before the owning scope exits and emits Drop —
        /// this count is informational for future optimization passes.
        /// </summary>
        public int ActiveBorrows { get; set; }
    }

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

    /// <summary>
    /// Sets source context for debug info tagging. Call before Lower()
    /// so that emitted IR instructions carry source locations.
    /// </summary>
    public void SetSourceContext(string filePath, string sourceText)
    {
        _currentFilePath = filePath;
        _lineStarts = SourceTextUtils.GetLineStarts(sourceText);
        _sourceLength = sourceText.Length;
    }

    /// <summary>
    /// Sets multi-file source context using a <see cref="CombinedCompilationLayout"/>
    /// so that emitted IR instructions carry per-file source locations.
    /// </summary>
    public void SetSourceContext(CombinedCompilationLayout layout)
    {
        _lineStarts = SourceTextUtils.GetLineStarts(layout.Source);
        _sourceLength = layout.Source.Length;
        _moduleOffsets = layout.ModuleOffsets;

        // Pre-compute line starts per region (not per file) so disjoint regions
        // for the same file each get correct line/column mappings.
        _moduleLineStarts = new int[layout.ModuleOffsets.Count][];
        for (int i = 0; i < layout.ModuleOffsets.Count; i++)
        {
            var (_, startOffset, endOffset) = layout.ModuleOffsets[i];
            var moduleText = layout.Source[startOffset..endOffset];
            _moduleLineStarts[i] = SourceTextUtils.GetLineStarts(moduleText);
        }

        // Default to first entry module file
        if (layout.ModuleOffsets.Count > 0)
        {
            _currentFilePath = layout.ModuleOffsets[^1].FilePath;
        }
    }

    /// <summary>
    /// Emits an IR instruction, optionally tagging it with the source
    /// location of <see cref="_currentSourceExpr"/> when debug context is set.
    /// </summary>
    private void Emit(IrInst inst)
    {
        if (_lineStarts is not null && _currentSourceExpr is not null)
        {
            var span = AstSpans.GetOrDefault(_currentSourceExpr);
            if (span.Length > 0 || span.Start > 0)
            {
                var (filePath, line, column) = ResolveSourceLocation(span.Start);
                inst = inst with { Location = new SourceLocation(filePath, line, column) };
            }
        }

        _inst.Add(inst);
    }

    private (string FilePath, int Line, int Column) ResolveSourceLocation(int absolutePosition)
    {
        // Multi-file resolution: find which module the position falls in
        if (_moduleOffsets is not null)
        {
            for (int i = _moduleOffsets.Count - 1; i >= 0; i--)
            {
                var (filePath, startOffset, endOffset) = _moduleOffsets[i];
                if (absolutePosition >= startOffset && absolutePosition < endOffset)
                {
                    var relativePosition = absolutePosition - startOffset;
                    if (_moduleLineStarts is not null)
                    {
                        var moduleLength = endOffset - startOffset;
                        var (line, column) = SourceTextUtils.ToLineColumn(_moduleLineStarts[i], moduleLength, relativePosition);
                        return (filePath, line, column);
                    }
                }
            }
        }

        // Single-file fallback
        var (l, c) = SourceTextUtils.ToLineColumn(_lineStarts!, _sourceLength, absolutePosition);
        return (_currentFilePath ?? "<unknown>", l, c);
    }

    public IrProgram Lower(Program program)
    {
        RegisterTypeDeclarations(program.TypeDecls);
        return Lower(program.Body);
    }

    private void RegisterTypeDeclarations(IReadOnlyList<TypeDecl> typeDecls)
    {
        foreach (var decl in typeDecls)
        {
            if (BuiltinRegistry.IsReservedTypeName(decl.Name))
            {
                ReportDiagnostic(GetSpan(decl), "'Ashes' and built-in runtime types are reserved");
                continue;
            }

            if (_typeSymbols.ContainsKey(decl.Name))
            {
                ReportDiagnostic(GetSpan(decl), $"Duplicate type name '{decl.Name}'.");
                continue;
            }

            var declaredOrInferredTypeParameters = decl.TypeParameters.Count > 0
                ? decl.TypeParameters
                : InferImplicitTypeParameters(decl.Constructors);

            var seenTypeParams = new HashSet<string>(StringComparer.Ordinal);
            var hasDuplicateTypeParams = false;
            foreach (var tp in declaredOrInferredTypeParameters)
            {
                if (!seenTypeParams.Add(tp.Name))
                {
                    ReportDiagnostic(GetSpan(decl), $"Duplicate type parameter '{tp.Name}' in type '{decl.Name}'.");
                    hasDuplicateTypeParams = true;
                }
            }

            if (hasDuplicateTypeParams)
            {
                continue; // Do not register an inconsistent type symbol when type parameters are duplicated
            }

            if (decl.Constructors.Count == 0)
            {
                ReportDiagnostic(GetSpan(decl), $"Type '{decl.Name}' must have at least one constructor.");
                continue; // Cannot register a usable type symbol without constructors
            }

            var ctorSymbols = new List<ConstructorSymbol>();
            var seenCtors = new HashSet<string>(StringComparer.Ordinal);

            foreach (var ctor in decl.Constructors)
            {
                if (!seenCtors.Add(ctor.Name))
                {
                    ReportDiagnostic(GetSpan(ctor), $"Duplicate constructor name '{ctor.Name}' in type '{decl.Name}'.");
                    continue;
                }

                var ctorSymbol = new ConstructorSymbol(
                    Name: ctor.Name,
                    ParentType: decl.Name,
                    Arity: ctor.Parameters.Count,
                    ParameterTypes: ctor.Parameters
                        .Select(parameterName => ResolveUserConstructorParameterType(parameterName, declaredOrInferredTypeParameters))
                        .ToList(),
                    DeclaringSyntax: ctor
                );
                ctorSymbols.Add(ctorSymbol);
                // Constructor names are globally visible (ML/F#-style): a later type's
                // constructor with the same name shadows an earlier one intentionally.
                _constructorSymbols[ctor.Name] = ctorSymbol;
            }

            var typeSymbol = new TypeSymbol(
                Name: decl.Name,
                TypeParameters: declaredOrInferredTypeParameters.Select(tp => new TypeParameterSymbol(tp.Name)).ToList(),
                Constructors: ctorSymbols,
                DeclaringSyntax: decl with { TypeParameters = declaredOrInferredTypeParameters }
            );
            _typeSymbols[decl.Name] = typeSymbol;

            var typeParams = typeSymbol.TypeParameters
                .Select(tp => (TypeRef)new TypeRef.TTypeParam(tp))
                .ToList();
            _resolvedTypes[decl.Name] = new TypeRef.TNamedType(typeSymbol, typeParams);
        }
    }

    private void RegisterBuiltinSymbols()
    {
        foreach (var builtinType in BuiltinRegistry.Types)
        {
            if (_typeSymbols.ContainsKey(builtinType.Name))
            {
                continue;
            }

            var constructors = builtinType.Constructors
                .Select(ctor => new ConstructorSymbol(
                    Name: ctor.Name,
                    ParentType: builtinType.Name,
                    Arity: ctor.ParameterTypes.Count,
                    ParameterTypes: ctor.ParameterTypes,
                    DeclaringSyntax: ctor.DeclaringSyntax,
                    IsBuiltin: true))
                .ToList();

            var typeSymbol = new TypeSymbol(
                Name: builtinType.Name,
                TypeParameters: builtinType.TypeParameters,
                Constructors: constructors,
                DeclaringSyntax: builtinType.DeclaringSyntax,
                IsBuiltin: true);

            _typeSymbols[builtinType.Name] = typeSymbol;
            if (string.Equals(builtinType.Name, "List", StringComparison.Ordinal))
            {
                _resolvedTypes[builtinType.Name] = new TypeRef.TNamedType(typeSymbol, [new TypeRef.TTypeParam(typeSymbol.TypeParameters[0])]);
            }
            else if (typeSymbol.TypeParameters.Count > 0)
            {
                _resolvedTypes[builtinType.Name] = new TypeRef.TNamedType(
                    typeSymbol,
                    typeSymbol.TypeParameters.Select(tp => (TypeRef)new TypeRef.TTypeParam(tp)).ToList());
            }
            else
            {
                _resolvedTypes[builtinType.Name] = new TypeRef.TNamedType(typeSymbol, []);
            }
            foreach (var constructor in constructors)
            {
                _constructorSymbols[constructor.Name] = constructor;
            }
        }
    }

    private static TypeRef ResolveUserConstructorParameterType(string parameterName, IReadOnlyList<TypeParameter> declaredOrInferredTypeParameters)
    {
        var matchingParameter = declaredOrInferredTypeParameters.FirstOrDefault(tp => string.Equals(tp.Name, parameterName, StringComparison.Ordinal));
        if (matchingParameter is not null)
        {
            return new TypeRef.TTypeParam(new TypeParameterSymbol(matchingParameter.Name));
        }

        return new TypeRef.TTypeParam(new TypeParameterSymbol(parameterName));
    }

    private static IReadOnlyList<TypeParameter> InferImplicitTypeParameters(IReadOnlyList<TypeConstructor> constructors)
    {
        var typeParameters = new List<TypeParameter>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var parameterName in constructors.SelectMany(ctor => ctor.Parameters))
        {
            if (seen.Add(parameterName))
            {
                typeParameters.Add(new TypeParameter(parameterName));
            }
        }

        return typeParameters;
    }

    private static Dictionary<string, TypeRef> CreateTypeParameterMap(TypeSymbol typeSymbol, IReadOnlyList<TypeRef> typeArgs)
    {
        var result = new Dictionary<string, TypeRef>(StringComparer.Ordinal);
        for (int i = 0; i < typeSymbol.TypeParameters.Count && i < typeArgs.Count; i++)
        {
            result[typeSymbol.TypeParameters[i].Name] = typeArgs[i];
        }

        return result;
    }

    public TypeRef ResolveTypeName(string name, IReadOnlyList<TypeRef>? typeArgs = null)
    {
        typeArgs ??= [];
        if (BuiltinRegistry.TryGetPrimitiveType(name, out var primitiveType))
        {
            if (typeArgs.Count != 0)
            {
                ReportDiagnostic(0, $"Type '{name}' expects 0 type argument(s) but got {typeArgs.Count}.");
                return new TypeRef.TNever();
            }

            return primitiveType;
        }

        if (string.Equals(name, "List", StringComparison.Ordinal))
        {
            if (typeArgs.Count != 1)
            {
                ReportDiagnostic(0, $"Type 'List' expects 1 type argument(s) but got {typeArgs.Count}.");
                return new TypeRef.TNever();
            }

            return new TypeRef.TList(typeArgs[0]);
        }

        if (!_typeSymbols.TryGetValue(name, out var sym))
        {
            ReportDiagnostic(0, $"Unknown type name '{name}'.");
            return new TypeRef.TNever();
        }

        var expectedArity = sym.TypeParameters.Count;
        if (typeArgs.Count != expectedArity)
        {
            ReportDiagnostic(0, $"Type '{name}' expects {expectedArity} type argument(s) but got {typeArgs.Count}.");
            return new TypeRef.TNever();
        }

        return new TypeRef.TNamedType(sym, typeArgs);
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
            LocalCount: _nextLocal,
            TempCount: _nextTemp,
            HasEnvAndArgParams: false,
            LocalNames: new Dictionary<int, string>(_localNames),
            LocalTypes: SnapshotLocalTypes()
        );

        return new IrProgram(
            EntryFunction: entry,
            Functions: _funcs,
            StringLiterals: _strings,
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
            BuiltinRegistry.BuiltinValueKind.HttpGet => LowerQualifiedBuiltinFunctionReference(name, CreateHttpGetBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.HttpPost => LowerQualifiedBuiltinFunctionReference(name, CreateHttpPostBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.NetTcpConnect => LowerQualifiedBuiltinFunctionReference(name, CreateNetTcpConnectBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.NetTcpSend => LowerQualifiedBuiltinFunctionReference(name, CreateNetTcpSendBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.NetTcpReceive => LowerQualifiedBuiltinFunctionReference(name, CreateNetTcpReceiveBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.NetTcpClose => LowerQualifiedBuiltinFunctionReference(name, CreateNetTcpCloseBinding().S.Body),
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
        // Value is NOT in tail position
        var savedTailPos = _tcoCtx?.InTailPosition ?? false;
        if (_tcoCtx is not null) _tcoCtx.InTailPosition = false;

        // Save the arena watermark BEFORE lowering the let-bound value so that
        // heap allocations from the value expression are covered by the arena scope.
        EmitArenaWatermark();

        bool stackAllocateClosure = let.Value is Expr.Lambda && UsesNameOnlyAsDirectCallee(let.Body, let.Name);
        bool stackAllocateAdt = IsConstructorExpression(let.Value) && IsImmediateSingleArmAdtDestructuringMatch(let.Name, let.Body);

        (int valTemp, TypeRef valType) = stackAllocateClosure && let.Value is Expr.Lambda lam
            ? LowerLambda(lam, stackAllocateClosure: true)
            : stackAllocateAdt && TryLowerConstructorExpression(let.Value, stackAllocate: true, out var loweredAdt)
                ? loweredAdt
                : LowerExpr(let.Value);

        int slot = NewLocal();
        Emit(new IrInst.StoreLocal(slot, valTemp));
        RecordLocalDebugInfo(slot, let.Name, valType);
        var scheme = Generalize(Prune(valType));
        RecordHoverType(AstSpans.GetLetNameOrDefault(let), let.Name, scheme.Body);

        var parent = _scopes.Peek();
        var child = new Dictionary<string, Binding>(parent, StringComparer.Ordinal)
        {
            [let.Name] = new Binding.Scheme(slot, scheme, AstSpans.GetLetNameOrDefault(let))
        };
        _scopes.Push(child);

        // Track owned bindings for deterministic cleanup
        PushOwnershipScope();
        var prunedValType = Prune(valType);
        var ownedTypeName = GetOwnedTypeName(prunedValType);
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
                var isResource = GetResourceTypeName(prunedValType) is not null;
                TrackOwnedValue(let.Name, slot, ownedTypeName, isResource, AstSpans.GetLetNameOrDefault(let));
            }
        }

        // Body IS in tail position (if the let itself is)
        if (_tcoCtx is not null) _tcoCtx.InTailPosition = savedTailPos;
        var (bodyTemp, bodyType) = LowerExpr(let.Body);

        // Only emit result preservation when there are alive owned values to drop
        if (HasAliveOwnedValuesInCurrentScope())
        {
            // Save the result pointer in a local slot so it survives drop instructions.
            int resultSlot = NewLocal();
            Emit(new IrInst.StoreLocal(resultSlot, bodyTemp));
            int finalTemp = PopOwnershipScope(bodyType, bodyTemp);
            _scopes.Pop();
            if (finalTemp != bodyTemp)
            {
                // Copy-out occurred: finalTemp is the freshly allocated copy.
                return (finalTemp, bodyType);
            }
            // No copy-out: reload from the preserved slot (standard path).
            int resultTemp = NewTemp();
            Emit(new IrInst.LoadLocal(resultTemp, resultSlot));
            return (resultTemp, bodyType);
        }

        {
            int finalTemp = PopOwnershipScope(bodyType, bodyTemp);
            _scopes.Pop();
            return (finalTemp, bodyType);
        }
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
            or BuiltinRegistry.BuiltinValueKind.NetTcpClose;
    }

    private static bool IsAsyncOnlyNetworkingIntrinsic(IntrinsicKind kind)
    {
        return kind is IntrinsicKind.HttpGet
            or IntrinsicKind.HttpPost
            or IntrinsicKind.NetTcpConnect
            or IntrinsicKind.NetTcpSend
            or IntrinsicKind.NetTcpReceive
            or IntrinsicKind.NetTcpClose;
    }

    private static int GetIntrinsicArity(IntrinsicKind kind) => kind switch
    {
        IntrinsicKind.FileWriteText => 2,
        IntrinsicKind.HttpPost => 2,
        IntrinsicKind.NetTcpConnect => 2,
        IntrinsicKind.NetTcpSend => 2,
        IntrinsicKind.NetTcpReceive => 2,
        _ => 1
    };

    private bool TryRequireSocketType(TypeRef type, Expr origin, string diagnosticMessage)
    {
        var prunedType = Prune(type);
        if (prunedType is TypeRef.TVar)
        {
            Unify(prunedType, _resolvedTypes["Socket"]);
            return true;
        }

        if (prunedType is TypeRef.TNamedType named && string.Equals(named.Symbol.Name, "Socket", StringComparison.Ordinal))
        {
            return true;
        }

        ReportDiagnostic(GetSpan(origin), $"{diagnosticMessage} Got {Pretty(prunedType)}.");
        return false;
    }

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
        var savedTemp = _nextTemp;
        var savedLocal = _nextLocal;
        var savedScopes = _scopes.ToArray();
        var savedLocalNames = new Dictionary<int, string>(_localNames);
        var savedLocalTypes = new Dictionary<int, TypeRef>(_localTypes);

        // new function state
        _inst.Clear();
        _nextTemp = 0;
        _nextLocal = 0;
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
            LocalCount: _nextLocal,
            TempCount: _nextTemp,
            HasEnvAndArgParams: true,
            LocalNames: new Dictionary<int, string>(_localNames),
            LocalTypes: SnapshotLocalTypes()
        );

        // restore state
        _funcs.Add(func);

        _inst.Clear();
        _inst.AddRange(savedInst);
        _nextTemp = savedTemp;
        _nextLocal = savedLocal;
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

    private IDisposable PushDiagnosticContext(string context)
    {
        _diagnosticContext.Add(context);
        return new DiagnosticContextScope(_diagnosticContext);
    }

    private IDisposable PushDiagnosticCode(string code)
    {
        _diagnosticCodes.Push(code);
        return new DiagnosticCodeScope(_diagnosticCodes);
    }

    private string? CurrentDiagnosticCodeOrDefault(string? fallback = null)
    {
        return _diagnosticCodes.Count > 0 ? _diagnosticCodes.Peek() : fallback;
    }

    private void ReportDiagnostic(int pos, string message)
    {
        ReportDiagnostic(pos, message, null);
    }

    private void ReportDiagnostic(int pos, string message, string? code)
    {
        if (pos == 0 && _diagnosticSpans.Count > 0)
        {
            ReportDiagnostic(_diagnosticSpans.Peek(), message, code);
            return;
        }

        if (_diagnosticContext.Count == 0)
        {
            _diag.Error(pos, message, CurrentDiagnosticCodeOrDefault(code));
            return;
        }

        _diag.Error(pos, $"{message} Context: {string.Join(" -> ", _diagnosticContext.AsEnumerable().Reverse())}.", CurrentDiagnosticCodeOrDefault(code));
    }

    private void ReportDiagnostic(TextSpan span, string message)
    {
        ReportDiagnostic(span, message, null);
    }

    private void ReportDiagnostic(TextSpan span, string message, string? code)
    {
        if (_diagnosticContext.Count == 0)
        {
            _diag.Error(span, message, CurrentDiagnosticCodeOrDefault(code));
            return;
        }

        _diag.Error(span, $"{message} Context: {string.Join(" -> ", _diagnosticContext.AsEnumerable().Reverse())}.", CurrentDiagnosticCodeOrDefault(code));
    }

    private IDisposable PushDiagnosticSpan(Expr expr)
    {
        return PushDiagnosticSpan(GetSpan(expr));
    }

    private IDisposable PushDiagnosticSpan(Pattern pattern)
    {
        return PushDiagnosticSpan(GetSpan(pattern));
    }

    private IDisposable PushDiagnosticSpan(TextSpan span)
    {
        _diagnosticSpans.Push(span);
        return new DiagnosticSpanScope(_diagnosticSpans);
    }

    private static TextSpan CombineSpans(Expr left, Expr right)
    {
        var leftSpan = GetSpan(left);
        var rightSpan = GetSpan(right);
        return TextSpan.FromBounds(leftSpan.Start, Math.Max(leftSpan.End, rightSpan.End));
    }

    private static TextSpan GetSpan(Expr expr)
    {
        var span = AstSpans.GetOrDefault(expr);
        return span.Length == 0 ? TextSpan.FromBounds(span.Start, span.Start + 1) : span;
    }

    private static TextSpan GetSpan(Pattern pattern)
    {
        var span = AstSpans.GetOrDefault(pattern);
        return span.Length == 0 ? TextSpan.FromBounds(span.Start, span.Start + 1) : span;
    }

    private static TextSpan GetSpan(TypeDecl typeDecl)
    {
        var span = AstSpans.GetOrDefault(typeDecl);
        return span.Length == 0 ? TextSpan.FromBounds(span.Start, span.Start + 1) : span;
    }

    private static TextSpan GetSpan(TypeConstructor typeConstructor)
    {
        var span = AstSpans.GetOrDefault(typeConstructor);
        return span.Length == 0 ? TextSpan.FromBounds(span.Start, span.Start + 1) : span;
    }

    private (int, TypeRef) ReportArityMismatch(Expr callee, int expectedArgs, int providedArgs)
    {
        var calleeName = TryGetCalleeDisplayName(callee);
        if (calleeName is not null)
        {
            ReportDiagnostic(GetSpan(callee), $"Call to '{calleeName}' expects {expectedArgs} argument(s) but got {providedArgs}.");
        }
        else
        {
            ReportDiagnostic(GetSpan(callee), $"Call expects {expectedArgs} argument(s) but got {providedArgs}.");
        }

        return ReturnNeverWithDummyTemp();
    }

    private (int, TypeRef) ReportNonFunctionCall(Expr callee, TypeRef actualType, int providedArgs)
    {
        var calleeName = TryGetCalleeDisplayName(callee);
        if (calleeName is not null)
        {
            ReportDiagnostic(GetSpan(callee), $"Attempted to call '{calleeName}' with {providedArgs} argument(s), but its type is {Pretty(actualType)}, not a function.");
        }
        else
        {
            ReportDiagnostic(GetSpan(callee), $"Attempted to call non-function type {Pretty(actualType)}.");
        }

        return ReturnNeverWithDummyTemp();
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
                IntrinsicKind.HttpGet => LowerHttpGet(collectedArgs[0]),
                IntrinsicKind.HttpPost => LowerHttpPost(collectedArgs[0], collectedArgs[1]),
                IntrinsicKind.NetTcpConnect => LowerNetTcpConnect(collectedArgs[0], collectedArgs[1]),
                IntrinsicKind.NetTcpSend => LowerNetTcpSend(collectedArgs[0], collectedArgs[1]),
                IntrinsicKind.NetTcpReceive => LowerNetTcpReceive(collectedArgs[0], collectedArgs[1]),
                IntrinsicKind.NetTcpClose => LowerNetTcpClose(collectedArgs[0]),
                IntrinsicKind.Panic => LowerPanic(collectedArgs[0]),
                IntrinsicKind.AsyncRun => LowerAsyncRun(collectedArgs[0]),
                IntrinsicKind.AsyncFromResult => LowerAsyncFromResult(collectedArgs[0]),
                IntrinsicKind.AsyncSleep => LowerAsyncSleep(collectedArgs[0]),
                IntrinsicKind.AsyncAll => LowerAsyncAll(collectedArgs[0]),
                IntrinsicKind.AsyncRace => LowerAsyncRace(collectedArgs[0]),
                _ => throw new NotSupportedException($"Unknown intrinsic: {intrinsic.Kind}")
            };
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
                    BuiltinRegistry.BuiltinValueKind.HttpGet => LowerHttpGet(collectedArgs[0]),
                    BuiltinRegistry.BuiltinValueKind.HttpPost => LowerHttpPost(collectedArgs[0], collectedArgs[1]),
                    BuiltinRegistry.BuiltinValueKind.NetTcpConnect => LowerNetTcpConnect(collectedArgs[0], collectedArgs[1]),
                    BuiltinRegistry.BuiltinValueKind.NetTcpSend => LowerNetTcpSend(collectedArgs[0], collectedArgs[1]),
                    BuiltinRegistry.BuiltinValueKind.NetTcpReceive => LowerNetTcpReceive(collectedArgs[0], collectedArgs[1]),
                    BuiltinRegistry.BuiltinValueKind.NetTcpClose => LowerNetTcpClose(collectedArgs[0]),
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

    private (int, TypeRef) LowerPanic(Expr arg)
    {
        using var diagnosticSpan = PushDiagnosticSpan(arg);
        var (msgTemp, msgType) = LowerExpr(arg);
        Unify(msgType, new TypeRef.TStr());
        Emit(new IrInst.PanicStr(msgTemp));
        return (msgTemp, new TypeRef.TNever());
    }

    private (int, TypeRef) LowerNullaryConstructor(ConstructorSymbol ctor, bool stackAllocate = false)
    {
        var resultType = InstantiateAdtType(ctor);
        int tag = GetConstructorTag(ctor);

        // Allocate ADT heap cell: (1 + 0) * 8 = 8 bytes (tag only, no fields): [ctorTag]
        int ptrTemp = NewTemp();
        if (stackAllocate)
        {
            Emit(new IrInst.AllocAdtStack(ptrTemp, tag, 0));
        }
        else
        {
            Emit(new IrInst.AllocAdt(ptrTemp, tag, 0));
        }
        return (ptrTemp, resultType);
    }

    private static Expr BuildConstructorLambda(ConstructorSymbol ctor)
    {
        var paramNames = Enumerable.Range(0, ctor.Arity)
            .Select(i => $"__ctor_arg_{ctor.Name}_{i}")
            .ToArray();

        Expr body = new Expr.Var(ctor.Name);
        foreach (var paramName in paramNames)
        {
            body = new Expr.Call(body, new Expr.Var(paramName));
        }

        for (int i = paramNames.Length - 1; i >= 0; i--)
        {
            body = new Expr.Lambda(paramNames[i], body);
        }

        return body;
    }

    private (int, TypeRef) LowerConstructorApplication(ConstructorSymbol ctor, List<Expr> args, bool stackAllocate = false)
    {
        if (args.Count != ctor.Arity)
        {
            var errorSpan = args.Count > 0 ? GetSpan(args[0]) : GetSpan(ctor.DeclaringSyntax);
            ReportDiagnostic(errorSpan, $"Constructor '{ctor.Name}' expects {ctor.Arity} argument(s) but got {args.Count}. Expected shape: {FormatConstructorShape(ctor)}.");
            foreach (var a in args)
            {
                LowerExpr(a);
            }

            return ReturnNeverWithDummyTemp();
        }

        var resultType = InstantiateAdtType(ctor);

        // Evaluate all args left-to-right, unifying each with its declared type parameter.
        // This catches mismatches when the same type parameter appears in multiple positions
        // (e.g., Pair(T, T) applied to arguments of different types).
        var argTemps = new List<int>(args.Count);
        for (int i = 0; i < args.Count; i++)
        {
            var (argTemp, argType) = LowerExpr(args[i]);
            argTemps.Add(argTemp);

            var parameterType = InstantiateConstructorParameterType(ctor, i, resultType);
            Unify(parameterType, argType);
        }

        int tag = GetConstructorTag(ctor);

        // Allocate a tagged heap cell: [ctorTag, field0, field1, ..., fieldN]
        int ptrTemp = NewTemp();
        if (stackAllocate)
        {
            Emit(new IrInst.AllocAdtStack(ptrTemp, tag, ctor.Arity));
        }
        else
        {
            Emit(new IrInst.AllocAdt(ptrTemp, tag, ctor.Arity));
        }
        for (int i = 0; i < argTemps.Count; i++)
        {
            Emit(new IrInst.SetAdtField(ptrTemp, i, argTemps[i]));
        }

        return (ptrTemp, resultType);
    }

    private int GetConstructorTag(ConstructorSymbol ctor)
    {
        var typeSym = _typeSymbols[ctor.ParentType];
        for (int i = 0; i < typeSym.Constructors.Count; i++)
        {
            if (string.Equals(typeSym.Constructors[i].Name, ctor.Name, StringComparison.Ordinal))
            {
                return i;
            }
        }

        throw new InvalidOperationException(
            $"Constructor '{ctor.Name}' not found in its own parent type '{ctor.ParentType}'. This is a compiler invariant violation.");
    }

    private TypeRef.TNamedType InstantiateAdtType(ConstructorSymbol ctor)
    {
        var typeSym = _typeSymbols[ctor.ParentType];
        var freshArgs = typeSym.TypeParameters.Select(_ => (TypeRef)NewTypeVar()).ToList();
        return new TypeRef.TNamedType(typeSym, freshArgs);
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

    private (int, TypeRef) LowerMatch(Expr.Match match)
    {
        // The matched value is NOT in tail position
        var savedTailPos = _tcoCtx?.InTailPosition ?? false;
        if (_tcoCtx is not null) _tcoCtx.InTailPosition = false;

        var (valueTemp, valueType) = ShouldStackAllocateImmediateMatchScrutinee(match)
            && TryLowerConstructorExpression(match.Value, stackAllocate: true, out var loweredMatchValue)
                ? loweredMatchValue
                : LowerExpr(match.Value);
        var resultType = NewTypeVar();
        var resultSlot = NewLocal();
        var endLabel = NewLabel("match_end");
        var noMatchLabel = NewLabel("match_none");

        Debug.Assert(match.Cases.Count > 0, "Parser should ensure match has at least one case.");

        ValidateSingleAdtMatch(match.Cases);
        ValidateReachableMatchArms(match.Cases);
        var hasAnyTuplePattern = match.Cases.Any(c => c.Pattern is Pattern.Tuple);

        for (int i = 0; i < match.Cases.Count; i++)
        {
            var caseFailLabel = i == match.Cases.Count - 1 ? noMatchLabel : NewLabel("match_next");
            var armCleanupLabel = NewLabel("match_arm_cleanup");
            var caseScope = new Dictionary<string, Binding>(_scopes.Peek(), StringComparer.Ordinal);
            _scopes.Push(caseScope);
            // Save the arena watermark before pattern matching and body evaluation
            // so allocations in guard expressions and the arm body are covered.
            EmitArenaWatermark();
            var (armCursorSlot, armEndSlot) = _arenaWatermarks.Peek();
            PushOwnershipScope();

            var patternBindings = new Dictionary<string, TypeRef>(StringComparer.Ordinal);
            var patternType = InferPatternType(match.Cases[i].Pattern, patternBindings);
            var hasTupleArityMismatch = ValidateTuplePatternArity(Prune(valueType), match.Cases[i].Pattern);
            if (hasTupleArityMismatch)
            {
                RegisterPatternVariableBindings(patternBindings);
            }
            else
            {
                Unify(valueType, patternType);
                EmitPattern(match.Cases[i].Pattern, valueTemp, armCleanupLabel, patternBindings);
            }

            // Track owned bindings created by pattern matching
            TrackOwnedBindingsInPattern(patternBindings);

            // If the case has a guard, evaluate it and jump to cleanup label if false
            if (match.Cases[i].Guard is { } guard)
            {
                if (_tcoCtx is not null) _tcoCtx.InTailPosition = false;
                var (guardTemp, guardType) = LowerExpr(guard);
                Unify(guardType, new TypeRef.TBool());
                Emit(new IrInst.JumpIfFalse(guardTemp, armCleanupLabel));
            }

            // Each case body IS in tail position (if the match itself is)
            if (_tcoCtx is not null) _tcoCtx.InTailPosition = savedTailPos;
            var (bodyTemp, bodyType) = LowerExpr(match.Cases[i].Body);
            if (_tcoCtx is not null) _tcoCtx.InTailPosition = false;

            using (PushDiagnosticContext($"in match arm {i + 1}"))
            {
                using (PushDiagnosticCode(DiagnosticCodes.MatchBranchTypeMismatch))
                {
                    Unify(resultType, bodyType);
                }
            }
            Emit(new IrInst.StoreLocal(resultSlot, bodyTemp));
            int armFinalTemp = PopOwnershipScope(bodyType, bodyTemp);
            if (armFinalTemp != bodyTemp)
            {
                // Copy-out occurred: update the result slot with the freshly allocated copy.
                Emit(new IrInst.StoreLocal(resultSlot, armFinalTemp));
            }
            Emit(new IrInst.Jump(endLabel));

            // Arm cleanup path (Label → RestoreArenaState → ReclaimArenaChunks → Jump):
            // when pattern/guard fails, restore the arena watermark to reclaim any heap
            // allocations made during pattern matching or guard evaluation. This is always
            // safe on the failure path because no result escapes from a failed arm — all
            // allocations between the watermark and the current cursor are unreachable garbage.
            int armCleanupPreRestoreEndSlot = NewLocal();
            Emit(new IrInst.Label(armCleanupLabel));
            Emit(new IrInst.RestoreArenaState(armCursorSlot, armEndSlot, armCleanupPreRestoreEndSlot));
            Emit(new IrInst.ReclaimArenaChunks(armEndSlot, armCleanupPreRestoreEndSlot));
            Emit(new IrInst.Jump(caseFailLabel));

            _scopes.Pop();
            if (i < match.Cases.Count - 1)
            {
                Emit(new IrInst.Label(caseFailLabel));
            }
        }

        Emit(new IrInst.Label(noMatchLabel));
        var prunedValueType = Prune(valueType);
        var missingAdtConstructors = GetMissingAdtConstructors(prunedValueType, match.Cases);
        var missingListCases = GetMissingListCases(prunedValueType, match.Cases);
        var hasConstructorPatterns = HasConstructorPattern(match.Cases);
        var hasTuplePatternArm = prunedValueType is TypeRef.TTuple && hasAnyTuplePattern;
        bool reportedNonExhaustive = false;
        var matchPos = match.Pos ?? 0;
        if (missingAdtConstructors is not null)
        {
            if (missingAdtConstructors.Count > 0)
            {
                if (TryBuildMissingResultDiagnostic(prunedValueType, missingAdtConstructors, out var resultDiagnostic))
                {
                    _diag.Error(matchPos, resultDiagnostic);
                }
                else
                {
                    _diag.Error(matchPos, $"Non-exhaustive match expression. Missing constructor(s): {string.Join(", ", missingAdtConstructors.Select(name => $"'{name}'"))}.");
                }

                reportedNonExhaustive = true;
            }
        }
        else if (missingListCases is not null)
        {
            foreach (var missingCase in missingListCases)
            {
                _diag.Error(matchPos, $"Non-exhaustive match expression. Missing case: {missingCase}.");
                reportedNonExhaustive = true;
            }
        }
        else if (!hasTuplePatternArm && !hasConstructorPatterns && !IsDefinitelyExhaustive(match.Cases) && !IsBoolExhaustive(match.Cases))
        {
            _diag.Error(matchPos, "Non-exhaustive match expression.");
            reportedNonExhaustive = true;
        }

        if (!reportedNonExhaustive &&
            TryGetMissingPattern(prunedValueType, match.Cases.Where(c => c.Guard is null).Select(c => c.Pattern).ToList(), out var missingPattern))
        {
            _diag.Error(matchPos, $"Non-exhaustive match expression. Missing case: {FormatPattern(missingPattern)}.");
        }

        int defaultTemp = NewTemp();
        Emit(new IrInst.LoadConstInt(defaultTemp, 0));
        Emit(new IrInst.StoreLocal(resultSlot, defaultTemp));
        Emit(new IrInst.Label(endLabel));

        int resultTemp = NewTemp();
        Emit(new IrInst.LoadLocal(resultTemp, resultSlot));
        return (resultTemp, Prune(resultType));
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
        var savedTemp = _nextTemp;
        var savedLocal = _nextLocal;
        var savedScopes = _scopes.ToArray();
        var savedOwnershipScopes = _ownershipScopes.ToArray();
        var savedArenaWatermarks = _arenaWatermarks.ToArray();
        var savedTcoCtx = _tcoCtx;
        var savedLocalNames = new Dictionary<int, string>(_localNames);
        var savedLocalTypes = new Dictionary<int, TypeRef>(_localTypes);
        _tcoCtx = null;

        // --- Reset state for coroutine function ---
        _inst.Clear();
        _nextTemp = 0;
        _nextLocal = 0;
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
            LocalCount: _nextLocal,
            TempCount: Math.Max(_nextTemp, transformResult.MaxTemp + 1),
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
        _nextTemp = savedTemp;
        _nextLocal = savedLocal;
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

    private bool ValidateTuplePatternArity(TypeRef valueType, Pattern pattern)
    {
        if (valueType is not TypeRef.TTuple tupleType || pattern is not Pattern.Tuple tuplePattern)
        {
            return false;
        }

        if (tupleType.Elements.Count == tuplePattern.Elements.Count)
        {
            return false;
        }

        ReportDiagnostic(GetSpan(pattern), $"Tuple pattern arity mismatch: expected {tupleType.Elements.Count} element(s) but got {tuplePattern.Elements.Count}.");
        return true;
    }

    private void RegisterPatternVariableBindings(IReadOnlyDictionary<string, TypeRef> bindingTypes)
    {
        foreach (var (name, type) in bindingTypes)
        {
            int slot = NewLocal();
            _scopes.Peek()[name] = new Binding.Local(slot, Prune(type));
        }
    }

    private (int Temp, TypeRef Type) LowerEmptyList()
    {
        int t = NewTemp();
        Emit(new IrInst.LoadConstInt(t, 0));
        return (t, new TypeRef.TList(NewTypeVar()));
    }

    private (int Temp, TypeRef Type) LowerConsCell(int headTemp, int tailTemp, TypeRef headType, TypeRef tailType)
    {
        var listType = new TypeRef.TList(headType);
        Unify(tailType, listType);

        int nodeTemp = NewTemp();
        Emit(new IrInst.Alloc(nodeTemp, 16));
        Emit(new IrInst.StoreMemOffset(nodeTemp, 0, headTemp));
        Emit(new IrInst.StoreMemOffset(nodeTemp, 8, tailTemp));
        return (nodeTemp, Prune(listType));
    }

    private TypeRef InferPatternType(Pattern pattern, Dictionary<string, TypeRef> bindings)
    {
        switch (pattern)
        {
            case Pattern.EmptyList:
                return new TypeRef.TList(NewTypeVar());

            case Pattern.Wildcard:
                return NewTypeVar();

            case Pattern.Var v:
                // Check if this identifier is a known nullary constructor
                if (_constructorSymbols.TryGetValue(v.Name, out var nullaryCtor) && nullaryCtor.Arity == 0)
                {
                    return InstantiateAdtType(nullaryCtor);
                }
                if (bindings.ContainsKey(v.Name))
                {
                    ReportDiagnostic(GetSpan(pattern), $"Duplicate binding '{v.Name}' in pattern.");
                    return bindings[v.Name];
                }
                var varType = NewTypeVar();
                bindings[v.Name] = varType;
                return varType;

            case Pattern.Cons c:
                var headType = InferPatternType(c.Head, bindings);
                var tailType = InferPatternType(c.Tail, bindings);
                var listType = new TypeRef.TList(headType);
                Unify(tailType, listType);
                return listType;

            case Pattern.Tuple tuple:
                return new TypeRef.TTuple(tuple.Elements.Select(p => InferPatternType(p, bindings)).ToList());

            case Pattern.Constructor ctor:
                return InferConstructorPatternType(ctor.Name, ctor.Patterns, bindings);

            case Pattern.IntLit:
                return new TypeRef.TInt();

            case Pattern.StrLit:
                return new TypeRef.TStr();

            case Pattern.BoolLit:
                return new TypeRef.TBool();

            default:
                throw new NotSupportedException(pattern.GetType().Name);
        }
    }

    private TypeRef InferConstructorPatternType(string name, IReadOnlyList<Pattern> patterns, Dictionary<string, TypeRef> bindings)
    {
        if (!_constructorSymbols.TryGetValue(name, out var ctor))
        {
            var span = patterns.Count > 0
                ? TextSpan.FromBounds(GetSpan(patterns[0]).Start, GetSpan(patterns[^1]).End)
                : TextSpan.FromBounds(0, 1);
            ReportDiagnostic(span, $"Unknown constructor '{name}' in pattern.{BuildUnknownConstructorHint(name)}");
            foreach (var p in patterns)
            {
                InferPatternType(p, bindings);
            }
            return NewTypeVar();
        }

        if (patterns.Count != ctor.Arity)
        {
            var span = patterns.Count > 0 ? TextSpan.FromBounds(GetSpan(patterns[0]).Start, GetSpan(patterns[^1]).End) : GetSpan(ctor.DeclaringSyntax);
            ReportDiagnostic(span, $"Constructor '{name}' expects {ctor.Arity} argument(s) but pattern has {patterns.Count}. Expected shape: {FormatConstructorShape(ctor)}.");
            foreach (var p in patterns)
            {
                InferPatternType(p, bindings);
            }
            return new TypeRef.TNever();
        }

        var resultType = InstantiateAdtType(ctor);

        // Infer types for sub-patterns (bind variables into the branch scope)
        for (int i = 0; i < patterns.Count; i++)
        {
            var patternType = InferPatternType(patterns[i], bindings);
            var parameterType = InstantiateConstructorParameterType(ctor, i, resultType);
            Unify(parameterType, patternType);
        }

        return resultType;
    }

    private void EmitPattern(Pattern pattern, int valueTemp, string failLabel, IReadOnlyDictionary<string, TypeRef> bindingTypes)
    {
        switch (pattern)
        {
            case Pattern.EmptyList:
                EmitRequireZero(valueTemp, failLabel);
                return;

            case Pattern.Wildcard:
                return;

            case Pattern.Var v:
                // If this is a known nullary constructor, emit a tag check instead of binding
                if (_constructorSymbols.TryGetValue(v.Name, out var nullaryCtor) && nullaryCtor.Arity == 0)
                {
                    EmitRequireNonZero(valueTemp, failLabel);
                    EmitRequireTagMatch(valueTemp, GetConstructorTag(nullaryCtor), failLabel);
                    return;
                }
                int slot = NewLocal();
                Emit(new IrInst.StoreLocal(slot, valueTemp));
                RecordLocalDebugInfo(slot, v.Name, bindingTypes[v.Name]);
                _scopes.Peek()[v.Name] = new Binding.Local(slot, Prune(bindingTypes[v.Name]));
                return;

            case Pattern.Cons c:
                EmitRequireNonZero(valueTemp, failLabel);
                int headTemp = NewTemp();
                int tailTemp = NewTemp();
                Emit(new IrInst.LoadMemOffset(headTemp, valueTemp, 0));
                Emit(new IrInst.LoadMemOffset(tailTemp, valueTemp, 8));
                EmitPattern(c.Head, headTemp, failLabel, bindingTypes);
                EmitPattern(c.Tail, tailTemp, failLabel, bindingTypes);
                return;

            case Pattern.Tuple tuple:
                for (int i = 0; i < tuple.Elements.Count; i++)
                {
                    int elemTemp = NewTemp();
                    Emit(new IrInst.LoadMemOffset(elemTemp, valueTemp, i * 8));
                    EmitPattern(tuple.Elements[i], elemTemp, failLabel, bindingTypes);
                }
                return;

            case Pattern.Constructor ctor:
                EmitConstructorPattern(ctor, valueTemp, failLabel, bindingTypes);
                return;

            case Pattern.IntLit intLit:
                EmitRequireIntEqual(valueTemp, intLit.Value, failLabel);
                return;

            case Pattern.StrLit strLit:
                EmitRequireStrEqual(valueTemp, strLit.Value, failLabel);
                return;

            case Pattern.BoolLit boolLit:
                EmitRequireBoolEqual(valueTemp, boolLit.Value, failLabel);
                return;

            default:
                throw new NotSupportedException(pattern.GetType().Name);
        }
    }

    private void EmitConstructorPattern(Pattern.Constructor ctor, int valueTemp, string failLabel, IReadOnlyDictionary<string, TypeRef> bindingTypes)
    {
        if (!_constructorSymbols.TryGetValue(ctor.Name, out var ctorSym))
        {
            // Unknown constructor — already diagnosed in InferPatternType
            return;
        }

        // All constructors are tagged heap allocations: [ctorTag, ...payloads].
        // Check ptr != null, then check the tag matches this constructor.
        EmitRequireNonZero(valueTemp, failLabel);
        EmitRequireTagMatch(valueTemp, GetConstructorTag(ctorSym), failLabel);

        for (int i = 0; i < ctorSym.Arity && i < ctor.Patterns.Count; i++)
        {
            // Extract payload at each field index and bind sub-patterns.
            int payloadTemp = NewTemp();
            Emit(new IrInst.GetAdtField(payloadTemp, valueTemp, i));
            EmitPattern(ctor.Patterns[i], payloadTemp, failLabel, bindingTypes);
        }
    }

    private void EmitRequireTagMatch(int ptrTemp, int expectedTag, string failLabel)
    {
        int tagTemp = NewTemp();
        int eqTemp = NewTemp();
        int expectedTagTemp = NewTemp();
        Emit(new IrInst.GetAdtTag(tagTemp, ptrTemp));
        Emit(new IrInst.LoadConstInt(expectedTagTemp, expectedTag));
        Emit(new IrInst.CmpIntEq(eqTemp, tagTemp, expectedTagTemp));
        Emit(new IrInst.JumpIfFalse(eqTemp, failLabel));
    }

    private void EmitRequireZero(int valueTemp, string failLabel)
    {
        int zeroTemp = NewTemp();
        int eqTemp = NewTemp();
        Emit(new IrInst.LoadConstInt(zeroTemp, 0));
        Emit(new IrInst.CmpIntEq(eqTemp, valueTemp, zeroTemp));
        Emit(new IrInst.JumpIfFalse(eqTemp, failLabel));
    }

    private void EmitRequireNonZero(int valueTemp, string failLabel)
    {
        int zeroTemp = NewTemp();
        int neTemp = NewTemp();
        Emit(new IrInst.LoadConstInt(zeroTemp, 0));
        Emit(new IrInst.CmpIntNe(neTemp, valueTemp, zeroTemp));
        Emit(new IrInst.JumpIfFalse(neTemp, failLabel));
    }

    private void EmitRequireIntEqual(int valueTemp, long expected, string failLabel)
    {
        int expectedTemp = NewTemp();
        int cmpTemp = NewTemp();
        Emit(new IrInst.LoadConstInt(expectedTemp, expected));
        Emit(new IrInst.CmpIntEq(cmpTemp, valueTemp, expectedTemp));
        Emit(new IrInst.JumpIfFalse(cmpTemp, failLabel));
    }

    private void EmitRequireStrEqual(int valueTemp, string expected, string failLabel)
    {
        var label = InternString(expected);
        int expectedTemp = NewTemp();
        int cmpTemp = NewTemp();
        Emit(new IrInst.LoadConstStr(expectedTemp, label));
        Emit(new IrInst.CmpStrEq(cmpTemp, valueTemp, expectedTemp));
        Emit(new IrInst.JumpIfFalse(cmpTemp, failLabel));
    }

    private void EmitRequireBoolEqual(int valueTemp, bool expected, string failLabel)
    {
        // Booleans are represented as integers (0 = false, 1 = true).
        int expectedTemp = NewTemp();
        int cmpTemp = NewTemp();
        Emit(new IrInst.LoadConstBool(expectedTemp, expected));
        Emit(new IrInst.CmpIntEq(cmpTemp, valueTemp, expectedTemp));
        Emit(new IrInst.JumpIfFalse(cmpTemp, failLabel));
    }

    // ---------------- Type vars + unification ----------------

    private TypeRef NewTypeVar()
    {
        return new TypeRef.TVar(_nextTypeVar++);
    }

    // Collect the IDs of all unbound type variables in t.
    private void FtvType(TypeRef t, HashSet<int> result)
    {
        t = Prune(t);
        switch (t)
        {
            case TypeRef.TVar v:
                result.Add(v.Id);
                break;
            case TypeRef.TFun f:
                FtvType(f.Arg, result);
                FtvType(f.Ret, result);
                break;
            case TypeRef.TList l:
                FtvType(l.Element, result);
                break;
            case TypeRef.TTuple tuple:
                foreach (var e in tuple.Elements)
                {
                    FtvType(e, result);
                }
                break;
            case TypeRef.TNamedType n:
                foreach (var a in n.TypeArgs)
                {
                    FtvType(a, result);
                }

                break;
        }
    }

    // Collect the IDs of all free (non-quantified) type variables across all bindings in the current scope.
    private void FtvEnv(HashSet<int> result)
    {
        foreach (var binding in _scopes.Peek().Values)
        {
            if (binding is Binding.Scheme s)
            {
                // Free vars of a scheme are ftv(body) minus the quantified var IDs.
                var bodyFtv = new HashSet<int>();
                FtvType(s.S.Body, bodyFtv);
                foreach (var qv in s.S.Quantified)
                {
                    bodyFtv.Remove(qv.Id);
                }

                result.UnionWith(bodyFtv);
            }
            else if (binding is Binding.EnvScheme es)
            {
                AddSchemeFtv(es.S, result);
            }
            else if (binding is Binding.Intrinsic intrinsic)
            {
                AddSchemeFtv(intrinsic.S, result);
            }
            else if (binding is Binding.PreludeValue preludeValue)
            {
                AddSchemeFtv(preludeValue.S, result);
            }
            else
            {
                FtvType(binding.Type, result);
            }
        }
    }

    private void AddSchemeFtv(TypeScheme scheme, HashSet<int> result)
    {
        var bodyFtv = new HashSet<int>();
        FtvType(scheme.Body, bodyFtv);
        foreach (var qv in scheme.Quantified)
        {
            bodyFtv.Remove(qv.Id);
        }

        result.UnionWith(bodyFtv);
    }

    // Generalize t over free type variables not fixed by the current environment.
    private TypeScheme Generalize(TypeRef t)
    {
        var typeFtv = new HashSet<int>();
        FtvType(t, typeFtv);
        var envFtv = new HashSet<int>();
        FtvEnv(envFtv);
        typeFtv.ExceptWith(envFtv);

        var quantified = typeFtv
            .OrderBy(id => id)
            .Select(id => new TypeVar(id, $"t{id}"))
            .ToList();
        return new TypeScheme(quantified, t);
    }

    // Instantiate a scheme: replace each quantified variable with a fresh type variable.
    private TypeRef Instantiate(TypeScheme scheme)
    {
        if (scheme.Quantified.Count == 0)
        {
            return scheme.Body;
        }

        var subst = new Dictionary<int, TypeRef>(scheme.Quantified.Count);
        foreach (var tv in scheme.Quantified)
        {
            subst[tv.Id] = NewTypeVar();
        }

        return ApplyInstSubst(scheme.Body, subst);
    }

    // Apply an instantiation substitution (mapping old TVar IDs to fresh TypeRefs) to a type.
    private TypeRef ApplyInstSubst(TypeRef t, Dictionary<int, TypeRef> subst)
    {
        t = Prune(t);
        return t switch
        {
            TypeRef.TVar v => subst.TryGetValue(v.Id, out var r) ? r : t,
            TypeRef.TFun f => new TypeRef.TFun(ApplyInstSubst(f.Arg, subst), ApplyInstSubst(f.Ret, subst)),
            TypeRef.TList l => new TypeRef.TList(ApplyInstSubst(l.Element, subst)),
            TypeRef.TTuple tuple => new TypeRef.TTuple(tuple.Elements.Select(e => ApplyInstSubst(e, subst)).ToList()),
            TypeRef.TNamedType n => new TypeRef.TNamedType(n.Symbol, n.TypeArgs.Select(a => ApplyInstSubst(a, subst)).ToList()),
            _ => t
        };
    }

    private TypeRef Prune(TypeRef t)
    {
        if (t is TypeRef.TVar v && _subst.TryGetValue(v.Id, out var r))
        {
            var pr = Prune(r);
            _subst[v.Id] = pr;
            return pr;
        }
        return t;
    }

    private void Unify(TypeRef a, TypeRef b)
    {
        a = Prune(a);
        b = Prune(b);

        if (a is TypeRef.TNever || b is TypeRef.TNever)
        {
            return;
        }

        if (a.Equals(b))
        {
            return;
        }

        if (a is TypeRef.TVar va)
        {
            if (Occurs(va.Id, b))
            {
                ReportDiagnostic(0, "Occurs check failed (recursive type).");
                return;
            }
            _subst[va.Id] = b;
            return;
        }

        if (b is TypeRef.TVar vb)
        {
            Unify(b, a);
            return;
        }

        if (a is TypeRef.TFun fa && b is TypeRef.TFun fb)
        {
            Unify(fa.Arg, fb.Arg);
            Unify(fa.Ret, fb.Ret);
            return;
        }

        if (a is TypeRef.TList la && b is TypeRef.TList lb)
        {
            Unify(la.Element, lb.Element);
            return;
        }

        if (a is TypeRef.TTuple ta && b is TypeRef.TTuple tb)
        {
            if (ta.Elements.Count != tb.Elements.Count)
            {
                var tupleArityMismatch = PrettyPair(a, b);
                ReportDiagnostic(0, $"Type mismatch: {tupleArityMismatch.Left} vs {tupleArityMismatch.Right}.", DiagnosticCodes.TypeMismatch);
                return;
            }

            for (int i = 0; i < ta.Elements.Count; i++)
            {
                Unify(ta.Elements[i], tb.Elements[i]);
            }
            return;
        }

        if (a is TypeRef.TNamedType na && b is TypeRef.TNamedType nb)
        {
            if (!string.Equals(na.Symbol.Name, nb.Symbol.Name, StringComparison.Ordinal))
            {
                var namedTypeMismatch = PrettyPair(a, b);
                ReportDiagnostic(0, $"Type mismatch: {namedTypeMismatch.Left} vs {namedTypeMismatch.Right}.", DiagnosticCodes.TypeMismatch);
                return;
            }

            if (na.TypeArgs.Count != nb.TypeArgs.Count)
            {
                var namedTypeArityMismatch = PrettyPair(a, b);
                ReportDiagnostic(0, $"Type mismatch: {namedTypeArityMismatch.Left} vs {namedTypeArityMismatch.Right}.", DiagnosticCodes.TypeMismatch);
                return;
            }

            for (int i = 0; i < na.TypeArgs.Count; i++)
            {
                Unify(na.TypeArgs[i], nb.TypeArgs[i]);
            }

            return;
        }

        // base mismatch
        var typeMismatch = PrettyPair(a, b);
        ReportDiagnostic(0, $"Type mismatch: {typeMismatch.Left} vs {typeMismatch.Right}.", DiagnosticCodes.TypeMismatch);
    }

    private bool Occurs(int id, TypeRef t)
    {
        t = Prune(t);
        return t switch
        {
            TypeRef.TVar v => v.Id == id,
            TypeRef.TFun f => Occurs(id, f.Arg) || Occurs(id, f.Ret),
            TypeRef.TList l => Occurs(id, l.Element),
            TypeRef.TTuple tuple => tuple.Elements.Any(e => Occurs(id, e)),
            TypeRef.TNamedType n => n.TypeArgs.Any(a => Occurs(id, a)),
            _ => false
        };
    }

    private string Pretty(TypeRef t)
    {
        return Pretty(t, new Dictionary<int, string>(), parentPrecedence: 0);
    }

    private void RecordExprHoverType(Expr expr, TypeRef type)
    {
        RecordHoverType(GetSpan(expr), null, type);
    }

    private void RecordHoverType(TextSpan span, string? name, TypeRef type)
    {
        if (!IsValidSpan(span))
        {
            return;
        }

        _hoverTypes.Add(new HoverTypeInfo(span, name, type));
    }

    private static bool IsValidSpan(TextSpan span)
    {
        return span.Start >= 0 && span.End >= span.Start;
    }

    private static bool ContainsPosition(TextSpan span, int position)
    {
        if (span.Start == span.End)
        {
            return position == span.Start;
        }

        return position >= span.Start && position < span.End;
    }

    private static bool IsBetterHoverCandidate(HoverTypeInfo candidate, HoverTypeInfo current)
    {
        var candidateWidth = candidate.Span.End - candidate.Span.Start;
        var currentWidth = current.Span.End - current.Span.Start;

        if (candidateWidth != currentWidth)
        {
            return candidateWidth < currentWidth;
        }

        var candidateHasName = !string.IsNullOrEmpty(candidate.Name);
        var currentHasName = !string.IsNullOrEmpty(current.Name);
        if (candidateHasName != currentHasName)
        {
            return candidateHasName;
        }

        return candidate.Span.Start >= current.Span.Start;
    }

    private (string Left, string Right) PrettyPair(TypeRef left, TypeRef right)
    {
        var typeVarNames = new Dictionary<int, string>();
        return (
            Pretty(left, typeVarNames, parentPrecedence: 0),
            Pretty(right, typeVarNames, parentPrecedence: 0)
        );
    }

    private string Pretty(TypeRef t, Dictionary<int, string> typeVarNames, int parentPrecedence)
    {
        const int precArrow = 1;
        const int precAtom = 2;

        t = Prune(t);

        var (rendered, precedence) = t switch
        {
            TypeRef.TInt => ("Int", precAtom),
            TypeRef.TFloat => ("Float", precAtom),
            TypeRef.TStr => ("Str", precAtom),
            TypeRef.TBool => ("Bool", precAtom),
            TypeRef.TNever => ("Never", precAtom),
            TypeRef.TList l => ($"List<{Pretty(l.Element, typeVarNames, parentPrecedence: precAtom)}>", precAtom),
            TypeRef.TTuple tuple => ($"({string.Join(", ", tuple.Elements.Select(e => Pretty(e, typeVarNames, parentPrecedence: 0)))})", precAtom),
            TypeRef.TVar v => (GetTypeVarName(v.Id, typeVarNames), precAtom),
            TypeRef.TFun f => (
                $"{Pretty(f.Arg, typeVarNames, parentPrecedence: precAtom)} -> {Pretty(f.Ret, typeVarNames, parentPrecedence: precArrow)}",
                precArrow
            ),
            TypeRef.TNamedType n => n.TypeArgs.Count == 0
                ? (n.Symbol.Name, precAtom)
                : ($"{n.Symbol.Name}<{string.Join(", ", n.TypeArgs.Select(a => Pretty(a, typeVarNames, parentPrecedence: precAtom)))}>", precAtom),
            TypeRef.TTypeParam tp => (tp.Symbol.Name, precAtom),
            _ => (t.GetType().Name, precAtom)
        };

        return precedence < parentPrecedence ? $"({rendered})" : rendered;
    }

    private static string GetTypeVarName(int id, Dictionary<int, string> typeVarNames)
    {
        if (typeVarNames.TryGetValue(id, out var existing))
        {
            return existing;
        }

        var index = typeVarNames.Count;
        var typeVarName = "";
        do
        {
            // Generate a, b, ..., z, aa, ab, ... using spreadsheet-style base-26 naming.
            typeVarName = (char)('a' + (index % 26)) + typeVarName;
            index = (index / 26) - 1;
        } while (index >= 0);

        typeVarNames[id] = typeVarName;
        return typeVarName;
    }

    // ---------------- scopes / helpers ----------------

    private Binding? Lookup(string name)
    {
        return _scopes.Peek().TryGetValue(name, out var b) ? b : null;
    }

    // --- Resource tracking helpers ---

    /// <summary>
    /// Returns true if the given pruned type is a resource type requiring deterministic cleanup.
    /// </summary>
    private static bool IsResourceType(TypeRef prunedType)
    {
        return prunedType is TypeRef.TNamedType named && BuiltinRegistry.IsResourceTypeName(named.Symbol.Name);
    }

    /// <summary>
    /// Returns the owned type name if the type is an owned type (heap-allocated),
    /// otherwise null. Copy types (Int, Float, Bool) return null.
    /// </summary>
    private static string? GetOwnedTypeName(TypeRef prunedType)
    {
        return prunedType switch
        {
            TypeRef.TStr => "String",
            TypeRef.TList => "List",
            TypeRef.TTuple => "Tuple",
            TypeRef.TFun => "Function",
            TypeRef.TNamedType named => named.Symbol.Name,
            _ => null // Copy types (Int, Float, Bool), TNever, TVar, TTypeParam
        };
    }

    /// <summary>
    /// Returns the resource type name if the type is a resource type, otherwise null.
    /// Resource types are a subset of owned types with special cleanup behavior.
    /// </summary>
    private static string? GetResourceTypeName(TypeRef prunedType)
    {
        return prunedType is TypeRef.TNamedType named && BuiltinRegistry.IsResourceTypeName(named.Symbol.Name)
            ? named.Symbol.Name
            : null;
    }

    /// <summary>
    /// Resolves an ownership alias chain to the original owner name.
    /// If the name is not an alias, returns itself.
    /// </summary>
    private string ResolveOwnershipAlias(string name)
    {
        while (_ownershipAliases.TryGetValue(name, out var target))
        {
            name = target;
        }

        return name;
    }

    /// <summary>
    /// Registers an owned binding in the current ownership scope.
    /// Called when a let binding or pattern binding creates an owned-type variable.
    /// </summary>
    private void TrackOwnedValue(string name, int slot, string typeName, bool isResource, TextSpan? definitionSpan)
    {
        if (_ownershipScopes.Count > 0)
        {
            _ownershipScopes.Peek()[name] = new OwnershipInfo(slot, typeName, isResource, definitionSpan);
        }
    }

    /// <summary>
    /// Looks up an owned binding across all ownership scopes.
    /// Resolves ownership aliases so that accessing an alias (e.g. y when `let y = x`)
    /// returns the original owner's info.
    /// </summary>
    private OwnershipInfo? LookupOwnedValue(string name)
    {
        var resolved = ResolveOwnershipAlias(name);
        foreach (var scope in _ownershipScopes)
        {
            if (scope.TryGetValue(resolved, out var info))
            {
                return info;
            }
        }

        return null;
    }

    /// <summary>
    /// Marks an owned value as dropped (explicitly closed / released).
    /// Resolves aliases so that closing an alias marks the original owner as dropped.
    /// Returns true if the operation succeeded (value was alive and is now marked dropped)
    /// or if the name is not a tracked owned value (no-op — safe to call on any binding).
    /// Returns false if the value was already dropped (double-drop detected).
    /// </summary>
    private bool TryMarkDropped(string name)
    {
        var info = LookupOwnedValue(name); // already resolves aliases
        if (info is null)
        {
            return true; // not a tracked owned value — no action needed, returns true to indicate no error
        }

        if (info.IsDropped)
        {
            return false; // already dropped — double-drop
        }

        info.IsDropped = true;
        return true;
    }

    /// <summary>
    /// Emits Drop instructions for all alive (not yet dropped) owned values in the current scope.
    /// Called at scope exit.
    /// </summary>
    private void EmitDropsForCurrentScope()
    {
        if (_ownershipScopes.Count == 0)
        {
            return;
        }

        var scope = _ownershipScopes.Peek();
        foreach (var (_, info) in scope)
        {
            if (!info.IsDropped)
            {
                int loadTemp = NewTemp();
                Emit(new IrInst.LoadLocal(loadTemp, info.Slot));
                Emit(new IrInst.Drop(loadTemp, info.TypeName));
                info.IsDropped = true;
            }
        }
    }

    /// <summary>
    /// Emits SaveArenaState to capture the current heap watermark.
    /// Must be called before any heap allocations that should be covered
    /// by the arena scope. The returned slot pair is pushed onto the
    /// arena watermarks stack and will be popped by <see cref="PopOwnershipScope"/>.
    /// </summary>
    private void EmitArenaWatermark()
    {
        int cursorSlot = NewLocal();
        int endSlot = NewLocal();
        _arenaWatermarks.Push((cursorSlot, endSlot));
        Emit(new IrInst.SaveArenaState(cursorSlot, endSlot));
    }

    /// <summary>
    /// Pushes a new ownership scope. Must be matched with PopOwnershipScope().
    /// Does not emit SaveArenaState — call <see cref="EmitArenaWatermark"/> at the
    /// desired IR position before or after this call. The arena watermark stack
    /// must have one entry per ownership scope for PopOwnershipScope to pair correctly.
    /// </summary>
    private void PushOwnershipScope()
    {
        _ownershipScopes.Push(new Dictionary<string, OwnershipInfo>(StringComparer.Ordinal));
    }

    /// <summary>
    /// Pops an ownership scope, emitting Drop instructions for any remaining alive owned values,
    /// and optionally emitting arena-reset and/or copy-out instructions.
    /// <list type="bullet">
    ///   <item>Copy-type result (Int, Float, Bool): emits RestoreArenaState; returns
    ///     <paramref name="resultTemp"/> unchanged.</item>
    ///   <item>Heap-type result that can be copy-outed (String, List with safe element,
    ///     Closure, ADT with copy-type fields) AND the scope contained alive owned values
    ///     (so there is heap memory worth reclaiming): emits RestoreArenaState followed
    ///     by the appropriate copy-out instruction (CopyOutArena, CopyOutList, or
    ///     CopyOutClosure); returns the new copy-destination temp.</item>
    ///   <item>All other heap types, or heap type with no alive owned values: no arena action;
    ///     returns <paramref name="resultTemp"/> unchanged.</item>
    /// </list>
    /// </summary>
    /// <param name="resultType">The scope result type, used to decide arena action.</param>
    /// <param name="resultTemp">The IR temp holding the scope result (pointer or value).
    ///   Pass -1 if the result temp is unavailable or irrelevant.</param>
    /// <returns>The IR temp to use as the scope result after cleanup.
    ///   For copy-out, this is a newly allocated temp that differs from
    ///   <paramref name="resultTemp"/>. Otherwise it equals <paramref name="resultTemp"/>.</returns>
    private int PopOwnershipScope(TypeRef? resultType = null, int resultTemp = -1)
    {
        bool hadAliveOwned = HasAliveOwnedValuesInCurrentScope();
        EmitDropsForCurrentScope();

        var (cursorSlot, endSlot) = _arenaWatermarks.Pop();

        if (resultType is not null)
        {
            int preRestoreEndSlot = NewLocal();

            if (CanArenaReset(resultType))
            {
                // Copy-type result: arena reset is always safe. No heap values escape.
                Emit(new IrInst.RestoreArenaState(cursorSlot, endSlot, preRestoreEndSlot));
                Emit(new IrInst.ReclaimArenaChunks(endSlot, preRestoreEndSlot));
            }
            else if (hadAliveOwned && resultTemp >= 0)
            {
                var copyOutKind = GetCopyOutKind(resultType, out int staticSizeBytes);
                if (copyOutKind != CopyOutKind.None)
                {
                    Emit(new IrInst.RestoreArenaState(cursorSlot, endSlot, preRestoreEndSlot));
                    int copyDest = NewTemp();
                    switch (copyOutKind)
                    {
                        case CopyOutKind.Shallow:
                            Emit(new IrInst.CopyOutArena(copyDest, resultTemp, staticSizeBytes));
                            break;
                        case CopyOutKind.List:
                            Emit(new IrInst.CopyOutList(copyDest, resultTemp));
                            break;
                        case CopyOutKind.Closure:
                            Emit(new IrInst.CopyOutClosure(copyDest, resultTemp));
                            break;
                    }
                    Emit(new IrInst.ReclaimArenaChunks(endSlot, preRestoreEndSlot));
                    _ownershipScopes.Pop();
                    return copyDest;
                }
            }
            // else: heap type that cannot be copy-outed, or no owned values to reclaim.
            // No arena action; the caller retains the original result pointer.
        }

        _ownershipScopes.Pop();
        return resultTemp;
    }

    /// <summary>
    /// Returns true if the given type is a copy type safe for arena reset.
    /// Copy types (Int, Float, Bool) don't reference heap memory, so restoring
    /// the heap cursor after computing a copy-type result is always safe.
    /// </summary>
    private bool CanArenaReset(TypeRef type)
    {
        var pruned = Prune(type);
        return pruned is TypeRef.TInt or TypeRef.TFloat or TypeRef.TBool;
    }

    /// <summary>
    /// Describes the kind of arena copy-out to emit for a given result type.
    /// </summary>
    private enum CopyOutKind
    {
        /// <summary>Not eligible for copy-out.</summary>
        None,
        /// <summary>Shallow memcpy of a fixed or dynamic-size object (String, ADT, single cons cell).</summary>
        Shallow,
        /// <summary>Deep cons-chain walk for lists.</summary>
        List,
        /// <summary>Closure struct + env copy.</summary>
        Closure,
        /// <summary>TCO-specific: copy one cons cell + copy/deep-copy its head value.</summary>
        TcoListCell,
    }

    /// <summary>
    /// Determines whether the given type's heap representation can be safely copy-outed
    /// after a RestoreArenaState, and what kind of copy-out is needed.
    /// <para>
    /// Handles:
    /// <list type="bullet">
    ///   <item><b>String (TStr):</b> Shallow copy. Layout is {length:i64, bytes…}; all data
    ///     is inline, no internal pointers. <c>staticSizeBytes</c> is -1 (dynamic).</item>
    ///   <item><b>List (TList):</b> Deep cons-chain copy. Safe when element is a copy type
    ///     (Int, Float, Bool) or TStr. Walks tail pointers to copy entire chain.</item>
    ///   <item><b>Closure (TFun):</b> Closure + env copy. Copies the 24-byte closure struct
    ///     and the env block it references.</item>
    ///   <item><b>ADT (TNamedType):</b> Shallow copy of (1 + fieldCount) * 8 bytes. Safe
    ///     when all fields across all constructors are copy types.</item>
    /// </list>
    /// </para>
    /// </summary>
    private CopyOutKind GetCopyOutKind(TypeRef type, out int staticSizeBytes)
    {
        var pruned = Prune(type);
        switch (pruned)
        {
            case TypeRef.TStr:
                staticSizeBytes = -1; // dynamic: 8 (length word) + string.length
                return CopyOutKind.Shallow;

            case TypeRef.TList list when IsCopyOutSafeElement(list.Element):
                staticSizeBytes = 0; // not used — deep copy at runtime
                return CopyOutKind.List;

            case TypeRef.TFun:
                staticSizeBytes = 0;
                return CopyOutKind.None;

            case TypeRef.TNamedType named:
                return CanCopyOutAdt(named, out staticSizeBytes)
                    ? CopyOutKind.Shallow
                    : CopyOutKind.None;

            default:
                staticSizeBytes = 0;
                return CopyOutKind.None;
        }
    }

    /// <summary>
    /// Legacy helper — returns true if the type can be copy-outed via shallow memcpy.
    /// </summary>
    private bool CanCopyOutArena(TypeRef type, out int staticSizeBytes)
    {
        var kind = GetCopyOutKind(type, out staticSizeBytes);
        return kind == CopyOutKind.Shallow;
    }

    /// <summary>
    /// Returns true if a list element type is safe for shallow cons-cell copy-out.
    /// Safe elements are copy types only. Pointer-carrying values such as TStr are
    /// not shallow-copy safe here because copying the cons cells alone would preserve
    /// element references into arena memory that may later be reclaimed.
    /// </summary>
    private bool IsCopyOutSafeElement(TypeRef elementType)
    {
        var pruned = Prune(elementType);
        return CanArenaReset(pruned);
    }

    /// <summary>
    /// Returns true if an ADT type can be safely shallow-copied for arena copy-out.
    /// Requires all constructors to have the same arity (for static-size copy) and
    /// all field types across all constructors to be copy types (inline values, no
    /// heap pointers). Type parameters are substituted with the concrete type arguments
    /// from the instantiated <paramref name="named"/> type.
    /// </summary>
    private bool CanCopyOutAdt(TypeRef.TNamedType named, out int staticSizeBytes)
    {
        staticSizeBytes = 0;
        var sym = named.Symbol;
        if (sym.Constructors.Count == 0)
        {
            return false;
        }

        // All constructors must have the same arity for static-size copy.
        int arity = sym.Constructors[0].Arity;
        for (int i = 1; i < sym.Constructors.Count; i++)
        {
            if (sym.Constructors[i].Arity != arity)
            {
                return false;
            }
        }

        // Build type parameter substitution map: TTypeParam → concrete TypeRef.
        // Constructor parameter types use TTypeParam placeholders (e.g. Box(T) stores
        // TTypeParam("T")), while the instantiated TNamedType has the concrete type
        // arguments (e.g. TNamedType(Box, [TInt])).
        Dictionary<TypeParameterSymbol, TypeRef>? typeParamMap = null;
        if (sym.TypeParameters.Count > 0 && named.TypeArgs.Count == sym.TypeParameters.Count)
        {
            typeParamMap = new Dictionary<TypeParameterSymbol, TypeRef>();
            for (int i = 0; i < sym.TypeParameters.Count; i++)
            {
                typeParamMap[sym.TypeParameters[i]] = named.TypeArgs[i];
            }
        }

        // Check all field types across all constructors are copy types.
        // Pointer-containing fields (TStr, TList, TFun, TNamedType) are not safe
        // because the pointed-to data may be within the freed arena region.
        foreach (var ctor in sym.Constructors)
        {
            foreach (var fieldType in ctor.ParameterTypes)
            {
                var resolved = ResolveFieldType(fieldType, typeParamMap);
                if (!CanArenaReset(resolved))
                {
                    return false;
                }
            }
        }

        staticSizeBytes = (1 + arity) * 8;
        return true;
    }

    /// <summary>
    /// Resolves a constructor field type by substituting type parameters with their
    /// concrete type arguments, then pruning any remaining type variables.
    /// </summary>
    private TypeRef ResolveFieldType(TypeRef fieldType, Dictionary<TypeParameterSymbol, TypeRef>? typeParamMap)
    {
        var pruned = Prune(fieldType);
        if (pruned is TypeRef.TTypeParam tp && typeParamMap is not null
            && typeParamMap.TryGetValue(tp.Symbol, out var concrete))
        {
            return Prune(concrete);
        }
        return pruned;
    }

    /// <summary>
    /// Returns true if the given type can be copy-outed safely after a TCO arena reset,
    /// and determines the appropriate copy-out kind and IR instruction parameters.
    /// <para>
    /// Safe types for TCO copy-out:
    /// <list type="bullet">
    ///   <item><b>String (TStr):</b> Shallow copy — self-contained, no internal heap pointers.</item>
    ///   <item><b>List with copy-type element (TList where element is Int/Float/Bool):</b>
    ///     Copy only the top cons cell (16 bytes) with inline head value; the tail remains in pre-watermark memory.</item>
    ///   <item><b>List with string element (TList(TStr)):</b>
    ///     Copy only the top cons cell; the string head value is also copied, while the tail remains in pre-watermark memory.</item>
    ///   <item><b>List with inner-list element (TList(TList(copy-type))):</b>
    ///     Copy only the top cons cell; the inner-list head value is deep-copied, while the tail remains in pre-watermark memory.</item>
    ///   <item><b>Closure (TFun):</b> Closure struct + env copy (24 bytes + env block).</item>
    ///   <item><b>ADT (TNamedType):</b> Shallow copy when all fields are copy types.</item>
    /// </list>
    /// </para>
    /// </summary>
    private CopyOutKind GetTcoCopyOutKind(TypeRef type, out int staticSizeBytes, out IrInst.ListHeadCopyKind listHeadCopy)
    {
        var pruned = Prune(type);
        listHeadCopy = IrInst.ListHeadCopyKind.Inline;
        switch (pruned)
        {
            case TypeRef.TStr:
                staticSizeBytes = -1; // dynamic: 8 + length
                return CopyOutKind.Shallow;

            case TypeRef.TList list:
                {
                    var elemPruned = Prune(list.Element);
                    if (CanArenaReset(elemPruned))
                    {
                        // Copy-type heads: inline values, single cell shallow copy (16 bytes).
                        staticSizeBytes = 16;
                        return CopyOutKind.Shallow;
                    }
                    if (elemPruned is TypeRef.TStr)
                    {
                        // String heads: copy one cell + copy the string head value.
                        staticSizeBytes = 0;
                        listHeadCopy = IrInst.ListHeadCopyKind.String;
                        return CopyOutKind.TcoListCell;
                    }
                    if (elemPruned is TypeRef.TList inner && CanArenaReset(Prune(inner.Element)))
                    {
                        // Inner list with copy-type elements: copy one cell + deep-copy inner list head.
                        staticSizeBytes = 0;
                        listHeadCopy = IrInst.ListHeadCopyKind.InnerList;
                        return CopyOutKind.TcoListCell;
                    }
                    staticSizeBytes = 0;
                    return CopyOutKind.None;
                }

            case TypeRef.TFun:
                staticSizeBytes = 0;
                return CopyOutKind.Closure;

            case TypeRef.TNamedType named:
                return CanCopyOutAdt(named, out staticSizeBytes)
                    ? CopyOutKind.Shallow
                    : CopyOutKind.None;

            default:
                staticSizeBytes = 0;
                return CopyOutKind.None;
        }
    }

    /// <summary>
    /// Returns true if the current ownership scope contains any alive (not yet dropped) owned values.
    /// </summary>
    private bool HasAliveOwnedValuesInCurrentScope()
    {
        if (_ownershipScopes.Count == 0)
        {
            return false;
        }

        var scope = _ownershipScopes.Peek();
        foreach (var (_, info) in scope)
        {
            if (!info.IsDropped)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Tracks owned bindings created by pattern matching.
    /// Scans pattern bindings for owned types and registers them for tracking.
    /// </summary>
    private void TrackOwnedBindingsInPattern(IReadOnlyDictionary<string, TypeRef> patternBindings)
    {
        foreach (var (name, type) in patternBindings)
        {
            var prunedType = Prune(type);
            var ownedTypeName = GetOwnedTypeName(prunedType);
            if (ownedTypeName is not null)
            {
                // Look up the slot from the current scope
                if (Lookup(name) is Binding.Local local)
                {
                    var isResource = GetResourceTypeName(prunedType) is not null;
                    TrackOwnedValue(name, local.Slot, ownedTypeName, isResource, local.DefinitionSpan);
                }
            }
        }
    }

    /// <summary>
    /// Checks if a resource expression refers to a dropped resource and reports use-after-drop.
    /// Only applies to resource types (Socket), not general owned types.
    /// </summary>
    private void CheckUseAfterDrop(Expr expr)
    {
        if (expr is Expr.Var v)
        {
            var info = LookupOwnedValue(v.Name);
            if (info is not null && info.IsResource && info.IsDropped)
            {
                ReportDiagnostic(GetSpan(expr),
                    $"Resource '{v.Name}' has already been closed. Using a resource after it has been closed is not allowed.",
                    DiagnosticCodes.UseAfterDrop);
            }
        }
    }

    private int NewTemp()
    {
        return _nextTemp++;
    }

    private int NewLocal()
    {
        return _nextLocal++;
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

    private static IEnumerable<string> PatternBindings(Pattern p)
    {
        switch (p)
        {
            case Pattern.Var v:
                if (v.Name != "_")
                {
                    yield return v.Name;
                }

                yield break;
            case Pattern.Cons c:
                foreach (var n in PatternBindings(c.Head))
                {
                    yield return n;
                }

                foreach (var n in PatternBindings(c.Tail))
                {
                    yield return n;
                }

                yield break;
            case Pattern.Tuple tuple:
                foreach (var sub in tuple.Elements)
                {
                    foreach (var n in PatternBindings(sub))
                    {
                        yield return n;
                    }
                }

                yield break;
            case Pattern.Constructor ctor:
                foreach (var sub in ctor.Patterns)
                {
                    foreach (var n in PatternBindings(sub))
                    {
                        yield return n;
                    }
                }

                yield break;
            default:
                yield break;
        }
    }

    private bool IsDefinitelyExhaustive(IEnumerable<MatchCase> cases)
    {
        bool hasEmptyList = false;
        bool hasCons = false;

        foreach (var matchCase in cases)
        {
            if (IsCatchAllPattern(matchCase.Pattern) && matchCase.Guard is null)
            {
                return true;
            }

            switch (matchCase.Pattern)
            {
                case Pattern.EmptyList:
                    hasEmptyList = true;
                    break;
                case Pattern.Cons:
                    hasCons = true;
                    break;
            }
        }

        return hasEmptyList && hasCons;
    }

    /// <summary>
    /// Checks whether boolean patterns cover both true and false.
    /// </summary>
    private static bool IsBoolExhaustive(IReadOnlyList<MatchCase> cases)
    {
        bool hasTrue = false;
        bool hasFalse = false;

        foreach (var matchCase in cases)
        {
            if (matchCase.Guard is not null)
            {
                continue;
            }

            if (matchCase.Pattern is Pattern.BoolLit b)
            {
                if (b.Value) hasTrue = true;
                else hasFalse = true;
            }
            else if (matchCase.Pattern is Pattern.Wildcard or Pattern.Var)
            {
                return true;
            }
        }

        return hasTrue && hasFalse;
    }

    private bool IsCatchAllPattern(Pattern p)
    {
        if (p is Pattern.Wildcard)
        {
            return true;
        }

        if (p is Pattern.Tuple tuple)
        {
            return tuple.Elements.All(IsCatchAllPattern);
        }

        return p is Pattern.Var v && (!_constructorSymbols.TryGetValue(v.Name, out var ctor) || ctor.Arity != 0);
    }

    private IReadOnlyList<string>? GetMissingAdtConstructors(TypeRef valueType, IReadOnlyList<MatchCase> cases)
    {
        if (valueType is not TypeRef.TNamedType namedType)
        {
            return null;
        }

        if (cases.Any(c => IsCatchAllPattern(c.Pattern) && c.Guard is null))
        {
            return [];
        }

        var seenConstructors = new HashSet<string>(StringComparer.Ordinal);
        foreach (var matchCase in cases)
        {
            if (matchCase.Guard is not null)
            {
                continue;
            }

            if (TryGetConstructorSymbol(matchCase.Pattern, out var ctor) &&
                string.Equals(ctor.ParentType, namedType.Symbol.Name, StringComparison.Ordinal))
            {
                seenConstructors.Add(ctor.Name);
            }
        }

        return namedType.Symbol.Constructors
            .Select(c => c.Name)
            .Where(name => !seenConstructors.Contains(name))
            .ToList();
    }

    private IReadOnlyList<string>? GetMissingListCases(TypeRef valueType, IReadOnlyList<MatchCase> cases)
    {
        if (valueType is not TypeRef.TList)
        {
            return null;
        }

        if (cases.Any(c => IsCatchAllPattern(c.Pattern) && c.Guard is null))
        {
            return [];
        }

        bool hasEmptyList = false;
        bool hasCons = false;

        foreach (var matchCase in cases)
        {
            switch (matchCase.Pattern)
            {
                case Pattern.EmptyList:
                    hasEmptyList = true;
                    break;
                case Pattern.Cons:
                    hasCons = true;
                    break;
            }
        }

        List<string> missingCases = [];
        if (!hasEmptyList)
        {
            missingCases.Add("[]");
        }

        if (!hasCons)
        {
            missingCases.Add("x :: xs");
        }

        return missingCases;
    }

    private bool TryGetConstructorSymbol(Pattern p, out ConstructorSymbol ctor)
    {
        ctor = default!;
        if (p is Pattern.Constructor ctorPattern && _constructorSymbols.TryGetValue(ctorPattern.Name, out var ctorPatternSymbol))
        {
            ctor = ctorPatternSymbol;
            return true;
        }

        if (p is Pattern.Var v && _constructorSymbols.TryGetValue(v.Name, out var varPatternSymbol) && varPatternSymbol.Arity == 0)
        {
            ctor = varPatternSymbol;
            return true;
        }

        return false;
    }

    private bool HasConstructorPattern(IEnumerable<MatchCase> cases)
    {
        foreach (var matchCase in cases)
        {
            if (matchCase.Pattern is Pattern.Constructor)
            {
                return true;
            }

            if (TryGetConstructorSymbol(matchCase.Pattern, out _))
            {
                return true;
            }
        }

        return false;
    }

    private bool TryGetMissingPattern(TypeRef valueType, IReadOnlyList<Pattern> patterns, out Pattern missingPattern)
    {
        missingPattern = new Pattern.Wildcard();
        if (patterns.Any(ContainsUnknownConstructorPattern))
        {
            return false;
        }

        return TryGetMissingPatternCore(valueType, patterns, out missingPattern);
    }

    private bool TryGetMissingPatternCore(TypeRef? valueType, IReadOnlyList<Pattern> patterns, out Pattern missingPattern)
    {
        missingPattern = new Pattern.Wildcard();

        if (patterns.Any(IsCatchAllPattern))
        {
            return false;
        }

        valueType = valueType is null ? null : Prune(valueType);

        if (TryGetMissingListPattern(valueType, patterns, out missingPattern))
        {
            return true;
        }

        if (TryGetMissingTuplePattern(valueType, patterns, out missingPattern))
        {
            return true;
        }

        if (TryGetMissingAdtPattern(valueType, patterns, out missingPattern))
        {
            return true;
        }

        if (TryGetMissingBoolPattern(valueType, patterns, out missingPattern))
        {
            return true;
        }

        // Int and string literal patterns have infinite domains — if there are only
        // literal patterns and no catch-all, the match is non-exhaustive.
        if (TryGetMissingLiteralPattern(patterns, out missingPattern))
        {
            return true;
        }

        return false;
    }

    private bool TryGetMissingListPattern(TypeRef? valueType, IReadOnlyList<Pattern> patterns, out Pattern missingPattern)
    {
        missingPattern = new Pattern.Wildcard();
        var isListDomain = valueType is TypeRef.TList || patterns.Any(p => p is Pattern.EmptyList or Pattern.Cons);
        if (!isListDomain)
        {
            return false;
        }

        var consPatterns = patterns.OfType<Pattern.Cons>().ToList();
        if (!patterns.Any(p => p is Pattern.EmptyList))
        {
            missingPattern = new Pattern.EmptyList();
            return true;
        }

        if (consPatterns.Count == 0)
        {
            missingPattern = new Pattern.Cons(new Pattern.Wildcard(), new Pattern.Wildcard());
            return true;
        }

        var listTypeContext = valueType as TypeRef.TList;
        if (TryGetMissingPatternCore(
            listTypeContext?.Element,
            consPatterns.Select(c => c.Head).ToList(),
            out var missingHead))
        {
            missingPattern = new Pattern.Cons(missingHead, new Pattern.Wildcard());
            return true;
        }

        if (TryGetMissingPatternCore(
            // The tail of a cons pattern is itself a list.
            listTypeContext,
            consPatterns.Select(c => c.Tail).ToList(),
            out var missingTail))
        {
            missingPattern = new Pattern.Cons(new Pattern.Wildcard(), missingTail);
            return true;
        }

        return false;
    }

    private bool TryGetMissingTuplePattern(TypeRef? valueType, IReadOnlyList<Pattern> patterns, out Pattern missingPattern)
    {
        missingPattern = new Pattern.Wildcard();

        int? tupleArity = valueType is TypeRef.TTuple tupleType
            ? tupleType.Elements.Count
            : patterns.OfType<Pattern.Tuple>().Select(t => (int?)t.Elements.Count).FirstOrDefault();
        if (tupleArity is null)
        {
            return false;
        }

        var tuplePatterns = patterns
            .OfType<Pattern.Tuple>()
            .Where(t => t.Elements.Count == tupleArity.Value)
            .ToList();
        if (tuplePatterns.Count == 0)
        {
            missingPattern = new Pattern.Tuple(Enumerable.Repeat<Pattern>(new Pattern.Wildcard(), tupleArity.Value).ToList());
            return true;
        }

        // Conservative approximation: report the first tuple element dimension with a missing subpattern
        // and use wildcards for the remaining dimensions.
        for (int i = 0; i < tupleArity.Value; i++)
        {
            TypeRef? elementType = valueType is TypeRef.TTuple tupleValueType ? tupleValueType.Elements[i] : null;
            if (TryGetMissingPatternCore(elementType, tuplePatterns.Select(t => t.Elements[i]).ToList(), out var missingElement))
            {
                var elements = Enumerable.Repeat<Pattern>(new Pattern.Wildcard(), tupleArity.Value).ToArray();
                elements[i] = missingElement;
                missingPattern = new Pattern.Tuple(elements);
                return true;
            }
        }

        return false;
    }

    private bool TryGetMissingAdtPattern(TypeRef? valueType, IReadOnlyList<Pattern> patterns, out Pattern missingPattern)
    {
        missingPattern = new Pattern.Wildcard();

        var constructors = GetAdtConstructorsForPatterns(valueType, patterns);
        if (constructors is null)
        {
            return false;
        }

        foreach (var ctor in constructors)
        {
            var ctorPatterns = patterns.Where(p => IsPatternForConstructor(p, ctor)).ToList();
            if (ctorPatterns.Count == 0)
            {
                missingPattern = CreateMissingConstructorPattern(ctor, -1, null);
                return true;
            }

            if (ctor.Arity == 0)
            {
                continue;
            }

            var ctorWithArgs = ctorPatterns.OfType<Pattern.Constructor>().ToList();
            for (int i = 0; i < ctor.Arity; i++)
            {
                if (TryGetMissingPatternCore(
                    null,
                    ctorWithArgs.Select(c => c.Patterns[i]).ToList(),
                    out var missingField))
                {
                    missingPattern = CreateMissingConstructorPattern(ctor, i, missingField);
                    return true;
                }
            }
        }

        return false;
    }

    private IReadOnlyList<ConstructorSymbol>? GetAdtConstructorsForPatterns(TypeRef? valueType, IReadOnlyList<Pattern> patterns)
    {
        if (valueType is TypeRef.TNamedType namedType)
        {
            return namedType.Symbol.Constructors;
        }

        var constructorSymbols = patterns
            .Select(p => TryGetConstructorSymbol(p, out var ctor) ? ctor : null)
            .OfType<ConstructorSymbol>()
            .ToList();
        if (constructorSymbols.Count == 0)
        {
            return null;
        }

        var adtName = constructorSymbols[0].ParentType;
        if (constructorSymbols.Any(c => !string.Equals(c.ParentType, adtName, StringComparison.Ordinal)))
        {
            return null;
        }

        return _typeSymbols[adtName].Constructors;
    }

    private bool TryGetMissingBoolPattern(TypeRef? valueType, IReadOnlyList<Pattern> patterns, out Pattern missingPattern)
    {
        missingPattern = new Pattern.Wildcard();

        // Only apply when value type is Bool or patterns contain boolean literals
        bool isBoolType = valueType is TypeRef.TBool;
        bool hasBoolPatterns = patterns.Any(p => p is Pattern.BoolLit);
        if (!isBoolType && !hasBoolPatterns)
        {
            return false;
        }

        bool hasTrue = false;
        bool hasFalse = false;

        foreach (var p in patterns)
        {
            if (IsCatchAllPattern(p)) return false;
            if (p is Pattern.BoolLit b)
            {
                if (b.Value) hasTrue = true;
                else hasFalse = true;
            }
        }

        if (!hasTrue)
        {
            missingPattern = new Pattern.BoolLit(true);
            return true;
        }

        if (!hasFalse)
        {
            missingPattern = new Pattern.BoolLit(false);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Detects non-exhaustive matches over integer or string literal patterns.
    /// Since int and string domains are infinite, any set of literal patterns
    /// without a catch-all is non-exhaustive. Reports a wildcard as the missing case.
    /// </summary>
    private static bool TryGetMissingLiteralPattern(IReadOnlyList<Pattern> patterns, out Pattern missingPattern)
    {
        missingPattern = new Pattern.Wildcard();
        if (patterns.Any(p => p is Pattern.IntLit or Pattern.StrLit))
        {
            // Already checked for catch-all in the caller — reaching here means
            // there are literal patterns without a catch-all, which is non-exhaustive.
            return true;
        }

        return false;
    }

    private bool IsPatternForConstructor(Pattern pattern, ConstructorSymbol ctor)
    {
        if (pattern is Pattern.Constructor ctorPattern)
        {
            return string.Equals(ctorPattern.Name, ctor.Name, StringComparison.Ordinal);
        }

        return pattern is Pattern.Var varPattern &&
               ctor.Arity == 0 &&
               string.Equals(varPattern.Name, ctor.Name, StringComparison.Ordinal);
    }

    private Pattern CreateMissingConstructorPattern(ConstructorSymbol ctor, int missingFieldIndex, Pattern? missingFieldPattern)
    {
        if (ctor.Arity == 0)
        {
            return new Pattern.Var(ctor.Name);
        }

        var args = Enumerable.Repeat<Pattern>(new Pattern.Wildcard(), ctor.Arity).ToArray();
        if (missingFieldIndex >= 0 && missingFieldIndex < args.Length && missingFieldPattern is not null)
        {
            args[missingFieldIndex] = missingFieldPattern;
        }

        return new Pattern.Constructor(ctor.Name, args);
    }

    private bool ContainsUnknownConstructorPattern(Pattern pattern)
    {
        switch (pattern)
        {
            case Pattern.Constructor ctor:
                return !_constructorSymbols.ContainsKey(ctor.Name) || ctor.Patterns.Any(ContainsUnknownConstructorPattern);
            case Pattern.Cons cons:
                return ContainsUnknownConstructorPattern(cons.Head) || ContainsUnknownConstructorPattern(cons.Tail);
            case Pattern.Tuple tuple:
                return tuple.Elements.Any(ContainsUnknownConstructorPattern);
            default:
                return false;
        }
    }

    private static string FormatPattern(Pattern pattern)
    {
        return pattern switch
        {
            Pattern.EmptyList => "[]",
            Pattern.Wildcard => "_",
            Pattern.Var v => v.Name,
            Pattern.Cons cons => $"{FormatPattern(cons.Head)} :: {FormatPattern(cons.Tail)}",
            Pattern.Tuple tuple => $"({string.Join(", ", tuple.Elements.Select(FormatPattern))})",
            Pattern.Constructor ctor => ctor.Patterns.Count == 0
                ? ctor.Name
                : $"{ctor.Name}({string.Join(", ", ctor.Patterns.Select(FormatPattern))})",
            Pattern.IntLit intLit => intLit.Value.ToString(),
            Pattern.StrLit strLit => $"\"{strLit.Value}\"",
            Pattern.BoolLit boolLit => boolLit.Value ? "true" : "false",
            _ => "_"
        };
    }

    private string? TryGetConstructorAdtName(Pattern p)
    {
        if (TryGetConstructorSymbol(p, out var ctor))
        {
            return ctor.ParentType;
        }

        return null;
    }

    private void ValidateSingleAdtMatch(IReadOnlyList<MatchCase> cases)
    {
        var adtNames = cases
            .Select(c => TryGetConstructorAdtName(c.Pattern))
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (adtNames.Count > 1)
        {
            ReportDiagnostic(GetSpan(cases[0].Pattern), $"Constructor patterns from different ADTs ({string.Join(", ", adtNames.Select(n => $"'{n}'"))}) cannot appear in the same match expression.");
        }
    }

    private void ValidateReachableMatchArms(IReadOnlyList<MatchCase> cases)
    {
        var seenConstructors = new HashSet<string>(StringComparer.Ordinal);
        var seenIntLiterals = new HashSet<long>();
        var seenStrLiterals = new HashSet<string>(StringComparer.Ordinal);
        var seenBoolTrue = false;
        var seenBoolFalse = false;
        var hasCatchAll = false;

        foreach (var matchCase in cases)
        {
            if (hasCatchAll)
            {
                ReportDiagnostic(GetSpan(matchCase.Pattern), "Unreachable match arm: a catch-all pattern was already matched earlier.");
                continue;
            }

            if (IsCatchAllPattern(matchCase.Pattern) && matchCase.Guard is null)
            {
                hasCatchAll = true;
                continue;
            }

            switch (matchCase.Pattern)
            {
                case Pattern.IntLit intLit:
                    if (!seenIntLiterals.Add(intLit.Value))
                    {
                        ReportDiagnostic(GetSpan(matchCase.Pattern), $"Unreachable match arm: integer literal {intLit.Value} is already matched earlier.");
                    }
                    continue;
                case Pattern.StrLit strLit:
                    if (!seenStrLiterals.Add(strLit.Value))
                    {
                        ReportDiagnostic(GetSpan(matchCase.Pattern), $"Unreachable match arm: string literal \"{strLit.Value}\" is already matched earlier.");
                    }
                    continue;
                case Pattern.BoolLit boolLit:
                    if (boolLit.Value && seenBoolTrue)
                    {
                        ReportDiagnostic(GetSpan(matchCase.Pattern), "Unreachable match arm: 'true' is already matched earlier.");
                    }
                    else if (!boolLit.Value && seenBoolFalse)
                    {
                        ReportDiagnostic(GetSpan(matchCase.Pattern), "Unreachable match arm: 'false' is already matched earlier.");
                    }
                    if (boolLit.Value) seenBoolTrue = true;
                    else seenBoolFalse = true;
                    continue;
            }

            if (!TryGetConstructorSymbol(matchCase.Pattern, out var ctor))
            {
                continue;
            }

            // Payload constructors may need multiple arms for nested refinements (e.g. Some([]), Some(_ :: _)).
            // We still track all constructors in seenConstructors so that truly duplicate payload arms
            // can be detected after inspecting their nested patterns.
            var isNewConstructor = seenConstructors.Add(ctor.Name);
            if (ctor.Arity == 0 && !isNewConstructor)
            {
                ReportDiagnostic(GetSpan(matchCase.Pattern), $"Unreachable match arm: constructor {ctor.Name} is already matched earlier.");
            }
        }
    }

    private string FormatConstructorShape(ConstructorSymbol ctor)
    {
        if (ctor.Arity == 0)
        {
            return ctor.Name;
        }

        return $"{ctor.Name}({string.Join(", ", ctor.ParameterTypes.Select(FormatConstructorParameterType))})";
    }

    private TypeRef InstantiateConstructorParameterType(ConstructorSymbol ctor, int parameterIndex, TypeRef.TNamedType resultType)
    {
        var typeSym = _typeSymbols[ctor.ParentType];
        var typeParameterMap = CreateTypeParameterMap(typeSym, resultType.TypeArgs);
        return SubstituteTypeParameters(ctor.ParameterTypes[parameterIndex], typeParameterMap);
    }

    private static TypeRef SubstituteTypeParameters(TypeRef type, IReadOnlyDictionary<string, TypeRef> typeParameterMap)
    {
        return type switch
        {
            TypeRef.TTypeParam tp when typeParameterMap.TryGetValue(tp.Symbol.Name, out var replacement) => replacement,
            TypeRef.TList list => new TypeRef.TList(SubstituteTypeParameters(list.Element, typeParameterMap)),
            TypeRef.TTuple tuple => new TypeRef.TTuple(tuple.Elements.Select(element => SubstituteTypeParameters(element, typeParameterMap)).ToList()),
            TypeRef.TFun fun => new TypeRef.TFun(
                SubstituteTypeParameters(fun.Arg, typeParameterMap),
                SubstituteTypeParameters(fun.Ret, typeParameterMap)),
            TypeRef.TNamedType named => new TypeRef.TNamedType(
                named.Symbol,
                named.TypeArgs.Select(typeArg => SubstituteTypeParameters(typeArg, typeParameterMap)).ToList()),
            _ => type
        };
    }

    private static string FormatConstructorParameterType(TypeRef type)
    {
        return type switch
        {
            TypeRef.TInt => "Int",
            TypeRef.TFloat => "Float",
            TypeRef.TStr => "Str",
            TypeRef.TBool => "Bool",
            TypeRef.TNever => "Never",
            TypeRef.TTypeParam tp => tp.Symbol.Name,
            TypeRef.TList list => $"List<{FormatConstructorParameterType(list.Element)}>",
            TypeRef.TTuple tuple => $"({string.Join(", ", tuple.Elements.Select(FormatConstructorParameterType))})",
            TypeRef.TFun fun => $"{FormatConstructorParameterType(fun.Arg)} -> {FormatConstructorParameterType(fun.Ret)}",
            TypeRef.TNamedType named when named.TypeArgs.Count == 0 => named.Symbol.Name,
            TypeRef.TNamedType named => $"{named.Symbol.Name}<{string.Join(", ", named.TypeArgs.Select(FormatConstructorParameterType))}>",
            _ => type.GetType().Name
        };
    }

    /// <summary>
    /// Returns a dummy (int 0) temp with type <see cref="TypeRef.TNever"/>.
    /// Used as a sentinel return value after emitting a diagnostic so that
    /// downstream code can detect and suppress cascading type errors.
    /// </summary>
    private (int Temp, TypeRef Type) ReturnNeverWithDummyTemp()
    {
        int t = NewTemp();
        Emit(new IrInst.LoadConstInt(t, 0));
        return (t, new TypeRef.TNever());
    }

    private string BuildUnknownConstructorHint(string name)
    {
        if (_constructorSymbols.Count == 0)
        {
            return "";
        }

        // Only suggest constructors within a reasonable edit-distance threshold
        // to avoid surfacing very dissimilar names as suggestions.
        int threshold = Math.Max(3, name.Length / 2);
        var candidates = _constructorSymbols.Keys
            .Select(k => (Name: k, Dist: EditDistance(name, k)))
            .Where(x => x.Dist <= threshold)
            .OrderBy(x => x.Dist)
            .Take(3)
            .Select(x => x.Name)
            .ToList();

        if (candidates.Count == 0)
        {
            return "";
        }

        return $" Did you mean: {string.Join(", ", candidates)}?";
    }

    private static string BuildUnknownVariableHint(string name)
    {
        foreach (var moduleName in BuiltinRegistry.StandardModuleNames)
        {
            if (!BuiltinRegistry.TryGetModule(moduleName, out var module))
            {
                continue;
            }

            if (module.Members.ContainsKey(name))
            {
                return $" Did you mean '{moduleName}.{name}'?";
            }
        }

        return "";
    }

    /// <summary>
    /// Computes the Levenshtein edit distance between two strings.
    /// Used to rank constructor name suggestions for diagnostic hints.
    /// </summary>
    private static int EditDistance(string a, string b)
    {
        int m = a.Length, n = b.Length;
        var d = new int[m + 1, n + 1];
        for (int i = 0; i <= m; i++)
        {
            d[i, 0] = i;
        }

        for (int j = 0; j <= n; j++)
        {
            d[0, j] = j;
        }

        for (int i = 1; i <= m; i++)
        {
            for (int j = 1; j <= n; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
            }
        }

        return d[m, n];
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
}
