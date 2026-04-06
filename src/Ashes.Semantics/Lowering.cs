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

    private bool _usesPrintInt;
    private bool _usesPrintStr;
    private bool _usesPrintBool;
    private bool _usesConcatStr;
    private bool _usesClosures;
    private readonly List<HoverTypeInfo> _hoverTypes = [];

    // Source location tracking for debug info (Phase 0c)
    private string? _currentFilePath;
    private int[]? _lineStarts;
    private int _sourceLength;
    private Expr? _currentSourceExpr;
    private IReadOnlyList<(string FilePath, int StartOffset, int EndOffset)>? _moduleOffsets;
    private int[][]? _moduleLineStarts;

    private readonly bool _hasAshesIO;
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
    }

    private TcoContext? _tcoCtx;

    private enum IntrinsicKind
    {
        Print,
        Panic
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

        public sealed record Self(string FuncLabel, TypeRef T, TextSpan? Span = null) : Binding(T)
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

    // --- Resource tracking for Phase 1: Deterministic Resources ---
    // Tracks resource bindings and their drop state.
    // Key: binding name, Value: resource info (slot, type name, whether dropped).
    private sealed class ResourceInfo(int slot, string resourceTypeName, TextSpan? definitionSpan)
    {
        public int Slot { get; } = slot;
        public string ResourceTypeName { get; } = resourceTypeName;
        public TextSpan? DefinitionSpan { get; } = definitionSpan;
        public bool IsDropped { get; set; }
    }

    // Stack of resource scopes, parallel to _scopes.
    // Each scope level tracks resources introduced at that level.
    private readonly Stack<Dictionary<string, ResourceInfo>> _resourceScopes = new();

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

    public Lowering(Diagnostics diag, IReadOnlySet<string>? importedStdModules = null)
    {
        _diag = diag;
        _hasAshesIO = importedStdModules?.Contains("Ashes.IO") == true;
        RegisterBuiltinSymbols();
        var rootScope = new Dictionary<string, Binding>(StringComparer.Ordinal);
        if (_hasAshesIO)
        {
            AddStdIOBindings(rootScope);
        }
        _scopes.Push(rootScope);
        _resourceScopes.Push(new Dictionary<string, ResourceInfo>(StringComparer.Ordinal));
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
            HasEnvAndArgParams: false
        );

        return new IrProgram(
            EntryFunction: entry,
            Functions: _funcs,
            StringLiterals: _strings,
            UsesPrintInt: _usesPrintInt,
            UsesPrintStr: _usesPrintStr,
            UsesPrintBool: _usesPrintBool,
            UsesConcatStr: _usesConcatStr,
            UsesClosures: _usesClosures
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
                Emit(new IrInst.MakeClosure(temp, self.FuncLabel, envTemp));
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
        return result;
    }

    private (int, TypeRef) LowerProgramArgs(int target, TypeRef type)
    {
        Emit(new IrInst.LoadProgramArgs(target));
        return (target, type);
    }

    private (int, TypeRef) LowerQualifiedVar(Expr.QualifiedVar qv)
    {
        if (BuiltinRegistry.TryGetModule(qv.Module, out var builtinModule))
        {
            if (builtinModule.Members.ContainsKey(qv.Name))
            {
                var resolvedStdMember = ResolveBuiltinModuleMember(builtinModule, qv.Name);
                RecordHoverType(GetSpan(qv), $"{qv.Module}.{qv.Name}", resolvedStdMember.Item2);
                return resolvedStdMember;
            }

            if (builtinModule.ResourceName is null)
            {
                return StdMemberNotFound(qv.Module, qv.Name, GetSpan(qv));
            }
        }

        var sanitizedModuleName = ProjectSupport.SanitizeModuleBindingName(qv.Module);
        var exportedBindingName = $"{sanitizedModuleName}_{qv.Name}";
        if (Lookup(exportedBindingName) is not null)
        {
            var resolvedQualifiedBinding = LowerVar(new Expr.Var(exportedBindingName));
            RecordHoverType(GetSpan(qv), $"{qv.Module}.{qv.Name}", resolvedQualifiedBinding.Item2);
            return resolvedQualifiedBinding;
        }

        // User module: resolve to the sanitized module binding if it exists.
        var binding = Lookup(qv.Module) ?? Lookup(sanitizedModuleName);
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

        var (valTemp, valType) = LowerExpr(let.Value);

        int slot = NewLocal();
        Emit(new IrInst.StoreLocal(slot, valTemp));

        var scheme = Generalize(Prune(valType));
        RecordHoverType(AstSpans.GetLetNameOrDefault(let), let.Name, scheme.Body);

        var parent = _scopes.Peek();
        var child = new Dictionary<string, Binding>(parent, StringComparer.Ordinal)
        {
            [let.Name] = new Binding.Scheme(slot, scheme, AstSpans.GetLetNameOrDefault(let))
        };
        _scopes.Push(child);

        // Track resource bindings for deterministic cleanup
        PushResourceScope();
        var resourceTypeName = GetResourceTypeName(Prune(valType));
        if (resourceTypeName is not null)
        {
            TrackResource(let.Name, slot, resourceTypeName, AstSpans.GetLetNameOrDefault(let));
        }

        // Body IS in tail position (if the let itself is)
        if (_tcoCtx is not null) _tcoCtx.InTailPosition = savedTailPos;
        var (bodyTemp, bodyType) = LowerExpr(let.Body);

        // Only emit result preservation when there are alive resources to drop
        if (HasAliveResourcesInCurrentScope())
        {
            int resultSlot = NewLocal();
            Emit(new IrInst.StoreLocal(resultSlot, bodyTemp));
            PopResourceScope();
            int resultTemp = NewTemp();
            Emit(new IrInst.LoadLocal(resultTemp, resultSlot));
            _scopes.Pop();
            return (resultTemp, bodyType);
        }

        PopResourceScope();
        _scopes.Pop();
        return (bodyTemp, bodyType);
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
        var unitType = _resolvedTypes["Unit"];

        if (t is TypeRef.TNever)
        {
            return (vTemp, t);
        }

        if (t is TypeRef.TInt)
        {
            _usesPrintInt = true;
            Emit(new IrInst.PrintInt(vTemp));
            return (vTemp, unitType);
        }

        if (t is TypeRef.TStr)
        {
            _usesPrintStr = true;
            Emit(new IrInst.PrintStr(vTemp));
            return (vTemp, unitType);
        }

        if (t is TypeRef.TBool)
        {
            _usesPrintBool = true;
            Emit(new IrInst.PrintBool(vTemp));
            return (vTemp, unitType);
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

        var target = NewTemp();
        Emit(new IrInst.NetTcpConnect(target, hostTemp, portTemp));
        return (target, CreateStringResultType(_resolvedTypes["Socket"]));
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

        var target = NewTemp();
        Emit(new IrInst.HttpGet(target, urlTemp));
        return (target, CreateStringResultType(new TypeRef.TStr()));
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

        var target = NewTemp();
        Emit(new IrInst.HttpPost(target, urlTemp, bodyTemp));
        return (target, CreateStringResultType(new TypeRef.TStr()));
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

        var target = NewTemp();
        Emit(new IrInst.NetTcpSend(target, socketTemp, textTemp));
        return (target, CreateStringResultType(new TypeRef.TInt()));
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

        var target = NewTemp();
        Emit(new IrInst.NetTcpReceive(target, socketTemp, maxBytesTemp));
        return (target, CreateStringResultType(new TypeRef.TStr()));
    }

    private (int, TypeRef) LowerNetTcpClose(Expr socketArg)
    {
        using var socketSpan = PushDiagnosticSpan(socketArg);

        // Check for double-drop before lowering the argument
        if (socketArg is Expr.Var v)
        {
            var resourceInfo = LookupResource(v.Name);
            if (resourceInfo is not null && resourceInfo.IsDropped)
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
            TryMarkResourceDropped(varExpr.Name);
        }

        var target = NewTemp();
        Emit(new IrInst.NetTcpClose(target, socketTemp));
        return (target, CreateStringResultType(_resolvedTypes["Unit"]));
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

    private (int, TypeRef) LowerLambda(Expr.Lambda lam)
    {
        return LowerLambdaCore(lam, null, null);
    }

    private (int, TypeRef) LowerLambdaRecursive(string selfName, TypeRef selfType, Expr.Lambda lam)
    {
        return LowerLambdaCore(lam, selfName, selfType);
    }

    private (int, TypeRef) LowerLambdaCore(Expr.Lambda lam, string? selfName, TypeRef? selfType)
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
            Emit(new IrInst.Alloc(envPtrTemp, captures.Count * 8));

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

        // new function state
        _inst.Clear();
        _nextTemp = 0;
        _nextLocal = 0;

        // Lambda function gets implicit locals for env and arg at slots 0 and 1
        int envSlot = NewLocal(); // 0
        int argSlot = NewLocal(); // 1

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
            scope[selfName] = new Binding.Self(label, selfType, Lookup(selfName)?.DefinitionSpan);
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
            HasEnvAndArgParams: true
        );

        // restore state
        _funcs.Add(func);

        _inst.Clear();
        _inst.AddRange(savedInst);
        _nextTemp = savedTemp;
        _nextLocal = savedLocal;
        _scopes.Clear();
        foreach (var s in savedScopes.Reverse())
        {
            _scopes.Push(new Dictionary<string, Binding>(s, StringComparer.Ordinal));
        }

        // Produce closure object: alloc 16 bytes and store (code_ptr, env_ptr)
        int closureTemp = NewTemp();
        Emit(new IrInst.MakeClosure(closureTemp, label, envPtrTemp));
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
            // Type-check: resolve self binding and unify arg types with param types
            var selfBinding = Lookup(tco.SelfName);
            var curType = selfBinding is not null ? Prune(selfBinding.Type) : null;
            for (int i = 0; i < collectedArgs.Count; i++)
            {
                var (argTemp, argType) = LowerExpr(collectedArgs[i]);
                newArgTemps[i] = argTemp;
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
            if (collectedArgs.Count != 1)
            {
                return ReportArityMismatch(rootExpr, 1, collectedArgs.Count);
            }

            return intrinsic.Kind switch
            {
                IntrinsicKind.Print => LowerPrint(collectedArgs[0]),
                IntrinsicKind.Panic => LowerPanic(collectedArgs[0]),
                _ => throw new NotSupportedException($"Unknown intrinsic: {intrinsic.Kind}")
            };
        }

        // Qualified intrinsic call: Ashes.IO.print(...), Ashes.IO.panic(...)
        if (rootExpr is Expr.QualifiedVar qv
            && BuiltinRegistry.TryGetModule(qv.Module, out var builtinModule)
            && builtinModule.Members.TryGetValue(qv.Name, out var builtinMember))
        {
            if (!builtinMember.IsCallable)
            {
                ReportDiagnostic(GetSpan(qv), $"'{qv.Module}.{qv.Name}' is not callable.");
                return ReturnNeverWithDummyTemp();
            }

            if (collectedArgs.Count != builtinMember.Arity)
            {
                return ReportArityMismatch(rootExpr, builtinMember.Arity, collectedArgs.Count);
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
                _ => StdMemberNotFound(qv.Module, qv.Name)
            };
        }

        // For non-TCO calls, sub-expressions are NOT in tail position
        var savedTailPos = _tcoCtx?.InTailPosition ?? false;
        if (_tcoCtx is not null) _tcoCtx.InTailPosition = false;

        var (currentTemp, currentType) = LowerExpr(rootExpr);

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

    private (int, TypeRef) LowerNullaryConstructor(ConstructorSymbol ctor)
    {
        var resultType = InstantiateAdtType(ctor);
        int tag = GetConstructorTag(ctor);

        // Allocate ADT heap cell: (1 + 0) * 8 = 8 bytes (tag only, no fields): [ctorTag]
        int ptrTemp = NewTemp();
        Emit(new IrInst.AllocAdt(ptrTemp, tag, 0));
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

    private (int, TypeRef) LowerConstructorApplication(ConstructorSymbol ctor, List<Expr> args)
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
        Emit(new IrInst.AllocAdt(ptrTemp, tag, ctor.Arity));
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

        var (valueTemp, valueType) = LowerExpr(match.Value);
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
            var caseScope = new Dictionary<string, Binding>(_scopes.Peek(), StringComparer.Ordinal);
            _scopes.Push(caseScope);
            PushResourceScope();

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
                EmitPattern(match.Cases[i].Pattern, valueTemp, caseFailLabel, patternBindings);
            }

            // Track resource bindings created by pattern matching
            TrackResourceBindingsInPattern(patternBindings);

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
            PopResourceScope();
            Emit(new IrInst.Jump(endLabel));

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
        else if (!hasTuplePatternArm && !hasConstructorPatterns && !IsDefinitelyExhaustive(match.Cases))
        {
            _diag.Error(matchPos, "Non-exhaustive match expression.");
            reportedNonExhaustive = true;
        }

        if (!reportedNonExhaustive &&
            TryGetMissingPattern(prunedValueType, match.Cases.Select(c => c.Pattern).ToList(), out var missingPattern))
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
        // The IR has no dedicated equality instruction; emulate tag == expectedTag as
        // (tag >= expectedTag) AND (tag <= expectedTag) using the existing CmpIntGe / CmpIntLe.
        int tagTemp = NewTemp();
        int geTemp = NewTemp();
        int leTemp = NewTemp();
        int expectedTagTemp = NewTemp();
        Emit(new IrInst.GetAdtTag(tagTemp, ptrTemp));
        Emit(new IrInst.LoadConstInt(expectedTagTemp, expectedTag));
        Emit(new IrInst.CmpIntGe(geTemp, tagTemp, expectedTagTemp));
        Emit(new IrInst.JumpIfFalse(geTemp, failLabel));
        Emit(new IrInst.CmpIntLe(leTemp, tagTemp, expectedTagTemp));
        Emit(new IrInst.JumpIfFalse(leTemp, failLabel));
    }

    private void EmitRequireZero(int valueTemp, string failLabel)
    {
        // IR has <= and >= but no direct == comparison.
        int zeroTemp = NewTemp();
        int geTemp = NewTemp();
        int leTemp = NewTemp();
        Emit(new IrInst.LoadConstInt(zeroTemp, 0));
        Emit(new IrInst.CmpIntGe(geTemp, valueTemp, zeroTemp));
        Emit(new IrInst.JumpIfFalse(geTemp, failLabel));
        Emit(new IrInst.CmpIntLe(leTemp, valueTemp, zeroTemp));
        Emit(new IrInst.JumpIfFalse(leTemp, failLabel));
    }

    private void EmitRequireNonZero(int valueTemp, string failLabel)
    {
        // IR has <= but no > comparison; treat non-zero list pointers as > 0.
        int zeroTemp = NewTemp();
        int leTemp = NewTemp();
        var passLabel = NewLabel("match_nonzero");
        Emit(new IrInst.LoadConstInt(zeroTemp, 0));
        Emit(new IrInst.CmpIntLe(leTemp, valueTemp, zeroTemp));
        Emit(new IrInst.JumpIfFalse(leTemp, passLabel));
        Emit(new IrInst.Jump(failLabel));
        Emit(new IrInst.Label(passLabel));
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
    /// Returns the resource type name if the type is a resource type, otherwise null.
    /// </summary>
    private static string? GetResourceTypeName(TypeRef prunedType)
    {
        return prunedType is TypeRef.TNamedType named && BuiltinRegistry.IsResourceTypeName(named.Symbol.Name)
            ? named.Symbol.Name
            : null;
    }

    /// <summary>
    /// Registers a resource binding in the current resource scope.
    /// Called when a let binding or pattern binding creates a resource-typed variable.
    /// </summary>
    private void TrackResource(string name, int slot, string resourceTypeName, TextSpan? definitionSpan)
    {
        if (_resourceScopes.Count > 0)
        {
            _resourceScopes.Peek()[name] = new ResourceInfo(slot, resourceTypeName, definitionSpan);
        }
    }

    /// <summary>
    /// Looks up a resource binding across all resource scopes.
    /// </summary>
    private ResourceInfo? LookupResource(string name)
    {
        foreach (var scope in _resourceScopes)
        {
            if (scope.TryGetValue(name, out var info))
            {
                return info;
            }
        }

        return null;
    }

    /// <summary>
    /// Marks a resource as dropped (explicitly closed).
    /// Returns true if the operation succeeded (resource was alive and is now marked dropped)
    /// or if the name is not a tracked resource (no-op — safe to call on any binding).
    /// Returns false if the resource was already dropped (double-drop detected).
    /// </summary>
    private bool TryMarkResourceDropped(string name)
    {
        var info = LookupResource(name);
        if (info is null)
        {
            return true; // not a tracked resource — no-op
        }

        if (info.IsDropped)
        {
            return false; // already dropped — double-drop
        }

        info.IsDropped = true;
        return true;
    }

    /// <summary>
    /// Emits Drop instructions for all alive (not yet dropped) resources in the current resource scope.
    /// Called at scope exit.
    /// </summary>
    private void EmitDropsForCurrentScope()
    {
        if (_resourceScopes.Count == 0)
        {
            return;
        }

        var scope = _resourceScopes.Peek();
        foreach (var (_, info) in scope)
        {
            if (!info.IsDropped)
            {
                int loadTemp = NewTemp();
                Emit(new IrInst.LoadLocal(loadTemp, info.Slot));
                Emit(new IrInst.Drop(loadTemp, info.ResourceTypeName));
                info.IsDropped = true;
            }
        }
    }

    /// <summary>
    /// Pushes a new resource scope. Must be matched with PopResourceScope().
    /// </summary>
    private void PushResourceScope()
    {
        _resourceScopes.Push(new Dictionary<string, ResourceInfo>(StringComparer.Ordinal));
    }

    /// <summary>
    /// Pops a resource scope, emitting Drop instructions for any remaining alive resources.
    /// </summary>
    private void PopResourceScope()
    {
        EmitDropsForCurrentScope();
        _resourceScopes.Pop();
    }

    /// <summary>
    /// Returns true if the current resource scope contains any alive (not yet dropped) resources.
    /// </summary>
    private bool HasAliveResourcesInCurrentScope()
    {
        if (_resourceScopes.Count == 0)
        {
            return false;
        }

        var scope = _resourceScopes.Peek();
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
    /// Tracks resource bindings created by pattern matching.
    /// Scans pattern bindings for resource types and registers them for tracking.
    /// </summary>
    private void TrackResourceBindingsInPattern(IReadOnlyDictionary<string, TypeRef> patternBindings)
    {
        foreach (var (name, type) in patternBindings)
        {
            var prunedType = Prune(type);
            var resourceTypeName = GetResourceTypeName(prunedType);
            if (resourceTypeName is not null)
            {
                // Look up the slot from the current scope
                if (Lookup(name) is Binding.Local local)
                {
                    TrackResource(name, local.Slot, resourceTypeName, local.DefinitionSpan);
                }
            }
        }
    }

    /// <summary>
    /// Checks if a resource expression refers to a dropped resource and reports use-after-drop.
    /// </summary>
    private void CheckUseAfterDrop(Expr expr)
    {
        if (expr is Expr.Var v)
        {
            var resourceInfo = LookupResource(v.Name);
            if (resourceInfo is not null && resourceInfo.IsDropped)
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
                default:
                    throw new NotSupportedException(ex.GetType().Name);
            }
        }

        Visit(e, bound);
        return res;
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
            if (IsCatchAllPattern(matchCase.Pattern))
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

        if (cases.Any(c => IsCatchAllPattern(c.Pattern)))
        {
            return [];
        }

        var seenConstructors = new HashSet<string>(StringComparer.Ordinal);
        foreach (var matchCase in cases)
        {
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

        if (cases.Any(c => IsCatchAllPattern(c.Pattern)))
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
        var hasCatchAll = false;

        foreach (var matchCase in cases)
        {
            if (hasCatchAll)
            {
                ReportDiagnostic(GetSpan(matchCase.Pattern), "Unreachable match arm: a catch-all pattern was already matched earlier.");
                continue;
            }

            if (IsCatchAllPattern(matchCase.Pattern))
            {
                hasCatchAll = true;
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
            IntrinsicKind.Print,
            new TypeScheme([], new TypeRef.TFun(new TypeRef.TStr(), _resolvedTypes["Unit"]))
        );
    }

    private Binding.Intrinsic CreateWriteLineBinding()
    {
        return new Binding.Intrinsic(
            IntrinsicKind.Print,
            new TypeScheme([], new TypeRef.TFun(new TypeRef.TStr(), _resolvedTypes["Unit"]))
        );
    }

    private Binding.Intrinsic CreateReadLineBinding()
    {
        return new Binding.Intrinsic(
            IntrinsicKind.Print,
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
            IntrinsicKind.Print,
            new TypeScheme([], new TypeRef.TFun(new TypeRef.TStr(), CreateStringResultType(new TypeRef.TStr())))
        );
    }

    private Binding.Intrinsic CreateFileWriteTextBinding()
    {
        return new Binding.Intrinsic(
            IntrinsicKind.Print,
            new TypeScheme([], new TypeRef.TFun(new TypeRef.TStr(), new TypeRef.TFun(new TypeRef.TStr(), CreateStringResultType(_resolvedTypes["Unit"]))))
        );
    }

    private Binding.Intrinsic CreateFileExistsBinding()
    {
        return new Binding.Intrinsic(
            IntrinsicKind.Print,
            new TypeScheme([], new TypeRef.TFun(new TypeRef.TStr(), CreateStringResultType(new TypeRef.TBool())))
        );
    }

    private Binding.Intrinsic CreateHttpGetBinding()
    {
        return new Binding.Intrinsic(
            IntrinsicKind.Print,
            new TypeScheme([], new TypeRef.TFun(new TypeRef.TStr(), CreateStringResultType(new TypeRef.TStr())))
        );
    }

    private Binding.Intrinsic CreateHttpPostBinding()
    {
        return new Binding.Intrinsic(
            IntrinsicKind.Print,
            new TypeScheme([], new TypeRef.TFun(new TypeRef.TStr(), new TypeRef.TFun(new TypeRef.TStr(), CreateStringResultType(new TypeRef.TStr()))))
        );
    }

    private Binding.Intrinsic CreateNetTcpConnectBinding()
    {
        return new Binding.Intrinsic(
            IntrinsicKind.Print,
            new TypeScheme([], new TypeRef.TFun(new TypeRef.TStr(), new TypeRef.TFun(new TypeRef.TInt(), CreateStringResultType(_resolvedTypes["Socket"]))))
        );
    }

    private Binding.Intrinsic CreateNetTcpSendBinding()
    {
        return new Binding.Intrinsic(
            IntrinsicKind.Print,
            new TypeScheme([], new TypeRef.TFun(_resolvedTypes["Socket"], new TypeRef.TFun(new TypeRef.TStr(), CreateStringResultType(new TypeRef.TInt()))))
        );
    }

    private Binding.Intrinsic CreateNetTcpReceiveBinding()
    {
        return new Binding.Intrinsic(
            IntrinsicKind.Print,
            new TypeScheme([], new TypeRef.TFun(_resolvedTypes["Socket"], new TypeRef.TFun(new TypeRef.TInt(), CreateStringResultType(new TypeRef.TStr()))))
        );
    }

    private Binding.Intrinsic CreateNetTcpCloseBinding()
    {
        return new Binding.Intrinsic(
            IntrinsicKind.Print,
            new TypeScheme([], new TypeRef.TFun(_resolvedTypes["Socket"], CreateStringResultType(_resolvedTypes["Unit"])))
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
}
