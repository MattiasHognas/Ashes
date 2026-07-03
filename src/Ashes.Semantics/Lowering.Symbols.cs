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
            TypeExpr.Named n when _typeExprParamScope?.TryGetValue(n.Name, out var scoped) == true => scoped,
            TypeExpr.Named n => ResolveTypeName(n.Name),
            TypeExpr.Applied a => ResolveTypeName(a.Name, a.Args.Select(ResolveTypeExpr).ToList()),
            TypeExpr.Arrow arr => new TypeRef.TFun(ResolveTypeExpr(arr.From), ResolveTypeExpr(arr.To))
            {
                Row = arr.Uses is null ? null : ResolveUsesRow(arr.Uses)
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
    private (int, TypeRef) LowerRecordLit(Expr.RecordLit recLit)
    {
        if (!_constructorSymbols.TryGetValue(recLit.TypeName, out var ctor))
        {
            if (!_typeSymbols.TryGetValue(recLit.TypeName, out var typeSym))
            {
                ReportDiagnostic(GetSpan(recLit), $"Unknown record type '{recLit.TypeName}'.");
                return ReturnNeverWithDummyTemp();
            }

            // Type exists but no matching constructor — not a record type
            ReportDiagnostic(GetSpan(recLit), $"Type '{recLit.TypeName}' is not a record type.");
            return ReturnNeverWithDummyTemp();
        }

        var fieldNames = ctor.DeclaringSyntax.FieldNames;
        if (fieldNames.Count == 0)
        {
            ReportDiagnostic(GetSpan(recLit), $"Type '{recLit.TypeName}' is not a record type.");
            return ReturnNeverWithDummyTemp();
        }

        if (recLit.Fields.Count == 0 && ctor.Arity > 0)
        {
            ReportDiagnostic(GetSpan(recLit), $"Record literal for '{recLit.TypeName}' must provide all {ctor.Arity} field(s).");
            return ReturnNeverWithDummyTemp();
        }

        // Validate that all provided fields exist, and that all required fields are present
        var providedByName = new Dictionary<string, Expr>(StringComparer.Ordinal);
        foreach (var (name, value) in recLit.Fields)
        {
            if (!fieldNames.Contains(name, StringComparer.Ordinal))
            {
                ReportDiagnostic(GetSpan(recLit), $"Record type '{recLit.TypeName}' has no field '{name}'.");
            }
            else if (providedByName.ContainsKey(name))
            {
                ReportDiagnostic(GetSpan(recLit), $"Field '{name}' is provided more than once in record literal for '{recLit.TypeName}'.");
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
                ReportDiagnostic(GetSpan(recLit), $"Missing field '{fn}' in record literal for '{recLit.TypeName}'.");
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
    private (int, TypeRef) LowerRecordUpdate(Expr.RecordUpdate recUpdate)
    {
        var (targetTemp, targetType) = LowerExpr(recUpdate.Target);
        var prunedTarget = Prune(targetType);

        if (prunedTarget is not TypeRef.TNamedType namedType)
        {
            ReportDiagnostic(GetSpan(recUpdate), $"Record update requires a record type, got {Pretty(prunedTarget)}.");
            return ReturnNeverWithDummyTemp();
        }

        var typeSymbol = namedType.Symbol;
        if (typeSymbol.Constructors.Count != 1 || typeSymbol.Constructors[0].DeclaringSyntax.FieldNames.Count == 0)
        {
            ReportDiagnostic(GetSpan(recUpdate), $"Type '{typeSymbol.Name}' is not a record type and cannot be updated with '{{ with }}'.");
            return ReturnNeverWithDummyTemp();
        }

        var ctor = typeSymbol.Constructors[0];
        var fieldNames = ctor.DeclaringSyntax.FieldNames;

        // Validate update fields
        var updateByName = new Dictionary<string, Expr>(StringComparer.Ordinal);
        foreach (var (name, value) in recUpdate.Updates)
        {
            if (!fieldNames.Contains(name, StringComparer.Ordinal))
            {
                ReportDiagnostic(GetSpan(recUpdate), $"Record type '{typeSymbol.Name}' has no field '{name}'.");
                return ReturnNeverWithDummyTemp();
            }

            if (updateByName.ContainsKey(name))
            {
                ReportDiagnostic(GetSpan(recUpdate), $"Field '{name}' is updated more than once in record update for '{typeSymbol.Name}'.");
            }
            else
            {
                updateByName[name] = value;
            }
        }

        var resultType = namedType;
        int tag = GetConstructorTag(ctor);

        // Load all field values, then store update values, allocate new cell
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

        int ptrTemp = NewTemp();
        Emit(new IrInst.AllocAdt(ptrTemp, tag, ctor.Arity));
        for (int i = 0; i < fieldTemps.Length; i++)
        {
            Emit(new IrInst.SetAdtField(ptrTemp, i, fieldTemps[i]));
        }

        return (ptrTemp, resultType);
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
                : InferImplicitTypeParameters(decl.Name, decl.Constructors);

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
                        .Select(parameterName => ResolveUserConstructorParameterType(parameterName, declaredOrInferredTypeParameters, typeSymbol))
                        .ToList(),
                    DeclaringSyntax: ctor
                );
                ctorSymbols.Add(ctorSymbol);
                // Constructor names are globally visible (ML/F#-style): a later type's
                // constructor with the same name shadows an earlier one intentionally.
                _constructorSymbols[ctor.Name] = ctorSymbol;
            }

            _typeSymbols[decl.Name] = typeSymbol;

            var typeParams = typeSymbol.TypeParameters
                .Select(tp => (TypeRef)new TypeRef.TTypeParam(tp))
                .ToList();
            _resolvedTypes[decl.Name] = new TypeRef.TNamedType(typeSymbol, typeParams);
        }
    }

    private void RegisterExternDeclarations(IReadOnlyList<ExternDecl> externDecls)
    {
        foreach (var opaqueType in externDecls.OfType<ExternDecl.OpaqueType>())
        {
            if (!_externOpaqueTypes.Add(opaqueType.Name))
            {
                ReportDiagnostic(GetSpan(opaqueType), $"Duplicate extern type '{opaqueType.Name}'.");
            }
        }

        foreach (var function in externDecls.OfType<ExternDecl.Function>())
        {
            var parameterTypes = function.ParameterTypes.Select(t => ResolveExternParsedType(function, t, allowVoid: false)).ToList();
            var returnType = ResolveExternParsedType(function, function.ReturnType, allowVoid: true);
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

            var irFunction = new IrExternFunction(
                function.Name,
                symbolName,
                resolvedParameterTypes.Select(t => t.FfiType).ToList(),
                returnType.FfiType,
                string.IsNullOrWhiteSpace(libraryName) ? null : libraryName);
            _externFunctions.Add(irFunction);

            var type = BuildFunctionType(resolvedParameterTypes.Select(t => t.SourceType).ToList(), returnType.SourceType);
            _scopes.Peek()[function.Name] = new Binding.ExternFunction(irFunction, type);
        }
    }

    private ResolvedExternType? ResolveExternParsedType(ExternDecl externDecl, ParsedType parsedType, bool allowVoid)
    {
        if (parsedType is ParsedType.Pointer pointer)
        {
            var pointee = ResolveExternParsedType(externDecl, pointer.Pointee, allowVoid: false);
            return pointee is null
                ? null
                : new ResolvedExternType(new TypeRef.TPtr(pointee.SourceType), new FfiType.Ptr(pointee.FfiType));
        }

        if (parsedType is not ParsedType.Named named)
        {
            ReportDiagnostic(GetSpan(externDecl), "Unsupported extern type syntax.");
            return null;
        }

        return named.Name switch
        {
            "Int" => new ResolvedExternType(new TypeRef.TInt(), new FfiType.Int()),
            "u8" => new ResolvedExternType(new TypeRef.TInt(), new FfiType.UInt(8)),
            "u16" => new ResolvedExternType(new TypeRef.TInt(), new FfiType.UInt(16)),
            "u32" => new ResolvedExternType(new TypeRef.TInt(), new FfiType.UInt(32)),
            "u64" => new ResolvedExternType(new TypeRef.TInt(), new FfiType.UInt(64)),
            "Float" => new ResolvedExternType(new TypeRef.TFloat(), new FfiType.Float()),
            "Bool" => new ResolvedExternType(new TypeRef.TBool(), new FfiType.Bool()),
            "Str" => new ResolvedExternType(new TypeRef.TStr(), new FfiType.Str()),
            "void" when allowVoid => new ResolvedExternType(_resolvedTypes["Unit"], new FfiType.Void()),
            "void" => ReportVoidParameterExternType(externDecl),
            _ when _externOpaqueTypes.Contains(named.Name) => new ResolvedExternType(new TypeRef.TOpaque(named.Name), new FfiType.Opaque(named.Name)),
            _ => ReportUnsupportedExternType(externDecl, named.Name)
        };
    }

    private ResolvedExternType? ReportUnsupportedExternType(ExternDecl externDecl, string name)
    {
        ReportDiagnostic(GetSpan(externDecl), $"Type '{name}' is not supported in extern declarations.");
        return null;
    }

    private ResolvedExternType? ReportVoidParameterExternType(ExternDecl externDecl)
    {
        ReportDiagnostic(GetSpan(externDecl), "Type 'void' is only supported as an extern return type.");
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

    private sealed record ResolvedExternType(TypeRef SourceType, FfiType FfiType);

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

    private static TypeRef ResolveUserConstructorParameterType(
        string parameterName,
        IReadOnlyList<TypeParameter> declaredOrInferredTypeParameters,
        TypeSymbol declaringTypeSymbol)
    {
        var matchingParameter = declaredOrInferredTypeParameters.FirstOrDefault(tp => string.Equals(tp.Name, parameterName, StringComparison.Ordinal));
        if (matchingParameter is not null)
        {
            return new TypeRef.TTypeParam(
                declaringTypeSymbol.TypeParameters.First(tp => string.Equals(tp.Name, matchingParameter.Name, StringComparison.Ordinal)));
        }

        if (string.Equals(parameterName, declaringTypeSymbol.Name, StringComparison.Ordinal))
        {
            return new TypeRef.TNamedType(
                declaringTypeSymbol,
                declaringTypeSymbol.TypeParameters.Select(tp => (TypeRef)new TypeRef.TTypeParam(tp)).ToList());
        }

        if (string.Equals(parameterName, "Int", StringComparison.Ordinal))
        {
            return new TypeRef.TInt();
        }

        if (string.Equals(parameterName, "Bool", StringComparison.Ordinal))
        {
            return new TypeRef.TBool();
        }

        if (string.Equals(parameterName, "Str", StringComparison.Ordinal))
        {
            return new TypeRef.TStr();
        }

        if (string.Equals(parameterName, "Bytes", StringComparison.Ordinal))
        {
            return new TypeRef.TBytes();
        }

        if (string.Equals(parameterName, "Float", StringComparison.Ordinal))
        {
            return new TypeRef.TFloat();
        }

        return new TypeRef.TTypeParam(new TypeParameterSymbol(parameterName));
    }

    // Concrete primitive type names that may appear as constructor payloads. A payload naming one of
    // these is a concrete field type, never an implicit type parameter. (The full resolution list also
    // treats the declaring type's own name as concrete — handled per-declaration below.)
    private static readonly HashSet<string> PrimitivePayloadTypeNames =
        new(StringComparer.Ordinal) { "Int", "Bool", "Str", "Bytes", "Float" };

    private static IReadOnlyList<TypeParameter> InferImplicitTypeParameters(
        string declaringTypeName,
        IReadOnlyList<TypeConstructor> constructors)
    {
        var typeParameters = new List<TypeParameter>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var parameterName in constructors.SelectMany(ctor => ctor.Parameters))
        {
            // A payload that names the declaring type itself (a self-recursive field) or a primitive
            // type is a *concrete* field type, not an implicit type parameter. Inferring a parameter
            // for it over-generalizes the constructor: a self-recursive field becomes polymorphic —
            // which makes `let rec build n = ... Node(build(n - 1)) ...` fail the occurs check when the
            // type is actually built recursively — and a primitive field's concrete type is lost to
            // later analyses. `ResolveUserConstructorParameterType` already resolves both of these to
            // their concrete `TypeRef` once they are absent from the parameter list.
            if (string.Equals(parameterName, declaringTypeName, StringComparison.Ordinal)
                || PrimitivePayloadTypeNames.Contains(parameterName))
            {
                continue;
            }

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

    private (int, TypeRef) LowerNullaryConstructor(ConstructorSymbol ctor, bool stackAllocate = false)
    {
        var resultType = InstantiateAdtType(ctor);
        int tag = GetConstructorTag(ctor);

        // Allocate ADT heap cell: (1 + 0) * 8 = 8 bytes (tag only, no fields): [ctorTag]
        int ptrTemp = NewTemp();
        if (!stackAllocate && TryConsumeReuseToken(0, out int reuseTokenTemp))
        {
            // In-place reuse of a dead nullary cell (e.g. Leaf -> Leaf), keeping the rebuilt result
            // below the watermark so the enclosing loop can reset the arena.
            Emit(new IrInst.AllocReusing(ptrTemp, tag, 0, reuseTokenTemp));
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
        var argTypes = new List<TypeRef>(args.Count);
        for (int i = 0; i < args.Count; i++)
        {
            var (argTemp, argType) = LowerExpr(args[i]);
            argTemps.Add(argTemp);

            var parameterType = InstantiateConstructorParameterType(ctor, i, resultType);
            Unify(parameterType, argType);
            argTypes.Add(Prune(argType));
            MarkResourceArgMoved(args[i]);
        }

        int tag = GetConstructorTag(ctor);

        // Allocate a tagged heap cell: [ctorTag, field0, field1, ..., fieldN]
        int ptrTemp = NewTemp();
        bool reuseNode = false;
        int consumedTokenTemp = -1;
        if (!stackAllocate && TryConsumeReuseToken(ctor.Arity, out int reuseTokenTemp))
        {
            consumedTokenTemp = reuseTokenTemp;
            // In-place reuse: overwrite a same-size dead cell (the node a linear value was just
            // deconstructed from) instead of bump-allocating. The args were already read into temps
            // above, so overwriting the cell now is safe.
            Emit(new IrInst.AllocReusing(ptrTemp, tag, ctor.Arity, reuseTokenTemp));
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
            Emit(new IrInst.AllocAdt(ptrTemp, tag, ctor.Arity));
        }
        for (int i = 0; i < argTemps.Count; i++)
        {
            int fieldTemp = argTemps[i];
            // A FRESH heap leaf field of a reuse-built node (a Map key/value produced from the spec's
            // newKey/newValue input, on insert OR update) must be copied into the persistent blob, or it
            // dangles past the per-iteration reset (the node survives, but the field would point into
            // reclaimed scratch). Fields taken from the matched accumulator (pattern bindings) are already
            // persistent and are NOT copied — identified by the field argument being a variable that is
            // not one of the spec's fresh-input names (see _specFreshInputNames, propagated through
            // inlined helpers). Any non-variable field expression (a value computed in the arm, e.g. an
            // upsert's onHit(value) call) is fresh arena scratch and must be materialized as well —
            // over-materializing an already-persistent value only costs a copy, never correctness.
            if (_inSpecialization && _specFreshInputNames is not null
                && (args[i] is not Expr.Var fieldVar || _specFreshInputNames.Contains(fieldVar.Name)))
            {
                var pruned = Prune(argTypes[i]);
                if (pruned is TypeRef.TStr or TypeRef.TBytes)
                {
                    int persistentField = NewTemp();
                    Emit(new IrInst.CopyOutArenaToSpace(persistentField, fieldTemp, -1));
                    fieldTemp = persistentField;
                }
                else if (pruned is TypeRef.TTuple tup && tup.Elements.All(e => BuiltinRegistry.IsCopyType(Prune(e))))
                {
                    int sizeBytes = tup.Elements.Count * 8;
                    if (reuseNode && ReuseTokenFieldIsDead(consumedTokenTemp, i))
                    {
                        // Update path: the reused node's old value cell (a same-size blob tuple, still in
                        // field i until we overwrite it below) is dead. Overwrite its contents in place
                        // rather than allocating a fresh blob cell, so value storage is reused and the blob
                        // stays bounded by distinct keys instead of growing per update.
                        int oldValueTemp = NewTemp();
                        Emit(new IrInst.GetAdtField(oldValueTemp, ptrTemp, i));
                        Emit(new IrInst.CopyFixedInto(oldValueTemp, fieldTemp, sizeBytes));
                        fieldTemp = oldValueTemp;
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

            Emit(new IrInst.SetAdtField(ptrTemp, i, fieldTemp));
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
