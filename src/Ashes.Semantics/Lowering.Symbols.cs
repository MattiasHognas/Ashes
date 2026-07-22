using System.Diagnostics;
using Ashes.Frontend;

namespace Ashes.Semantics;

public sealed partial class Lowering
{
    /// <summary>
    /// Resolves a user-written <see cref="TypeExpr"/> to its internal <see cref="TypeRef"/>.
    /// Unknown types produce a diagnostic and return <see cref="TypeRef.TNever"/>.
    /// </summary>
    private TypeRef ResolveTypeExpr(TypeExpr typeExpr)
    {
        return typeExpr switch
        {
            TypeExpr.UnitType => _resolvedTypes["Unit"],
            TypeExpr.Named { Name: "Int" } => new TypeRef.TInt(),
            TypeExpr.Named { Name: "Bool" } => new TypeRef.TBool(),
            TypeExpr.Named { Name: "Str" } => new TypeRef.TStr(),
            TypeExpr.Named { Name: "Float" } => new TypeRef.TFloat(),
            TypeExpr.Named { Name: "BigInt" } => new TypeRef.TBigInt(),
            TypeExpr.Named n when _typeExprParamScope?.TryGetValue(n.Name, out var scoped) == true => scoped,
            TypeExpr.Named n => ResolveTypeName(n.Name),
            TypeExpr.Applied a => ResolveTypeName(a.Name, a.Args.Select(ResolveTypeExpr).ToList()),
            TypeExpr.Arrow arr => new TypeRef.TFun(ResolveTypeExpr(arr.From), ResolveTypeExpr(arr.To))
            {
                Row = arr.Needs is null ? null : ResolveNeedsRow(arr.Needs)
            },
            TypeExpr.TupleType t when t.Elements.Count == 0 => _resolvedTypes["Unit"],
            TypeExpr.TupleType t => new TypeRef.TTuple(t.Elements.Select(ResolveTypeExpr).ToList()),
            _ => throw new NotSupportedException($"Unknown TypeExpr: {typeExpr.GetType().Name}")
        };
    }

    /// <summary>
    /// Lowers a record literal expression:
    /// <c>TypeName { field1 = e1, field2 = e2 }</c>.
    /// Field values are reordered to match the declared field order.
    /// </summary>
    private (int, TypeRef) LowerRecordLit(Expr.RecordLit recordLit)
    {
        if (!_constructorSymbols.TryGetValue(recordLit.TypeName, out var ctor))
        {
            if (!_typeSymbols.TryGetValue(recordLit.TypeName, out var typeSym))
            {
                ReportDiagnostic(GetSpan(recordLit), $"Unknown record type '{recordLit.TypeName}'.");
                return ReturnNeverWithDummyTemp();
            }

            // Type exists but no matching constructor — not a record type
            ReportDiagnostic(GetSpan(recordLit), $"Type '{recordLit.TypeName}' is not a record type.");
            return ReturnNeverWithDummyTemp();
        }

        var fieldNames = ctor.DeclaringSyntax.FieldNames;
        if (fieldNames.Count == 0)
        {
            ReportDiagnostic(GetSpan(recordLit), $"Type '{recordLit.TypeName}' is not a record type.");
            return ReturnNeverWithDummyTemp();
        }

        if (recordLit.Fields.Count == 0 && ctor.Arity > 0)
        {
            ReportDiagnostic(GetSpan(recordLit), $"Record literal for '{recordLit.TypeName}' must provide all {ctor.Arity} field(s).");
            return ReturnNeverWithDummyTemp();
        }

        // Validate that all provided fields exist, and that all required fields are present
        var providedByName = new Dictionary<string, Expr>(StringComparer.Ordinal);
        foreach (var (name, value) in recordLit.Fields)
        {
            if (!fieldNames.Contains(name, StringComparer.Ordinal))
            {
                ReportDiagnostic(GetSpan(recordLit), $"Record type '{recordLit.TypeName}' has no field '{name}'.");
            }
            else if (providedByName.ContainsKey(name))
            {
                ReportDiagnostic(GetSpan(recordLit), $"Field '{name}' is provided more than once in record literal for '{recordLit.TypeName}'.");
            }
            else
            {
                providedByName[name] = value;
            }
        }

        foreach (var fn in fieldNames)
        {
            if (!providedByName.ContainsKey(fn))
            {
                ReportDiagnostic(GetSpan(recordLit), $"Missing field '{fn}' in record literal for '{recordLit.TypeName}'.");
                return ReturnNeverWithDummyTemp();
            }
        }

        // Build positional args in declared field order
        var orderedArgs = fieldNames.Select(fn => providedByName[fn]).ToList();
        return LowerConstructorApplication(ctor, orderedArgs);
    }

    /// <summary>
    /// Lowers a record update expression:
    /// <c>{ target with field1 = e1, field2 = e2 }</c>.
    /// Produces a fresh ADT with unchanged fields copied and specified fields replaced.
    /// </summary>
    private (int, TypeRef) LowerRecordUpdate(Expr.RecordUpdate recordUpdate)
    {
        var (targetTemp, targetType) = LowerExpr(recordUpdate.Target);
        var prunedTarget = Prune(targetType);

        if (prunedTarget is not TypeRef.TNamedType namedType)
        {
            ReportDiagnostic(GetSpan(recordUpdate), $"Record update requires a record type, got {Pretty(prunedTarget)}.");
            return ReturnNeverWithDummyTemp();
        }

        var typeSymbol = namedType.Symbol;
        if (typeSymbol.Constructors.Count != 1 || typeSymbol.Constructors[0].DeclaringSyntax.FieldNames.Count == 0)
        {
            ReportDiagnostic(GetSpan(recordUpdate), $"Type '{typeSymbol.Name}' is not a record type and cannot be updated with '{{ with }}'.");
            return ReturnNeverWithDummyTemp();
        }

        var ctor = typeSymbol.Constructors[0];
        var fieldNames = ctor.DeclaringSyntax.FieldNames;

        // Validate update fields
        var updateByName = new Dictionary<string, Expr>(StringComparer.Ordinal);
        foreach (var (name, value) in recordUpdate.Updates)
        {
            if (!fieldNames.Contains(name, StringComparer.Ordinal))
            {
                ReportDiagnostic(GetSpan(recordUpdate), $"Record type '{typeSymbol.Name}' has no field '{name}'.");
                return ReturnNeverWithDummyTemp();
            }

            if (updateByName.ContainsKey(name))
            {
                ReportDiagnostic(GetSpan(recordUpdate), $"Field '{name}' is updated more than once in record update for '{typeSymbol.Name}'.");
            }
            else
            {
                updateByName[name] = value;
            }
        }

        var resultType = namedType;
        int tag = GetConstructorTag(ctor);

        // Load all field values, then store update values, allocate new cell
        var fieldTemps = BuildRecordUpdateFieldTemps(ctor, fieldNames, updateByName, targetTemp, resultType);

        int ptrTemp = NewTemp();
        Emit(new IrInst.AllocAdt(ptrTemp, tag, ctor.Arity));
        for (int i = 0; i < fieldTemps.Length; i++)
        {
            Emit(new IrInst.SetAdtField(ptrTemp, i, fieldTemps[i]));
        }

        return (ptrTemp, resultType);
    }

    /// <summary>
    /// Builds the per-field temps for a record update: updated fields are lowered and unified with
    /// their declared parameter types; unchanged fields are loaded from the update target.
    /// </summary>
    private int[] BuildRecordUpdateFieldTemps(
        ConstructorSymbol ctor,
        IReadOnlyList<string> fieldNames,
        Dictionary<string, Expr> updateByName,
        int targetTemp,
        TypeRef.TNamedType resultType)
    {
        var fieldTemps = new int[fieldNames.Count];
        for (int i = 0; i < fieldNames.Count; i++)
        {
            if (updateByName.TryGetValue(fieldNames[i], out var updateExpr))
            {
                var (updateTemp, updateType) = LowerExpr(updateExpr);
                var paramType = InstantiateConstructorParameterType(ctor, i, resultType);
                Unify(paramType, updateType);
                fieldTemps[i] = updateTemp;
            }
            else
            {
                int loadedTemp = NewTemp();
                Emit(new IrInst.GetAdtField(loadedTemp, targetTemp, i));
                fieldTemps[i] = loadedTemp;
            }
        }

        return fieldTemps;
    }

    private void RegisterTypeDeclarations(IReadOnlyList<TypeDecl> typeDecls)
    {
        // Every name that denotes a concrete type — builtins registered already, all user types in
        // this program (so forward references resolve), and the primitives. A constructor field that
        // names something outside this set is an implicit type parameter.
        var knownTypeNames = new HashSet<string>(_typeSymbols.Keys, StringComparer.Ordinal);
        knownTypeNames.UnionWith(typeDecls.Select(d => d.Name));
        knownTypeNames.UnionWith(PrimitivePayloadTypeNames);
        knownTypeNames.Add("Unit");

        foreach (var decl in typeDecls)
        {
            RegisterTypeDeclaration(decl, knownTypeNames);
        }
    }

    private void RegisterTypeDeclaration(TypeDecl decl, HashSet<string> knownTypeNames)
    {
        if (BuiltinRegistry.IsReservedTypeName(decl.Name))
        {
            ReportDiagnostic(GetSpan(decl), "'Ashes' and built-in runtime types are reserved");
            return;
        }

        if (_typeSymbols.ContainsKey(decl.Name))
        {
            ReportDiagnostic(GetSpan(decl), $"Duplicate type name '{decl.Name}'.");
            return;
        }

        var declaredOrInferredTypeParameters = decl.TypeParameters.Count > 0
            ? decl.TypeParameters
            : InferImplicitTypeParameters(decl.Name, decl.Constructors, knownTypeNames);

        if (HasDuplicateTypeParameters(decl, declaredOrInferredTypeParameters))
        {
            return; // Do not register an inconsistent type symbol when type parameters are duplicated
        }

        if (decl.Constructors.Count == 0)
        {
            ReportDiagnostic(GetSpan(decl), $"Type '{decl.Name}' must have at least one constructor.");
            return; // Cannot register a usable type symbol without constructors
        }

        var typeParameterSymbols = declaredOrInferredTypeParameters
            .Select(tp => new TypeParameterSymbol(tp.Name))
            .ToList();
        var ctorSymbols = new List<ConstructorSymbol>();
        var typeSymbol = new TypeSymbol(
            Name: decl.Name,
            TypeParameters: typeParameterSymbols,
            Constructors: ctorSymbols,
            DeclaringSyntax: decl with { TypeParameters = declaredOrInferredTypeParameters }
        );
        // Register the type symbol (and its resolved TNamedType) before resolving field types, so
        // a self-recursive field (`type Tree = | Node(Tree, Tree)`) resolves its own name. The
        // constructor list is filled in place below.
        _typeSymbols[decl.Name] = typeSymbol;
        _resolvedTypes[decl.Name] = new TypeRef.TNamedType(
            typeSymbol,
            typeParameterSymbols.Select(tp => (TypeRef)new TypeRef.TTypeParam(tp)).ToList());

        RegisterConstructorSymbols(decl, typeSymbol, ctorSymbols);
    }

    private bool HasDuplicateTypeParameters(TypeDecl decl, IReadOnlyList<TypeParameter> typeParameters)
    {
        var seenTypeParams = new HashSet<string>(StringComparer.Ordinal);
        var hasDuplicateTypeParams = false;
        foreach (var tp in typeParameters)
        {
            if (!seenTypeParams.Add(tp.Name))
            {
                ReportDiagnostic(GetSpan(decl), $"Duplicate type parameter '{tp.Name}' in type '{decl.Name}'.");
                hasDuplicateTypeParams = true;
            }
        }

        return hasDuplicateTypeParams;
    }

    private void RegisterConstructorSymbols(TypeDecl decl, TypeSymbol typeSymbol, List<ConstructorSymbol> ctorSymbols)
    {
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
                    .Select(fieldType => ResolveConstructorFieldType(fieldType, typeSymbol))
                    .ToList(),
                DeclaringSyntax: ctor
            );
            ctorSymbols.Add(ctorSymbol);
            // Constructor names are globally visible (ML/F#-style): a later type's
            // constructor with the same name shadows an earlier one intentionally.
            _constructorSymbols[ctor.Name] = ctorSymbol;
        }
    }

    private void RegisterExternalDeclarations(IReadOnlyList<ExternalDecl> externalDecls)
    {
        foreach (var opaqueType in externalDecls.OfType<ExternalDecl.OpaqueType>())
        {
            if (!_externalOpaqueTypes.Add(opaqueType.Name))
            {
                ReportDiagnostic(GetSpan(opaqueType), $"Duplicate external type '{opaqueType.Name}'.");
            }
        }

        foreach (var function in externalDecls.OfType<ExternalDecl.Function>())
        {
            var parameterTypes = function.ParameterTypes.Select(t => ResolveExternalParsedType(function, t, allowVoid: false)).ToList();
            var returnType = ResolveExternalParsedType(function, function.ReturnType, allowVoid: true);
            if (parameterTypes.Any(t => t is null) || returnType is null)
            {
                continue;
            }

            var resolvedParameterTypes = parameterTypes.Select(t => t!).ToList();

            var symbolName = function.SymbolName ?? function.Name;
            string? libraryName = null;
            var atIndex = symbolName.LastIndexOf('@');
            if (atIndex >= 0)
            {
                libraryName = symbolName[(atIndex + 1)..];
                symbolName = symbolName[..atIndex];
            }

            var irFunction = new IrExternalFunction(
                function.Name,
                symbolName,
                resolvedParameterTypes.Select(t => t.FfiType).ToList(),
                returnType.FfiType,
                string.IsNullOrWhiteSpace(libraryName) ? null : libraryName);
            _externalFunctions.Add(irFunction);

            var type = BuildFunctionType(resolvedParameterTypes.Select(t => t.SourceType).ToList(), returnType.SourceType);
            _scopes.Peek()[function.Name] = new Binding.ExternalFunction(irFunction, type);
        }
    }

    private ResolvedExternalType? ResolveExternalParsedType(ExternalDecl externalDecl, ParsedType parsedType, bool allowVoid)
    {
        if (parsedType is ParsedType.Pointer pointer)
        {
            var pointee = ResolveExternalParsedType(externalDecl, pointer.Pointee, allowVoid: false);
            return pointee is null
                ? null
                : new ResolvedExternalType(new TypeRef.TPtr(pointee.SourceType), new FfiType.Ptr(pointee.FfiType));
        }

        if (parsedType is not ParsedType.Named named)
        {
            ReportDiagnostic(GetSpan(externalDecl), "Unsupported external type syntax.");
            return null;
        }

        return named.Name switch
        {
            "Int" => new ResolvedExternalType(new TypeRef.TInt(), new FfiType.Int()),
            "u8" => new ResolvedExternalType(new TypeRef.TUInt(8), new FfiType.UInt(8)),
            "u16" => new ResolvedExternalType(new TypeRef.TUInt(16), new FfiType.UInt(16)),
            "u32" => new ResolvedExternalType(new TypeRef.TUInt(32), new FfiType.UInt(32)),
            "u64" => new ResolvedExternalType(new TypeRef.TUInt(64), new FfiType.UInt(64)),
            "Float" => new ResolvedExternalType(new TypeRef.TFloat(), new FfiType.Float()),
            "f32" => new ResolvedExternalType(new TypeRef.TFloat(), new FfiType.Float32()),
            "Bool" => new ResolvedExternalType(new TypeRef.TBool(), new FfiType.Bool()),
            "Str" => new ResolvedExternalType(new TypeRef.TStr(), new FfiType.Str()),
            "void" when allowVoid => new ResolvedExternalType(_resolvedTypes["Unit"], new FfiType.Void()),
            "void" => ReportVoidParameterExternalType(externalDecl),
            _ when _externalOpaqueTypes.Contains(named.Name) => new ResolvedExternalType(new TypeRef.TOpaque(named.Name), new FfiType.Opaque(named.Name)),
            _ => ReportUnsupportedExternalType(externalDecl, named.Name)
        };
    }

    private ResolvedExternalType? ReportUnsupportedExternalType(ExternalDecl externalDecl, string name)
    {
        ReportDiagnostic(GetSpan(externalDecl), $"Type '{name}' is not supported in external declarations.");
        return null;
    }

    private ResolvedExternalType? ReportVoidParameterExternalType(ExternalDecl externalDecl)
    {
        ReportDiagnostic(GetSpan(externalDecl), "Type 'void' is only supported as an external return type.");
        return null;
    }

    private static TypeRef BuildFunctionType(IReadOnlyList<TypeRef> parameterTypes, TypeRef returnType)
    {
        var result = returnType;
        for (int i = parameterTypes.Count - 1; i >= 0; i--)
        {
            result = new TypeRef.TFun(parameterTypes[i], result);
        }

        return result;
    }

    private sealed record ResolvedExternalType(TypeRef SourceType, FfiType FfiType);

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

    /// <summary>
    /// Resolves one constructor field's type expression to a <see cref="TypeRef"/>. The declaring
    /// type's own parameters are in scope (a <c>Named</c> matching one resolves to that parameter),
    /// its own name resolves to the recursive <see cref="TypeRef.TNamedType"/>, and everything else
    /// resolves like an ordinary type annotation — primitives, other user/builtin types (including
    /// parameterized ones), function types, and tuples. Field names that denote no known type were
    /// already promoted to implicit type parameters (see <see cref="InferImplicitTypeParameters"/>),
    /// so they resolve through the parameter scope.
    /// </summary>
    private TypeRef ResolveConstructorFieldType(TypeExpr fieldType, TypeSymbol declaringTypeSymbol)
    {
        // A bare reference to the declaring type (`type MapTree(K, V) = | Node(Int, MapTree, ...)`)
        // means the type applied to its own parameters, `MapTree(K, V)` — the idiomatic way to write
        // a self-recursive field. Rewrite such bare names to the explicit application (at any nesting
        // depth) before resolving; every other name resolves as an ordinary annotation.
        return ResolveAnnotationType(ExpandSelfReferences(fieldType, declaringTypeSymbol), declaringTypeSymbol.TypeParameters);
    }

    private static TypeExpr ExpandSelfReferences(TypeExpr typeExpr, TypeSymbol declaringTypeSymbol)
    {
        var ownParams = declaringTypeSymbol.TypeParameters;
        if (ownParams.Count == 0)
        {
            return typeExpr; // a non-parameterized self name already resolves correctly
        }

        TypeExpr SelfApplication() =>
            new TypeExpr.Applied(declaringTypeSymbol.Name, ownParams.Select(tp => (TypeExpr)new TypeExpr.Named(tp.Name)).ToList());

        TypeExpr Rewrite(TypeExpr t) => t switch
        {
            TypeExpr.Named n when string.Equals(n.Name, declaringTypeSymbol.Name, StringComparison.Ordinal) => SelfApplication(),
            TypeExpr.Applied a => new TypeExpr.Applied(a.Name, a.Args.Select(Rewrite).ToList()),
            TypeExpr.Arrow arr => new TypeExpr.Arrow(Rewrite(arr.From), Rewrite(arr.To)) { Needs = arr.Needs },
            TypeExpr.TupleType tup => new TypeExpr.TupleType(tup.Elements.Select(Rewrite).ToList()),
            _ => t
        };

        return Rewrite(typeExpr);
    }

    // Concrete primitive type names that may appear as constructor payloads. A payload naming one of
    // these is a concrete field type, never an implicit type parameter. (The full resolution list also
    // treats the declaring type's own name as concrete — handled per-declaration below.)
    private static readonly HashSet<string> PrimitivePayloadTypeNames =
        new(StringComparer.Ordinal) { "Int", "Bool", "Str", "Bytes", "Float", "BigInt" };

    private static IReadOnlyList<TypeParameter> InferImplicitTypeParameters(
        string declaringTypeName,
        IReadOnlyList<TypeConstructor> constructors,
        IReadOnlySet<string> knownTypeNames)
    {
        var typeParameters = new List<TypeParameter>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var name in constructors.SelectMany(ctor => ctor.Parameters).SelectMany(fieldType => fieldType.MentionedNames()))
        {
            // A name mentioned in a field type is an implicit type parameter only when it denotes no
            // known type. A name of the declaring type itself (a self-recursive field), a primitive,
            // or any other user/builtin type is a *concrete* reference, not a parameter — inferring a
            // parameter for it would over-generalize the constructor (a self-recursive field becomes
            // polymorphic, failing the occurs check when the type is actually built recursively; a
            // concrete field's type is lost). Uppercase or lowercase is irrelevant: `A`, `T`, `V` are
            // conventional parameter names and resolve here precisely because no type is named `A`.
            if (string.Equals(name, declaringTypeName, StringComparison.Ordinal)
                || knownTypeNames.Contains(name))
            {
                continue;
            }

            if (seen.Add(name))
            {
                typeParameters.Add(new TypeParameter(name));
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

    private (int, TypeRef) LowerNullaryConstructor(ConstructorSymbol ctor, bool stackAllocate = false)
    {
        var resultType = InstantiateAdtType(ctor);
        int tag = GetConstructorTag(ctor);
        bool runtimeManagedCandidate = (_runtimeRcCopyAdtAllocationRequested
                || RuntimeReuseAllocationMatches(resultType))
            && (CanRuntimeManageCopyAdt(resultType) || CanRuntimeManageRecursiveCopyAdt(resultType));

        // Allocate ADT heap cell: (1 + 0) * 8 = 8 bytes (tag only, no fields): [ctorTag]
        int ptrTemp = NewTemp();
        if (!stackAllocate && TryConsumeReuseToken(
                0,
                runtimeManagedCandidate,
                out int reuseTokenTemp,
                out RuntimeReuseCleanup? runtimeCleanup))
        {
            // In-place reuse of a dead nullary cell (e.g. Leaf -> Leaf), keeping the rebuilt result
            // below the watermark so the enclosing loop can reset the arena.
            EmitRuntimeReuseTokenChildrenDrop(reuseTokenTemp, runtimeCleanup);
            Emit(new IrInst.AllocReusing(
                ptrTemp,
                tag,
                0,
                reuseTokenTemp,
                runtimeCleanup is not null));
            _reuseResultTemps.Add(ptrTemp);
        }
        else if (stackAllocate)
        {
            Emit(new IrInst.AllocAdtStack(ptrTemp, tag, 0));
        }
        else if (_inSpecialization)
        {
            // Fresh nullary cell (e.g. an Empty leaf of a node Map.set creates for a new key) inside an
            // in-place reuse specialization: allocate in the persistent to-space so it survives the
            // loop's per-iteration arena reset. See LowerConstructorApplication / IrInst.AllocAdtToSpace.
            Emit(new IrInst.AllocAdtToSpace(ptrTemp, tag, 0));
            _reuseResultTemps.Add(ptrTemp);
        }
        else
        {
            Emit(new IrInst.AllocAdt(ptrTemp, tag, 0, runtimeManagedCandidate));
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
        bool runtimeManagedCandidate = resultType is TypeRef.TNamedType named
            && ((_runtimeRcRecordAllocationRequested && CanRuntimeManageConstructorApplication(ctor, args, named))
                || ((_runtimeRcCopyAdtAllocationRequested || RuntimeReuseAllocationMatches(named))
                    && (CanRuntimeManageCopyAdt(named)
                        || CanRuntimeManageRecursiveAdtConstructorApplication(ctor, args, named))));

        (List<int> argTemps, List<TypeRef> argTypes) = LowerConstructorArguments(
            ctor, args, resultType, runtimeManagedCandidate);
        if (runtimeManagedCandidate)
        {
            PrepareRuntimeManagedAdtChildArguments(args, argTemps);
        }

        int tag = GetConstructorTag(ctor);

        // Allocate a tagged heap cell: [ctorTag, field0, field1, ..., fieldN]
        int ptrTemp = AllocateConstructorCell(
            ctor,
            tag,
            stackAllocate,
            runtimeManagedCandidate,
            out bool reuseNode,
            out int consumedTokenTemp);
        for (int i = 0; i < argTemps.Count; i++)
        {
            int fieldTemp = MaterializeSpecializationField(args[i], argTypes[i], argTemps[i], ptrTemp, i, reuseNode, consumedTokenTemp);
            Emit(new IrInst.SetAdtField(ptrTemp, i, fieldTemp));
        }

        return (ptrTemp, resultType);
    }

    private bool RuntimeReuseAllocationMatches(TypeRef.TNamedType resultType)
        => _runtimeRcReuseAllocationTypeRequested is { } requested
            && ReferenceEquals(requested.Symbol, resultType.Symbol);

    private void PrepareRuntimeManagedAdtChildArguments(IReadOnlyList<Expr> arguments, List<int> argumentTemps)
    {
        if (_runtimeRcAdtChildBindings is null)
        {
            return;
        }

        for (int i = 0; i < arguments.Count; i++)
        {
            if (arguments[i] is not Expr.Var variable
                || !_runtimeRcAdtChildBindings.TryGetValue(variable.Name, out bool shared)
                || LookupOwnedValue(variable.Name) is not { RuntimeManaged: true, IsDropped: false } info)
            {
                continue;
            }

            if (shared)
            {
                int duplicatedTemp = NewTemp();
                Emit(new IrInst.RcDup(duplicatedTemp, argumentTemps[i], RuntimeManaged: true));
                argumentTemps[i] = duplicatedTemp;
                info.RuntimeDeepUnique = false;
            }
            else
            {
                info.ReleaseKind = ResourceReleaseKind.Moved;
            }
        }
    }

    private (List<int> Temps, List<TypeRef> Types) LowerConstructorArguments(
        ConstructorSymbol constructor,
        IReadOnlyList<Expr> arguments,
        TypeRef.TNamedType resultType,
        bool runtimeManagedCandidate)
    {
        var argumentTemps = new List<int>(arguments.Count);
        var argumentTypes = new List<TypeRef>(arguments.Count);
        bool savedRuntimeRequest = _runtimeRcRecordAllocationRequested;
        bool savedCopyAdtRequest = _runtimeRcCopyAdtAllocationRequested;
        _runtimeRcRecordAllocationRequested = runtimeManagedCandidate;
        _runtimeRcCopyAdtAllocationRequested = savedCopyAdtRequest && runtimeManagedCandidate;
        try
        {
            for (int i = 0; i < arguments.Count; i++)
            {
                (int argumentTemp, TypeRef argumentType) = LowerExpr(arguments[i]);
                argumentTemps.Add(argumentTemp);
                TypeRef parameterType = InstantiateConstructorParameterType(constructor, i, resultType);
                Unify(parameterType, argumentType);
                argumentTypes.Add(Prune(argumentType));
                MarkResourceArgMoved(arguments[i]);
            }
        }
        finally
        {
            _runtimeRcRecordAllocationRequested = savedRuntimeRequest;
            _runtimeRcCopyAdtAllocationRequested = savedCopyAdtRequest;
        }

        return (argumentTemps, argumentTypes);
    }

    /// <summary>
    /// Allocates the tagged cell for a constructor application, choosing between in-place reuse,
    /// stack allocation, to-space allocation (inside a reuse specialization), and a plain arena
    /// allocation. Returns the cell temp; <paramref name="reuseNode"/> and
    /// <paramref name="consumedTokenTemp"/> report whether (and which) reuse token was consumed.
    /// </summary>
    private int AllocateConstructorCell(
        ConstructorSymbol ctor,
        int tag,
        bool stackAllocate,
        bool runtimeManagedCandidate,
        out bool reuseNode,
        out int consumedTokenTemp)
    {
        int ptrTemp = NewTemp();
        reuseNode = false;
        consumedTokenTemp = -1;
        if (!stackAllocate && TryConsumeReuseToken(
                ctor.Arity,
                runtimeManagedCandidate,
                out int reuseTokenTemp,
                out RuntimeReuseCleanup? runtimeCleanup))
        {
            consumedTokenTemp = reuseTokenTemp;
            // In-place reuse: overwrite a same-size dead cell (the node a linear value was just
            // deconstructed from) instead of bump-allocating. The args were already read into temps
            // by the caller, so overwriting the cell now is safe.
            EmitRuntimeReuseTokenChildrenDrop(reuseTokenTemp, runtimeCleanup);
            Emit(new IrInst.AllocReusing(
                ptrTemp,
                tag,
                ctor.Arity,
                reuseTokenTemp,
                runtimeCleanup is not null));
            _reuseResultTemps.Add(ptrTemp);
            reuseNode = true;
        }
        else if (stackAllocate)
        {
            Emit(new IrInst.AllocAdtStack(ptrTemp, tag, ctor.Arity));
        }
        else if (_inSpecialization)
        {
            // Genuinely-new cell with no reuse token inside an in-place reuse specialization — e.g. the
            // node Map.set creates for a NEW key. Allocate it in the persistent to-space so it survives
            // the loop's per-iteration arena reset (the reset only reclaims the main arena). The cell is
            // uniquely owned, so it is also a linear reuse result (a rebuild by balance/rotate reuses it
            // in place, staying in to-space). See IrInst.AllocAdtToSpace / IsFullyReusing.
            Emit(new IrInst.AllocAdtToSpace(ptrTemp, tag, ctor.Arity));
            _reuseResultTemps.Add(ptrTemp);
        }
        else
        {
            Emit(new IrInst.AllocAdt(ptrTemp, tag, ctor.Arity, runtimeManagedCandidate));
        }

        return ptrTemp;
    }

    /// <summary>
    /// A FRESH heap leaf field of a reuse-built node (a Map key/value produced from the spec's
    /// newKey/newValue input, on insert OR update) must be copied into the persistent blob, or it
    /// dangles past the per-iteration reset (the node survives, but the field would point into
    /// reclaimed scratch). Fields taken from the matched accumulator (pattern bindings) are already
    /// persistent and are NOT copied — identified by the field argument being a variable that is
    /// not one of the spec's fresh-input names (see <c>_specFreshInputNames</c>, propagated through
    /// inlined helpers). Any non-variable field expression (a value computed in the arm, e.g. an
    /// upsert's onHit(value) call) is fresh arena scratch and must be materialized as well —
    /// over-materializing an already-persistent value only costs a copy, never correctness.
    /// Returns the (possibly persisted) field temp to store into the cell.
    /// </summary>
    private int MaterializeSpecializationField(Expr argExpr, TypeRef argType, int fieldTemp, int ptrTemp, int fieldIndex, bool reuseNode, int consumedTokenTemp)
    {
        if (_inSpecialization && _specFreshInputNames is not null
            && (argExpr is not Expr.Var fieldVar || _specFreshInputNames.Contains(fieldVar.Name)))
        {
            var pruned = Prune(argType);
            if (pruned is TypeRef.TStr or TypeRef.TBytes)
            {
                if (reuseNode && ReuseTokenFieldIsDead(consumedTokenTemp, fieldIndex))
                {
                    // Update path: reuse the dead old value blob in place when the new string fits and
                    // the old blob is provably persistent (a runtime blob-region check in the backend),
                    // else materialize fresh. Bounds blob growth to the largest value per cell instead of
                    // leaking one blob per update. The variable-size analogue of the tuple CopyFixedInto
                    // path below.
                    int oldValueTemp = NewTemp();
                    Emit(new IrInst.GetAdtField(oldValueTemp, ptrTemp, fieldIndex));
                    int persistentField = NewTemp();
                    Emit(new IrInst.CopyStringIntoOrFresh(persistentField, oldValueTemp, fieldTemp));
                    fieldTemp = persistentField;
                }
                else
                {
                    int persistentField = NewTemp();
                    Emit(new IrInst.CopyOutArenaToSpace(persistentField, fieldTemp, -1));
                    fieldTemp = persistentField;
                }
            }
            else if (pruned is TypeRef.TTuple tup && tup.Elements.All(e => BuiltinRegistry.IsCopyType(Prune(e))))
            {
                int sizeBytes = tup.Elements.Count * 8;
                if (reuseNode && ReuseTokenFieldIsDead(consumedTokenTemp, fieldIndex))
                {
                    // Update path: the reused node's old value cell is dead. Overwrite its contents in
                    // place when it is provably persistent (a runtime blob-region check in the backend),
                    // else materialize fresh — so value storage is reused and the blob stays bounded by
                    // distinct keys, without overwriting reclaimable main-arena memory in place.
                    int oldValueTemp = NewTemp();
                    Emit(new IrInst.GetAdtField(oldValueTemp, ptrTemp, fieldIndex));
                    int persistentField = NewTemp();
                    Emit(new IrInst.CopyFixedIntoOrFresh(persistentField, oldValueTemp, fieldTemp, sizeBytes));
                    fieldTemp = persistentField;
                }
                else
                {
                    // Insert path: no old cell to reuse — materialize a fresh blob cell (bounded by
                    // the number of distinct keys).
                    int persistentField = NewTemp();
                    Emit(new IrInst.CopyOutArenaToSpace(persistentField, fieldTemp, sizeBytes));
                    fieldTemp = persistentField;
                }
            }
        }

        return fieldTemp;
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

    /// <summary>CO-23: true when the reuse token's field can no longer be referenced on the current
    /// path — unbound (wildcard) or every arm reference already lowered/credited.</summary>
    private bool ReuseTokenFieldIsDead(int tokenTemp, int fieldIndex)
    {
        if (tokenTemp < 0
            || !_reuseTokenFieldBindings.TryGetValue(tokenTemp, out var fields)
            || !fields.TryGetValue(fieldIndex, out var info))
        {
            return true;
        }

        bool dead = _reuseBindingSeenBySlot.GetValueOrDefault(info.Slot) >= info.TotalRefs;
        if (Environment.GetEnvironmentVariable("ASH_DBG_REUSE") is not null)
        {
            Console.Error.WriteLine($"[co23] gate field={fieldIndex} slot={info.Slot} seen={_reuseBindingSeenBySlot.GetValueOrDefault(info.Slot)} total={info.TotalRefs} dead={dead}");
        }

        return dead;
    }
}
