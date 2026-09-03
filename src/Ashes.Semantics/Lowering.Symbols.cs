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
            TypeExpr.Named { Name: "Rune" } => new TypeRef.TRune(),
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
    private (int, TypeRef) LowerRecordLit(Expr.RecordLit recordLit, LoweredValueRequest request)
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

        return LowerRecordConstructor(
            recordLit,
            ctor,
            fieldNames,
            providedByName,
            request);
    }

    private (int Temp, TypeRef Type) LowerRecordConstructor(
        Expr.RecordLit recordLit,
        ConstructorSymbol constructor,
        IReadOnlyList<string> fieldNames,
        IReadOnlyDictionary<string, Expr> fields,
        LoweredValueRequest request) =>
        LowerConstructorApplication(
            constructor,
            fieldNames.Select(fieldName => fields[fieldName]).ToList(),
            location: ResolveSourceLocation(AstSpans.GetOrDefault(recordLit)),
            request: request);

    /// <summary>
    /// Lowers a record update expression:
    /// <c>{ target with field1 = e1, field2 = e2 }</c>.
    /// Produces a fresh ADT with unchanged fields copied and specified fields replaced.
    /// </summary>
    private (int, TypeRef) LowerRecordUpdate(
        Expr.RecordUpdate recordUpdate,
        LoweredValueRequest request)
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
        var fieldTemps = BuildRecordUpdateFieldTemps(ctor, fieldNames, updateByName, targetTemp, resultType, request);

        int ptrTemp = NewTemp();
        bool tagless = IsTaglessConstructor(ctor);
        Emit(new IrInst.AllocAdt(ptrTemp, tag, ctor.Arity, Tagless: tagless));
        for (int i = 0; i < fieldTemps.Length; i++)
        {
            Emit(new IrInst.SetAdtField(ptrTemp, i, fieldTemps[i], tagless));
        }

        return (ptrTemp, resultType);
    }

    /// <summary>
    /// Builds the per-field temps for a record update: updated fields are lowered and unified with
    /// their declared parameter types; unchanged fields are loaded from the update target.
    /// An updated field is a constructor argument of the rebuilt cell, so it takes the request the
    /// constructor path hands its arguments (an arena cell, hence no runtime-managed parent) and the
    /// same escaping-child retain: a runtime-managed value stored into the new cell — a list literal
    /// over a borrowed RC record, a `let`-bound call result — keeps a reference of its own past the
    /// scope that owns it, instead of dangling once that scope's release fires.
    /// </summary>
    private int[] BuildRecordUpdateFieldTemps(
        ConstructorSymbol ctor,
        IReadOnlyList<string> fieldNames,
        Dictionary<string, Expr> updateByName,
        int targetTemp,
        TypeRef.TNamedType resultType,
        LoweredValueRequest request)
    {
        LoweredValueRequest childRequest = request with
        {
            RuntimeRepresentation = request.RuntimeRepresentation
                & ~(LoweredValueRuntimeRepresentation.Adt | LoweredValueRuntimeRepresentation.Record),
        };
        var fieldTemps = new int[fieldNames.Count];
        for (int i = 0; i < fieldNames.Count; i++)
        {
            if (updateByName.TryGetValue(fieldNames[i], out var updateExpr))
            {
                TypeRef paramType = Prune(InstantiateConstructorParameterType(ctor, i, resultType));
                (int updateTemp, TypeRef updateType) = LowerRuntimeManagedConstructorArgument(
                    updateExpr,
                    paramType,
                    runtimeManagedParent: false,
                    childRequest);
                updateTemp = RetainEscapingConstructorArgument(
                    updateExpr,
                    updateTemp,
                    updateType,
                    runtimeManagedCandidate: false,
                    request);
                Unify(paramType, updateType);
                fieldTemps[i] = updateTemp;
                MarkResourceArgMoved(updateExpr);
            }
            else
            {
                int loadedTemp = NewTemp();
                Emit(new IrInst.GetAdtField(loadedTemp, targetTemp, i, IsTaglessConstructor(ctor)));
                fieldTemps[i] = loadedTemp;
            }
        }

        return fieldTemps;
    }

    private void RegisterTypeDeclarations(
        IReadOnlyList<TypeDecl> typeDecls,
        IReadOnlyList<TypeAliasDecl> typeAliasDecls,
        IReadOnlyList<ZeroCostTypeDecl> zeroCostTypeDecls)
    {
        // Every name that denotes a concrete type — builtins registered already, all user types in
        // this program (so forward references resolve), and the primitives. A constructor field that
        // names something outside this set is an implicit type parameter.
        var knownTypeNames = new HashSet<string>(_typeSymbols.Keys, StringComparer.Ordinal);
        knownTypeNames.UnionWith(typeDecls.Select(d => d.Name));
        knownTypeNames.UnionWith(typeAliasDecls.Select(declaration => declaration.Name));
        knownTypeNames.UnionWith(zeroCostTypeDecls.Select(declaration => declaration.Name));
        knownTypeNames.UnionWith(_externalOpaqueTypes);
        knownTypeNames.UnionWith(PrimitivePayloadTypeNames);
        knownTypeNames.Add("Unit");

        RegisterTypeAliases(typeAliasDecls);

        foreach (var decl in typeDecls)
        {
            RegisterTypeDeclaration(decl, knownTypeNames, isZeroCost: false);
        }

        foreach (ZeroCostTypeDecl declaration in zeroCostTypeDecls)
        {
            TypeDecl semanticDeclaration = new(
                declaration.Name,
                declaration.TypeParameters,
                [declaration.Constructor])
            {
                Deriving = declaration.Deriving,
            };
            AstSpans.Set(semanticDeclaration, GetSpan(declaration));
            RegisterTypeDeclaration(semanticDeclaration, knownTypeNames, isZeroCost: true);
        }

        foreach (TypeAliasDecl declaration in typeAliasDecls)
        {
            IReadOnlyList<TypeRef> parameters = declaration.TypeParameters
                .Select(parameter => (TypeRef)new TypeRef.TTypeParam(new TypeParameterSymbol(parameter.Name)))
                .ToList();
            _ = ResolveTypeAlias(declaration.Name, parameters);
        }
    }

    private void RegisterTypeAliases(IReadOnlyList<TypeAliasDecl> declarations)
    {
        foreach (TypeAliasDecl declaration in declarations)
        {
            if (BuiltinRegistry.IsReservedTypeName(declaration.Name))
            {
                ReportDiagnostic(GetSpan(declaration), "'Ashes' and built-in runtime types are reserved");
                continue;
            }

            if (_typeAliases.ContainsKey(declaration.Name) || _typeSymbols.ContainsKey(declaration.Name))
            {
                ReportDiagnostic(GetSpan(declaration), $"Duplicate type name '{declaration.Name}'.");
                continue;
            }

            HashSet<string> parameters = new(StringComparer.Ordinal);
            if (declaration.TypeParameters.Any(parameter => !parameters.Add(parameter.Name)))
            {
                ReportDiagnostic(GetSpan(declaration), $"Duplicate type parameter in alias '{declaration.Name}'.");
                continue;
            }

            _typeAliases[declaration.Name] = declaration;
        }
    }

    private void RegisterTypeDeclaration(TypeDecl decl, HashSet<string> knownTypeNames, bool isZeroCost)
    {
        if (BuiltinRegistry.IsReservedTypeName(decl.Name))
        {
            ReportDiagnostic(GetSpan(decl), "'Ashes' and built-in runtime types are reserved");
            return;
        }

        if (_typeSymbols.ContainsKey(decl.Name) || _typeAliases.ContainsKey(decl.Name))
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
            DeclaringSyntax: decl with { TypeParameters = declaredOrInferredTypeParameters },
            IsZeroCost: isZeroCost
        );
        // Register the type symbol (and its resolved TNamedType) before resolving field types, so
        // a self-recursive field (`type Tree = | Node(Tree, Tree)`) resolves its own name. The
        // constructor list is filled in place below.
        _typeSymbols[decl.Name] = typeSymbol;
        _typeProvenanceBySymbol[typeSymbol] = ResolveDeclarationProvenance(GetSpan(decl));
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

    private void RegisterExternalOpaqueTypes(IReadOnlyList<ExternalDecl> externalDecls)
    {
        foreach (var opaqueType in externalDecls.OfType<ExternalDecl.OpaqueType>())
        {
            if (!_externalOpaqueTypes.Add(opaqueType.Name))
            {
                ReportDiagnostic(GetSpan(opaqueType), $"Duplicate external type '{opaqueType.Name}'.");
            }
            else if (opaqueType.DestructorName is not null)
            {
                _externalResourceTypes[opaqueType.Name] = opaqueType;
            }
        }
    }

    private void RegisterExternalFunctions(IReadOnlyList<ExternalDecl> externalDecls)
    {
        foreach (ExternalDecl.Function function in externalDecls.OfType<ExternalDecl.Function>())
        {
            _externalFunctionDeclarations[function.Name] = function;
        }

        foreach (var function in externalDecls.OfType<ExternalDecl.Function>())
        {
            RegisterExternalFunction(function);
        }

        foreach ((string resourceName, ExternalDecl.OpaqueType declaration) in _externalResourceTypes)
        {
            if (!_externalResourceDestructors.ContainsKey(resourceName)
                && !_invalidExternalResourceDestructors.Contains(resourceName))
            {
                ReportDiagnostic(
                    GetSpan(declaration),
                    $"External resource '{resourceName}' requires destructor '{declaration.DestructorName}' with signature (consume {resourceName}) -> void.",
                    DiagnosticCodes.InvalidExternalResourceDestructor);
            }
        }
    }

    private void RegisterExternalFunction(ExternalDecl.Function function)
    {
        List<ResolvedExternalType?> parameterTypes = function.ParameterTypes
            .Select(type => ResolveExternalParsedType(
                function,
                type,
                allowVoid: false,
                allowBuffer: true,
                allowOut: true,
                allowNativeString: false))
            .ToList();
        ResolvedExternalType? returnType = ResolveExternalParsedType(
            function,
            function.ReturnType,
            allowVoid: true,
            allowBuffer: false,
            allowOut: false,
            allowNativeString: true);
        if (parameterTypes.Any(type => type is null) || returnType is null)
        {
            return;
        }

        List<ResolvedExternalType> resolvedParameters = parameterTypes.Select(type => type!).ToList();
        IReadOnlyList<FfiParameterOwnership> ownerships = ResolveExternalParameterOwnerships(
            function,
            resolvedParameters);
        (string symbolName, string? libraryName) = SplitExternalSymbol(function);
        var irFunction = new IrExternalFunction(
            function.Name,
            symbolName,
            resolvedParameters.Select(type => type.FfiType).ToList(),
            returnType.FfiType,
            libraryName)
        {
            ParameterOwnerships = ownerships,
        };
        irFunction = RegisterExternalResourceDestructor(
            function,
            irFunction,
            resolvedParameters,
            ownerships,
            returnType);
        IReadOnlyList<string> runtimeCapabilities = ResolveExternalRuntimeCapabilities(
            function,
            irFunction.DestructorForResource is not null);
        irFunction = irFunction with { RuntimeCapabilities = runtimeCapabilities };
        _externalFunctions.Add(irFunction);

        IReadOnlyList<TypeRef> sourceParameters = [.. resolvedParameters
            .Where(parameter => parameter.FfiType is not FfiType.Out)
            .Select(parameter => parameter.SourceType)];
        TypeRef sourceResult = BuildExternalResultType(resolvedParameters, returnType);
        TypeRef type = BuildFunctionType(sourceParameters, sourceResult);
        if (type is TypeRef.TFun)
        {
            type = WithCapabilityRow(type, BuiltinCapabilityRow(runtimeCapabilities));
        }
        SetCurrentScopeBinding(function.Name, new Binding.ExternalFunction(irFunction, type));
    }

    private IReadOnlyList<string> ResolveExternalRuntimeCapabilities(
        ExternalDecl.Function function,
        bool isDestructor)
    {
        if (function.Needs is null)
        {
            return isDestructor ? [] : [UnsafeFfiCapabilityName];
        }

        bool valid = function.Needs.TailVar is null;
        var names = new SortedSet<string>(StringComparer.Ordinal);
        foreach (CapabilityRefSyntax capability in function.Needs.Capabilities)
        {
            if (!IsBuiltinRuntimeCapability(capability.Name) || capability.Args.Count != 0)
            {
                valid = false;
                continue;
            }
            names.Add(capability.Name);
        }

        if (!valid)
        {
            ReportDiagnostic(
                GetSpan(function),
                $"External function '{function.Name}' may use only built-in runtime capabilities in a closed needs row.",
                UnknownCapabilityCode);
            return [UnsafeFfiCapabilityName];
        }

        return [.. names];
    }

    private static (string SymbolName, string? LibraryName) SplitExternalSymbol(
        ExternalDecl.Function function)
    {
        string symbolName = function.SymbolName ?? function.Name;
        int atIndex = symbolName.LastIndexOf('@');
        if (atIndex < 0)
        {
            return (symbolName, null);
        }

        string libraryName = symbolName[(atIndex + 1)..];
        return (symbolName[..atIndex], string.IsNullOrWhiteSpace(libraryName) ? null : libraryName);
    }

    private IrExternalFunction RegisterExternalResourceDestructor(
        ExternalDecl.Function function,
        IrExternalFunction irFunction,
        IReadOnlyList<ResolvedExternalType> parameterTypes,
        IReadOnlyList<FfiParameterOwnership> parameterOwnerships,
        ResolvedExternalType returnType)
    {
        IReadOnlyList<ExternalDecl.OpaqueType> resources = _externalResourceTypes.Values
            .Where(resource => string.Equals(resource.DestructorName, function.Name, StringComparison.Ordinal))
            .ToList();
        if (resources.Count == 1
            && IsValidExternalResourceDestructor(resources[0], parameterTypes, parameterOwnerships, returnType))
        {
            string resourceName = resources[0].Name;
            IrExternalFunction destructor = irFunction with { DestructorForResource = resourceName };
            _externalResourceDestructors[resourceName] = destructor;
            return destructor;
        }

        if (resources.Count > 1)
        {
            foreach (ExternalDecl.OpaqueType resource in resources)
            {
                _invalidExternalResourceDestructors.Add(resource.Name);
                ReportDiagnostic(
                    GetSpan(resource),
                    $"External resource destructor '{function.Name}' is assigned to more than one resource type.",
                    DiagnosticCodes.InvalidExternalResourceDestructor);
            }
        }
        return irFunction;
    }

    private IReadOnlyList<FfiParameterOwnership> ResolveExternalParameterOwnerships(
        ExternalDecl.Function function,
        IReadOnlyList<ResolvedExternalType> parameterTypes)
    {
        var result = new List<FfiParameterOwnership>(parameterTypes.Count);
        for (int i = 0; i < parameterTypes.Count; i++)
        {
            ExternalParameterOwnership written = i < function.ParameterOwnerships.Count
                ? function.ParameterOwnerships[i]
                : ExternalParameterOwnership.Unspecified;
            if (parameterTypes[i].FfiType is FfiType.Out)
            {
                if (written != ExternalParameterOwnership.Unspecified)
                {
                    ReportDiagnostic(
                        GetSpan(function),
                        $"External out parameter #{i + 1} of '{function.Name}' cannot use 'borrow' or 'consume'.",
                        DiagnosticCodes.InvalidFfiOutParameter);
                }
                result.Add(FfiParameterOwnership.Unspecified);
                continue;
            }
            bool isResource = IsDeclaredExternalResourceType(parameterTypes[i].SourceType);
            if (isResource && written == ExternalParameterOwnership.Unspecified)
            {
                ReportDiagnostic(
                    GetSpan(function),
                    $"External resource parameter #{i + 1} of '{function.Name}' must be marked 'borrow' or 'consume'.",
                    DiagnosticCodes.InvalidExternalOwnershipMarker);
            }
            else if (!isResource && written != ExternalParameterOwnership.Unspecified)
            {
                ReportDiagnostic(
                    GetSpan(function),
                    $"External ownership marker on parameter #{i + 1} of '{function.Name}' requires a direct resource type.",
                    DiagnosticCodes.InvalidExternalOwnershipMarker);
            }

            result.Add(written switch
            {
                ExternalParameterOwnership.Borrow => FfiParameterOwnership.Borrow,
                ExternalParameterOwnership.Consume => FfiParameterOwnership.Consume,
                _ => FfiParameterOwnership.Unspecified,
            });
        }
        return result;
    }

    private bool IsValidExternalResourceDestructor(
        ExternalDecl.OpaqueType resource,
        IReadOnlyList<ResolvedExternalType> parameterTypes,
        IReadOnlyList<FfiParameterOwnership> parameterOwnerships,
        ResolvedExternalType returnType)
    {
        bool valid = parameterTypes.Count == 1
            && parameterTypes[0].SourceType is TypeRef.TOpaque opaque
            && string.Equals(opaque.Name, resource.Name, StringComparison.Ordinal)
            && parameterOwnerships.Count == 1
            && parameterOwnerships[0] == FfiParameterOwnership.Consume
            && returnType.FfiType is FfiType.Void;
        if (!valid)
        {
            _invalidExternalResourceDestructors.Add(resource.Name);
            ReportDiagnostic(
                GetSpan(resource),
                $"External resource destructor '{resource.DestructorName}' must have signature (consume {resource.Name}) -> void.",
                DiagnosticCodes.InvalidExternalResourceDestructor);
        }
        return valid;
    }

    private bool IsDeclaredExternalResourceType(TypeRef type)
    {
        TypeRef represented = EraseZeroCostTypeRepresentation(type);
        return represented is TypeRef.TOpaque opaque
            && _externalResourceTypes.ContainsKey(opaque.Name);
    }

    private ResolvedExternalType? ResolveExternalParsedType(
        ExternalDecl externalDecl,
        ParsedType parsedType,
        bool allowVoid,
        bool allowBuffer,
        bool allowOut,
        bool allowNativeString)
    {
        if (parsedType is ParsedType.Pointer pointer)
        {
            ResolvedExternalType? pointee = ResolveExternalParsedType(
                externalDecl,
                pointer.Pointee,
                allowVoid: false,
                allowBuffer: false,
                allowOut: false,
                allowNativeString: false);
            return pointee is null
                ? null
                : new ResolvedExternalType(new TypeRef.TPtr(pointee.SourceType), new FfiType.Ptr(pointee.FfiType));
        }

        if (parsedType is ParsedType.Buffer buffer)
        {
            return ResolveExternalBufferType(externalDecl, buffer, allowBuffer);
        }

        if (parsedType is ParsedType.Out output)
        {
            return ResolveExternalOutType(externalDecl, output, allowOut);
        }

        if (parsedType is ParsedType.NativeString nativeString)
        {
            return ResolveExternalNativeStringType(externalDecl, nativeString, allowNativeString);
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
            _ => ResolveDeclaredExternalType(externalDecl, named.Name)
        };
    }

    private ResolvedExternalType? ResolveExternalBufferType(
        ExternalDecl externalDecl,
        ParsedType.Buffer buffer,
        bool allowBuffer)
    {
        if (!allowBuffer)
        {
            ReportDiagnostic(
                GetSpan(externalDecl),
                "FfiBuffer(T) is supported only as a direct external parameter.",
                DiagnosticCodes.InvalidFfiBuffer);
            return null;
        }

        ResolvedExternalType? element = ResolveExternalParsedType(
            externalDecl,
            buffer.Element,
            allowVoid: false,
            allowBuffer: false,
            allowOut: false,
            allowNativeString: false);
        if (element is null)
        {
            return null;
        }

        if (element.FfiType is not FfiType.Opaque opaque
            || element.SourceType is not TypeRef.TOpaque sourceOpaque)
        {
            ReportDiagnostic(
                GetSpan(externalDecl),
                "FfiBuffer(T) requires a copyable opaque external type T.",
                DiagnosticCodes.InvalidFfiBuffer);
            return null;
        }

        if (_externalResourceTypes.ContainsKey(sourceOpaque.Name))
        {
            ReportDiagnostic(
                GetSpan(externalDecl),
                $"FfiBuffer({sourceOpaque.Name}) cannot contain affine external resources.",
                DiagnosticCodes.InvalidFfiBuffer);
            return null;
        }

        return new ResolvedExternalType(
            new TypeRef.TList(element.SourceType),
            new FfiType.Buffer(opaque));
    }

    private ResolvedExternalType? ResolveExternalOutType(
        ExternalDecl externalDecl,
        ParsedType.Out output,
        bool allowOut)
    {
        if (!allowOut)
        {
            ReportDiagnostic(
                GetSpan(externalDecl),
                "out T is supported only as a direct external parameter.",
                DiagnosticCodes.InvalidFfiOutParameter);
            return null;
        }

        ResolvedExternalType? element = ResolveExternalParsedType(
            externalDecl,
            output.Element,
            allowVoid: false,
            allowBuffer: false,
            allowOut: false,
            allowNativeString: true);
        if (element is null)
        {
            return null;
        }
        if (element.FfiType is not (FfiType.Opaque or FfiType.Ptr or FfiType.NativeString))
        {
            ReportDiagnostic(
                GetSpan(externalDecl),
                "out T requires an opaque external type or pointer type T.",
                DiagnosticCodes.InvalidFfiOutParameter);
            return null;
        }

        if (element.FfiType is FfiType.NativeString nativeString)
        {
            if (nativeString.Nullable)
            {
                ReportDiagnostic(
                    GetSpan(externalDecl),
                    "out FfiStr(...) is already nullable and cannot also specify 'nullable'.",
                    DiagnosticCodes.InvalidFfiString);
                return null;
            }
            return new ResolvedExternalType(
                CreateStringResultType(CreateMaybeType(new TypeRef.TStr())),
                new FfiType.Out(nativeString));
        }

        return new ResolvedExternalType(CreateMaybeType(element.SourceType), new FfiType.Out(element.FfiType));
    }

    private ResolvedExternalType? ResolveExternalNativeStringType(
        ExternalDecl declaration,
        ParsedType.NativeString nativeString,
        bool allowNativeString)
    {
        if (!allowNativeString)
        {
            ReportDiagnostic(
                GetSpan(declaration),
                "FfiStr(...) is supported only as an external return or out-parameter element.",
                DiagnosticCodes.InvalidFfiString);
            return null;
        }

        string? destructorSymbol = null;
        string? destructorLibrary = null;
        if (nativeString.Ownership == FfiStringOwnership.Owned)
        {
            if (nativeString.DestructorName is null
                || !_externalFunctionDeclarations.TryGetValue(nativeString.DestructorName, out ExternalDecl.Function? destructor)
                || !IsValidNativeStringDestructor(destructor))
            {
                ReportDiagnostic(
                    GetSpan(declaration),
                    $"Owned FfiStr destructor '{nativeString.DestructorName}' must be an external (*u8) -> void function in the same file.",
                    DiagnosticCodes.InvalidFfiString);
                return null;
            }
            (destructorSymbol, destructorLibrary) = SplitExternalSymbol(destructor);
        }

        TypeRef success = nativeString.Nullable
            ? CreateMaybeType(new TypeRef.TStr())
            : new TypeRef.TStr();
        return new ResolvedExternalType(
            CreateStringResultType(success),
            new FfiType.NativeString(
                nativeString.Nullable,
                nativeString.Ownership == FfiStringOwnership.Owned
                    ? FfiNativeStringOwnership.Owned
                    : FfiNativeStringOwnership.Borrowed,
                destructorSymbol,
                destructorLibrary));
    }

    private static bool IsValidNativeStringDestructor(ExternalDecl.Function destructor) =>
        destructor.ParameterTypes.Count == 1
        && destructor.ParameterTypes[0] is ParsedType.Pointer { Pointee: ParsedType.Named { Name: "u8" } }
        && destructor.ParameterOwnerships.All(ownership => ownership == ExternalParameterOwnership.Unspecified)
        && destructor.ReturnType is ParsedType.Named { Name: "void" };

    private ResolvedExternalType? ResolveDeclaredExternalType(ExternalDecl declaration, string name)
    {
        TypeRef sourceType;
        if (_typeAliases.ContainsKey(name))
        {
            sourceType = ResolveTypeAlias(name, []);
        }
        else if (_typeSymbols.TryGetValue(name, out TypeSymbol? symbol) && symbol.IsZeroCost)
        {
            if (symbol.TypeParameters.Count != 0)
            {
                return ReportUnsupportedExternalType(declaration, name);
            }
            sourceType = new TypeRef.TNamedType(symbol, []);
        }
        else
        {
            return ReportUnsupportedExternalType(declaration, name);
        }

        FfiType? ffiType = FfiTypeForRepresentation(EraseZeroCostTypeRepresentation(sourceType));
        if (ffiType is null)
        {
            return ReportUnsupportedExternalType(declaration, name);
        }
        return new ResolvedExternalType(sourceType, ffiType);
    }

    private static FfiType? FfiTypeForRepresentation(TypeRef type) => type switch
    {
        TypeRef.TInt => new FfiType.Int(),
        TypeRef.TUInt unsigned => new FfiType.UInt(unsigned.Bits),
        TypeRef.TFloat => new FfiType.Float(),
        TypeRef.TBool => new FfiType.Bool(),
        TypeRef.TStr => new FfiType.Str(),
        TypeRef.TOpaque opaque => new FfiType.Opaque(opaque.Name),
        TypeRef.TPtr pointer when FfiTypeForRepresentation(pointer.Pointee) is { } pointee =>
            new FfiType.Ptr(pointee),
        _ => null,
    };

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

    private static TypeRef BuildExternalResultType(
        IReadOnlyList<ResolvedExternalType> parameterTypes,
        ResolvedExternalType returnType)
    {
        List<TypeRef> components = [];
        if (returnType.FfiType is not FfiType.Void)
        {
            components.Add(returnType.SourceType);
        }
        components.AddRange(parameterTypes
            .Where(parameter => parameter.FfiType is FfiType.Out)
            .Select(parameter => parameter.SourceType));
        return components.Count switch
        {
            0 => returnType.SourceType,
            1 => components[0],
            _ => new TypeRef.TTuple(components),
        };
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
            _typeProvenanceBySymbol[typeSymbol] = new TraitDeclarationProvenance(
                "ashes-core",
                "Ashes.Core",
                $"<builtin:{builtinType.Name}>",
                new TextSpan(0, 0));
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
                _builtinConstructorSymbols[constructor.Name] = constructor;
            }
        }
    }

    private bool TryResolveConstructorSymbol(
        string name,
        TextSpan contextSpan,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ConstructorSymbol? constructor)
    {
        TraitDeclarationProvenance context = ResolveDeclarationProvenance(contextSpan);
        if (string.Equals(context.PackageId, "ashes-core", StringComparison.Ordinal)
            && _builtinConstructorSymbols.TryGetValue(name, out constructor))
        {
            return true;
        }
        return _constructorSymbols.TryGetValue(name, out constructor);
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
        new(StringComparer.Ordinal) { "Int", "Bool", "Str", "Bytes", "Float", "BigInt", "Rune" };

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

    private TypeRef GetZeroCostTypePayload(TypeRef.TNamedType named)
    {
        Debug.Assert(named.Symbol.IsZeroCost);
        ConstructorSymbol constructor = named.Symbol.Constructors[0];
        return Prune(InstantiateConstructorParameterType(constructor, 0, named));
    }

    private TypeRef EraseZeroCostTypeRepresentation(TypeRef type)
    {
        TypeRef current = Prune(type);
        var seen = new HashSet<TypeSymbol>(ReferenceEqualityComparer.Instance);
        while (current is TypeRef.TNamedType { Symbol.IsZeroCost: true } named
            && seen.Add(named.Symbol))
        {
            current = Prune(GetZeroCostTypePayload(named));
        }

        return current;
    }

    /// <summary>
    /// Resolves a written type name and optional type arguments to a <see cref="TypeRef"/>, handling
    /// built-in primitives, declared and built-in named types, and type parameters. Reports a
    /// diagnostic and yields <see cref="TypeRef.TNever"/> on arity mismatch or an unknown name.
    /// </summary>
    public TypeRef ResolveTypeName(string name, IReadOnlyList<TypeRef>? typeArgs = null)
    {
        typeArgs ??= [];
        if (_typeAliases.ContainsKey(name))
        {
            return ResolveTypeAlias(name, typeArgs);
        }
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

        if (_externalOpaqueTypes.Contains(name))
        {
            if (typeArgs.Count != 0)
            {
                ReportDiagnostic(0, $"Opaque external type '{name}' expects 0 type argument(s) but got {typeArgs.Count}.");
                return new TypeRef.TNever();
            }
            return new TypeRef.TOpaque(name);
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

    private TypeRef ResolveTypeAlias(string name, IReadOnlyList<TypeRef> typeArgs)
    {
        TypeAliasDecl declaration = _typeAliases[name];
        if (typeArgs.Count != declaration.TypeParameters.Count)
        {
            ReportDiagnostic(
                GetSpan(declaration),
                $"Type alias '{name}' expects {declaration.TypeParameters.Count} type argument(s) but got {typeArgs.Count}.");
            return new TypeRef.TNever();
        }

        int cycleStart = _typeAliasExpansionStack.IndexOf(name);
        if (cycleStart >= 0)
        {
            List<string> cycle = _typeAliasExpansionStack.Skip(cycleStart).Append(name).ToList();
            string cycleText = string.Join(" -> ", cycle);
            string cycleKey = string.Join(
                "\0",
                cycle.Take(cycle.Count - 1).OrderBy(part => part, StringComparer.Ordinal));
            if (_reportedTypeAliasCycles.Add(cycleKey))
            {
                ReportDiagnostic(
                    GetSpan(declaration),
                    $"Recursive type alias cycle: {cycleText}.",
                    DiagnosticCodes.RecursiveTypeAlias);
            }
            return new TypeRef.TNever();
        }

        Dictionary<string, TypeRef>? savedScope = _typeExprParamScope;
        _typeExprParamScope = declaration.TypeParameters
            .Select((parameter, index) => (parameter.Name, Type: typeArgs[index]))
            .ToDictionary(item => item.Name, item => item.Type, StringComparer.Ordinal);
        _typeAliasExpansionStack.Add(name);
        try
        {
            return ResolveTypeExpr(declaration.Target);
        }
        finally
        {
            _typeAliasExpansionStack.RemoveAt(_typeAliasExpansionStack.Count - 1);
            _typeExprParamScope = savedScope;
        }
    }

    private (int, TypeRef) LowerNullaryConstructor(
        ConstructorSymbol ctor,
        bool stackAllocate = false,
        SourceLocation? location = null,
        LoweredValueRequest request = default)
    {
        var resultType = InstantiateAdtType(ctor);
        int tag = GetConstructorTag(ctor);
        bool runtimeReuseRequest = RuntimeReuseAllocationMatches(resultType, request);
        bool runtimeManagedCandidate =
            (request.EmitsRuntime(LoweredValueRuntimeRepresentation.Adt)
                || request.EmitsRuntime(LoweredValueRuntimeRepresentation.Record)
                || request.EmitsRuntime(LoweredValueRuntimeRepresentation.TcoAdt)
                || runtimeReuseRequest)
            && (CanRuntimeManageCopyAdt(resultType)
                || CanRuntimeManageAdt(resultType)
                || CanRuntimeManageOwnedChildAdt(resultType)
                || CanRuntimeManageRecursiveCopyAdt(resultType));

        // Allocate ADT heap cell: (1 + 0) * 8 = 8 bytes (tag only, no fields): [ctorTag]
        int ptrTemp = NewTemp();
        ReuseTokenMatch tokenMatch = !stackAllocate
            ? TryConsumeReuseToken(
                0,
                runtimeManagedCandidate,
                listCell: false,
                tagless: IsTaglessConstructor(ctor),
                targetConstructor: ctor.Name,
                location)
            : default;
        if (tokenMatch.Token is { } reuseToken)
        {
            EmitReusedNullaryConstructor(ctor, tag, ptrTemp, reuseToken, location);
        }
        else
        {
            EmitFreshNullaryConstructor(
                ctor,
                tag,
                ptrTemp,
                stackAllocate,
                runtimeManagedCandidate,
                tokenMatch,
                location);
        }
        if (runtimeManagedCandidate)
        {
            MarkRuntimeManagedTemp(ptrTemp);
        }
        return (ptrTemp, resultType);
    }

    private void EmitReusedNullaryConstructor(
        ConstructorSymbol ctor,
        int tag,
        int ptrTemp,
        ReuseToken reuseToken,
        SourceLocation? location)
    {
        // In-place reuse of a dead nullary cell (e.g. Leaf -> Leaf), keeping the rebuilt result
        // below the watermark so the enclosing loop can reset the arena.
        EmitRuntimeReuseTokenChildrenDrop(
            reuseToken.Temp,
            reuseToken.RuntimeCleanup);
        Emit(new IrInst.AllocReusing(
            ptrTemp,
            tag,
            0,
            reuseToken.Temp,
            reuseToken.RuntimeManaged));
        _reuseResultTemps.Add(ptrTemp);
        RecordReuseTokenDisposition(
            reuseToken,
            ReuseDecisionOutcome.Consumed,
            ReuseDecisionReason.CompatibleTokenConsumed,
            ptrTemp,
            ctor.Name,
            location);
        if (reuseToken.RuntimeManaged)
        {
            RecordReuseFallbackAllocation(
                reuseToken,
                ptrTemp,
                ctor.Name,
                location,
                ReuseDecisionOutcome.Available,
                ReuseDecisionReason.RuntimeUniquenessFallback,
                ReuseFallbackAllocationKind.RuntimeRc);
        }
    }

    private void EmitFreshNullaryConstructor(
        ConstructorSymbol ctor,
        int tag,
        int ptrTemp,
        bool stackAllocate,
        bool runtimeManagedCandidate,
        ReuseTokenMatch tokenMatch,
        SourceLocation? location)
    {
        if (stackAllocate)
        {
            Emit(new IrInst.AllocAdtStack(ptrTemp, tag, 0));
            return;
        }

        if (_inSpecialization)
        {
            // Fresh nullary cell inside a reuse specialization belongs in persistent to-space.
            Emit(new IrInst.AllocAdtToSpace(ptrTemp, tag, 0));
            _reuseResultTemps.Add(ptrTemp);
            RecordReuseFallbackAllocation(
                null,
                ptrTemp,
                ctor.Name,
                location,
                ReuseDecisionOutcome.Allocated,
                ReuseFallbackReason(tokenMatch),
                ReuseFallbackAllocationKind.ToSpace,
                fieldCount: 0,
                listCell: false,
                runtimeManaged: false);
            return;
        }

        Emit(new IrInst.AllocAdt(ptrTemp, tag, 0, runtimeManagedCandidate));
        if (ShouldRecordReuseFallback(tokenMatch))
        {
            RecordReuseFallbackAllocation(
                null,
                ptrTemp,
                ctor.Name,
                location,
                ReuseDecisionOutcome.Allocated,
                ReuseFallbackReason(tokenMatch),
                runtimeManagedCandidate
                    ? ReuseFallbackAllocationKind.RuntimeRc
                    : ReuseFallbackAllocationKind.Arena,
                fieldCount: 0,
                listCell: false,
                runtimeManagedCandidate);
        }
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

    private (int, TypeRef) LowerConstructorApplication(
        ConstructorSymbol ctor,
        List<Expr> args,
        bool stackAllocate = false,
        SourceLocation? location = null,
        LoweredValueRequest request = default)
    {
        if (args.Count != ctor.Arity)
        {
            return ReportConstructorArityMismatch(ctor, args);
        }

        var resultType = InstantiateAdtType(ctor);
        bool runtimeReuseRequest = resultType is TypeRef.TNamedType reuseNamed
            && RuntimeReuseAllocationMatches(reuseNamed, request);
        bool runtimeManagedCandidate = resultType is TypeRef.TNamedType named
            && IsRuntimeManagedConstructorCandidate(
                ctor,
                args,
                named,
                runtimeReuseRequest,
                request);

        (List<int> argTemps, List<TypeRef> argTypes) = LowerConstructorArguments(
            ctor,
            args,
            resultType,
            runtimeManagedCandidate,
            request);
        if (ctor.ParentType is { } parentType
            && _typeSymbols[parentType].IsZeroCost)
        {
            return (argTemps[0], resultType);
        }
        PrepareConstructorArgumentOwnership(
            args,
            argTypes,
            argTemps,
            runtimeManagedCandidate,
            request.RuntimeAdtChildBindings);

        int tag = GetConstructorTag(ctor);

        // Allocate a tagged heap cell: [ctorTag, field0, field1, ..., fieldN]
        int ptrTemp = AllocateConstructorCell(
            ctor,
            tag,
            stackAllocate,
            runtimeManagedCandidate,
            args,
            argTemps,
            location,
            out bool reuseNode,
            out int consumedTokenTemp);
        for (int i = 0; i < argTemps.Count; i++)
        {
            bool tagless = IsTaglessConstructor(ctor);
            int fieldTemp = MaterializeSpecializationField(args[i], argTypes[i], argTemps[i], ptrTemp, i, reuseNode, consumedTokenTemp, tagless, resultType);
            Emit(new IrInst.SetAdtField(ptrTemp, i, fieldTemp, tagless));
        }
        if (runtimeManagedCandidate)
        {
            MarkRuntimeManagedTemp(ptrTemp);
        }

        return (ptrTemp, resultType);
    }

    private void PrepareConstructorArgumentOwnership(
        IReadOnlyList<Expr> arguments,
        IReadOnlyList<TypeRef> argumentTypes,
        List<int> argumentTemps,
        bool runtimeManagedCandidate,
        IReadOnlyDictionary<string, bool>? childBindings)
    {
        RetainRuntimeManagedTcoConstructorArguments(arguments, argumentTypes, argumentTemps);
        if (!runtimeManagedCandidate)
        {
            return;
        }

        if (childBindings is null)
        {
            RetainRuntimeManagedOwnedChildArguments(arguments, argumentTypes, argumentTemps);
        }
        else
        {
            PrepareRuntimeManagedAdtChildArguments(arguments, argumentTemps, childBindings);
        }
    }

    // A runtime-RC aggregate owns its RC children. Outside a tail self-call (whose argument context
    // decides per binding whether to move or share, see PrepareRuntimeManagedAdtChildArguments), a
    // child read from a runtime-managed owned binding is retained here: the binding's own scope-exit
    // release still fires, and without the extra reference the aggregate would carry a freed child
    // out of that scope (`let updated = f(xs) in Hit(updated)`).
    private void RetainRuntimeManagedOwnedChildArguments(
        IReadOnlyList<Expr> arguments,
        IReadOnlyList<TypeRef> argumentTypes,
        List<int> argumentTemps)
    {
        for (int index = 0; index < arguments.Count; index++)
        {
            if (arguments[index] is not Expr.Var variable
                || LookupOwnedValue(variable.Name) is not
                { RuntimeManaged: true, IsDropped: false, PerceusPatternOwner: false } info)
            {
                continue;
            }

            argumentTemps[index] = DuplicateRuntimeManagedOwnedValueForTransfer(
                arguments[index],
                argumentTemps[index],
                argumentTypes[index]);
            info.RuntimeDeepUnique = false;
        }
    }

    private void RetainRuntimeManagedTcoConstructorArguments(
        IReadOnlyList<Expr> arguments,
        IReadOnlyList<TypeRef> argumentTypes,
        List<int> argumentTemps)
    {
        for (int index = 0; index < arguments.Count; index++)
        {
            if (arguments[index] is not Expr.Var variable
                || Lookup(variable.Name) is not Binding.Local local
                || _tcoCtx?.ParamSlots.Contains(local.Slot) != true)
            {
                continue;
            }

            argumentTemps[index] = IsRuntimeManagedTcoParamSlot(local)
                ? EmitRuntimeManagedConstructorFieldRetain(
                    argumentTemps[index],
                    MayUseEmptyListRepresentation(argumentTypes[index]))
                : EmitPendingRuntimeManagedConstructorFieldRetain(argumentTemps[index], local.Slot);
        }
    }

    private int EmitRuntimeManagedConstructorFieldRetain(int sourceTemp, bool mayBeEmpty)
    {
        int retainedTemp = NewTemp();
        Emit(new IrInst.RcDup(retainedTemp, sourceTemp, RuntimeManaged: true, MayBeEmpty: mayBeEmpty));
        return retainedTemp;
    }

    private int EmitPendingRuntimeManagedConstructorFieldRetain(int sourceTemp, int parameterSlot)
    {
        int runtimeManagedFlagTemp = NewTemp();
        Emit(new IrInst.LoadConstInt(runtimeManagedFlagTemp, 1));
        _pendingRuntimeArgumentFlags[runtimeManagedFlagTemp] = parameterSlot;

        int resultSlot = NewLocal();
        Emit(new IrInst.StoreLocal(resultSlot, sourceTemp));
        string doneLabel = NewLabel("rc_constructor_field_not_retained");
        Emit(new IrInst.JumpIfFalse(runtimeManagedFlagTemp, doneLabel));
        int retainedTemp = EmitRuntimeManagedConstructorFieldRetain(sourceTemp, mayBeEmpty: true);
        Emit(new IrInst.StoreLocal(resultSlot, retainedTemp));
        Emit(new IrInst.Label(doneLabel));
        int resultTemp = NewTemp();
        Emit(new IrInst.LoadLocal(resultTemp, resultSlot));
        return resultTemp;
    }

    private (int Temp, TypeRef Type) ReportConstructorArityMismatch(
        ConstructorSymbol constructor,
        IReadOnlyList<Expr> arguments)
    {
        TextSpan errorSpan = arguments.Count > 0
            ? GetSpan(arguments[0])
            : GetSpan(constructor.DeclaringSyntax);
        ReportDiagnostic(errorSpan, $"Constructor '{constructor.Name}' expects {constructor.Arity} argument(s) but got {arguments.Count}. Expected shape: {FormatConstructorShape(constructor)}.");
        foreach (Expr argument in arguments)
        {
            LowerExpr(argument);
        }

        return ReturnNeverWithDummyTemp();
    }

    private bool IsRuntimeManagedConstructorCandidate(
        ConstructorSymbol constructor,
        IReadOnlyList<Expr> arguments,
        TypeRef.TNamedType resultType,
        bool runtimeReuseRequest,
        LoweredValueRequest request)
        => request.EmitsRuntime(LoweredValueRuntimeRepresentation.Record)
                && CanRuntimeManageConstructorApplication(
                    constructor,
                    arguments,
                    resultType,
                    request.RuntimeAdtChildBindings)
            || (request.EmitsRuntime(LoweredValueRuntimeRepresentation.Adt)
                    || runtimeReuseRequest)
                && (CanRuntimeManageCopyAdt(resultType)
                    || CanRuntimeManageGenericCopyAdtConstructorApplication(constructor, arguments, resultType)
                    || CanRuntimeManageFreshHeapChildAdtConstructorApplication(constructor, arguments, resultType)
                    || CanRuntimeManageOwnedChildAdtConstructorApplication(
                        constructor,
                        arguments,
                        resultType,
                        request.RuntimeAdtChildBindings)
                    // A positional, single-constructor accumulator shape (e.g. a TCO loop's own state
                    // record) nested as a field of an escaping ADT reaches this same ambient allocation
                    // request too — not only the TCO tail-call-argument path below, which is gated on a
                    // separate, narrower ambient flag. Consulting the same eligibility test here lets a
                    // constructor that embeds one of these as a child field (rather than as its own
                    // loop-carried parameter) still qualify.
                    || CanRuntimeManageTcoOwnedChildAdtConstructorApplication(constructor, arguments, resultType)
                    || CanRuntimeManageRecursiveAdtConstructorApplication(
                        constructor,
                        arguments,
                        resultType,
                        request.RuntimeAdtChildBindings)
                    || runtimeReuseRequest
                        && CanRuntimeReuseAdtConstructorApplication(constructor, arguments, resultType))
            || request.EmitsRuntime(LoweredValueRuntimeRepresentation.TcoAdt)
                && CanRuntimeManageTcoOwnedChildAdtConstructorApplication(
                    constructor,
                    arguments,
                    resultType);

    private static bool RuntimeReuseAllocationMatches(
        TypeRef.TNamedType resultType,
        LoweredValueRequest request)
        => request.RuntimeReuseAdtType is { } requested
            && ReferenceEquals(requested.Symbol, resultType.Symbol);

    private bool CanRuntimeReuseAdtConstructorApplication(
        ConstructorSymbol constructor,
        IReadOnlyList<Expr> arguments,
        TypeRef.TNamedType resultType)
    {
        if ((!CanRuntimeManageAdt(resultType)
                && !CanRuntimeManageOwnedChildAdt(resultType)
                && !CanRuntimeManageRecursiveCopyAdt(resultType))
            || arguments.Count != constructor.Arity)
        {
            return false;
        }

        for (int i = 0; i < constructor.Arity; i++)
        {
            TypeRef fieldType = Prune(InstantiateConstructorParameterType(constructor, i, resultType));
            if (CanArenaReset(fieldType)
                || (CanRuntimeManageRecursiveCopyAdt(resultType)
                    && IsFreshConstructorTree(arguments[i], resultType.Symbol))
                || ((CanRuntimeManageAdt(resultType)
                        || CanRuntimeManageOwnedChildAdt(resultType))
                    && arguments[i] is Expr.RecordLit))
            {
                continue;
            }

            if (arguments[i] is not Expr.Var variable
                || !_reuseTokens.Any(token => token.FieldCount == constructor.Arity
                    && token.RuntimeCleanup is { } cleanup
                    && ReferenceEquals(cleanup.Type.Symbol, resultType.Symbol)
                    && cleanup.TransferableFields.ContainsKey(variable.Name)))
            {
                return false;
            }
        }

        return true;
    }

    private void PrepareRuntimeManagedAdtChildArguments(
        IReadOnlyList<Expr> arguments,
        List<int> argumentTemps,
        IReadOnlyDictionary<string, bool>? childBindings)
    {
        if (childBindings is null)
        {
            return;
        }

        for (int i = 0; i < arguments.Count; i++)
        {
            if (arguments[i] is not Expr.Var variable
                || !childBindings.TryGetValue(variable.Name, out bool shared)
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
        bool runtimeManagedCandidate,
        LoweredValueRequest request)
    {
        LoweredValueRuntimeRepresentation childRepresentations =
            request.RuntimeRepresentation
            & ~(LoweredValueRuntimeRepresentation.Adt
                | LoweredValueRuntimeRepresentation.Record);
        bool runtimeManagedAdt =
            runtimeManagedCandidate
            && request.EmitsRuntime(LoweredValueRuntimeRepresentation.Adt);
        if (runtimeManagedAdt)
        {
            childRepresentations |= LoweredValueRuntimeRepresentation.Adt;
        }
        if (runtimeManagedCandidate)
        {
            childRepresentations |= LoweredValueRuntimeRepresentation.Record;
        }
        LoweredValueRequest childRequest = request with
        {
            ConsumerCanOwn = request.ConsumerCanOwn || runtimeManagedCandidate,
            RuntimeRepresentation = childRepresentations,
        };

        var argumentTemps = new List<int>(arguments.Count);
        var argumentTypes = new List<TypeRef>(arguments.Count);
        for (int i = 0; i < arguments.Count; i++)
        {
            TypeRef fieldType = Prune(InstantiateConstructorParameterType(constructor, i, resultType));
            (int argumentTemp, TypeRef argumentType) = LowerRuntimeManagedConstructorArgument(
                arguments[i],
                fieldType,
                runtimeManagedCandidate,
                childRequest);
            argumentTemp = RetainEscapingConstructorArgument(
                arguments[i],
                argumentTemp,
                argumentType,
                runtimeManagedCandidate,
                request);
            argumentTemps.Add(argumentTemp);
            TypeRef parameterType = InstantiateConstructorParameterType(constructor, i, resultType);
            Unify(parameterType, argumentType);
            argumentTypes.Add(Prune(argumentType));
            MarkResourceArgMoved(arguments[i]);
        }

        return (argumentTemps, argumentTypes);
    }

    /// <summary>
    /// Retains a constructor argument that outlives the scope building the aggregate. Runtime-RC
    /// aggregates own their RC children. An arena aggregate also needs a retained child when an
    /// owning consumer may normalize the aggregate after its pattern root has been released (for
    /// example a TCO function result); purely local arena aggregates borrow instead, since retaining
    /// those would leave no arena-shell drop site. Independently of that, a value that escapes the
    /// current iteration's binding scopes without an owning consumer of the aggregate itself (a tail
    /// self-call argument, which becomes the next iteration's parameter) still carries a
    /// runtime-managed owned binding stored inside it out of that binding's scope: the binding's own
    /// scope-exit release fires at the back edge, so the aggregate needs its own retained reference
    /// or the next iteration reads a freed value.
    /// </summary>
    private int RetainEscapingConstructorArgument(
        Expr argument,
        int argumentTemp,
        TypeRef argumentType,
        bool runtimeManagedCandidate,
        LoweredValueRequest request)
    {
        bool owningConsumer = runtimeManagedCandidate
            || request.ConsumerCanOwn
            || _tcoCtx?.InTailPosition == true;
        if (owningConsumer)
        {
            argumentTemp = DuplicatePerceusPatternOwnerForAggregate(argument, argumentTemp);
        }

        if (!runtimeManagedCandidate && request.TransfersRuntimeManagedChildren)
        {
            argumentTemp = DuplicateRuntimeManagedOwnedValueForTransfer(argument, argumentTemp, argumentType);
        }

        return argumentTemp;
    }

    private (int Temp, TypeRef Type) LowerRuntimeManagedConstructorArgument(
        Expr argument,
        TypeRef fieldType,
        bool runtimeManagedParent,
        LoweredValueRequest parentRequest)
    {
        LoweredValueRequest request = parentRequest
            .AddRuntime(
                runtimeManagedParent
                    && (fieldType is TypeRef.TStr
                        || fieldType is TypeRef.TVar or TypeRef.TTypeParam)
                    && IsRuntimeRcStringProducer(argument)
                    && IsRuntimeRcClosureCaptureSafeStringProducer(argument),
                LoweredValueRuntimeRepresentation.String)
            .AddRuntime(
                runtimeManagedParent
                    && (fieldType is TypeRef.TBytes
                        || fieldType is TypeRef.TVar or TypeRef.TTypeParam)
                    && IsRuntimeRcBytesProducer(argument)
                    && IsRuntimeRcClosureCaptureSafeBytesProducer(argument),
                LoweredValueRuntimeRepresentation.Bytes)
            .AddRuntime(
                runtimeManagedParent
                    && (fieldType is TypeRef.TBigInt
                        || fieldType is TypeRef.TVar or TypeRef.TTypeParam)
                    && IsRuntimeRcBigIntProducer(argument)
                    && IsRuntimeRcClosureCaptureSafeBigIntProducer(argument),
                LoweredValueRuntimeRepresentation.BigInt)
            .AddRuntime(
                runtimeManagedParent
                    && (fieldType is TypeRef.TList
                        || fieldType is TypeRef.TVar or TypeRef.TTypeParam)
                    && IsFreshListConstructionExpression(argument),
                LoweredValueRuntimeRepresentation.List)
            .AddRuntime(
                runtimeManagedParent
                    && (fieldType is TypeRef.TTuple
                        || fieldType is TypeRef.TVar or TypeRef.TTypeParam)
                    && argument is Expr.TupleLit,
                LoweredValueRuntimeRepresentation.Tuple)
            .WithRuntimeAdtContext(parentRequest.RuntimeAdtChildBindings);
        LoweredValue loweredValue = LowerExpr(argument, request);
        if (runtimeManagedParent && fieldType is TypeRef.TBytes)
        {
            loweredValue = NormalizeRuntimeManagedBytesValue(loweredValue);
        }

        (int Temp, TypeRef Type) lowered = loweredValue.AsPair();
        if (runtimeManagedParent
            && fieldType is TypeRef.TList list
            && CanArenaReset(Prune(list.Element))
            && !IsRuntimeManagedResultTemp(lowered.Temp))
        {
            int normalizedTemp = NewTemp();
            Emit(new IrInst.CopyOutList(
                normalizedTemp,
                lowered.Temp,
                IrInst.ListHeadCopyKind.Inline,
                RuntimeManaged: true,
                IrInst.CopyOutPurpose.RcNormalization));
            MarkRuntimeManagedTemp(normalizedTemp);
            lowered = (normalizedTemp, lowered.Type);
        }
        return lowered;
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
        IReadOnlyList<Expr> arguments,
        List<int> argumentTemps,
        SourceLocation? location,
        out bool reuseNode,
        out int consumedTokenTemp)
    {
        int ptrTemp = NewTemp();
        reuseNode = false;
        consumedTokenTemp = -1;
        ReuseTokenMatch tokenMatch = !stackAllocate
            ? TryConsumeReuseToken(
                ctor.Arity,
                runtimeManagedCandidate,
                listCell: false,
                tagless: IsTaglessConstructor(ctor),
                targetConstructor: ctor.Name,
                location)
            : default;
        if (tokenMatch.Token is { } reuseToken)
        {
            consumedTokenTemp = reuseToken.Temp;
            EmitReusedConstructorCell(
                ctor,
                tag,
                ptrTemp,
                arguments,
                argumentTemps,
                reuseToken,
                location);
            reuseNode = true;
        }
        else
        {
            EmitFreshConstructorCell(
                ctor,
                tag,
                ptrTemp,
                stackAllocate,
                runtimeManagedCandidate,
                tokenMatch,
                location);
        }

        return ptrTemp;
    }

    private void EmitReusedConstructorCell(
        ConstructorSymbol ctor,
        int tag,
        int ptrTemp,
        IReadOnlyList<Expr> arguments,
        List<int> argumentTemps,
        ReuseToken reuseToken,
        SourceLocation? location)
    {
        // The arguments were lowered before this call, so overwriting the dead cell is safe.
        HashSet<int> transferredFields = PrepareRuntimeReuseTransferredChildren(
            arguments,
            argumentTemps,
            reuseToken.Temp,
            reuseToken.RuntimeCleanup);
        EmitRuntimeReuseTokenChildrenDrop(
            reuseToken.Temp,
            reuseToken.RuntimeCleanup,
            transferredFields);
        Emit(new IrInst.AllocReusing(
            ptrTemp,
            tag,
            ctor.Arity,
            reuseToken.Temp,
            reuseToken.RuntimeManaged,
            Tagless: IsTaglessConstructor(ctor)));
        _reuseResultTemps.Add(ptrTemp);
        RecordReuseTokenDisposition(
            reuseToken,
            ReuseDecisionOutcome.Consumed,
            ReuseDecisionReason.CompatibleTokenConsumed,
            ptrTemp,
            ctor.Name,
            location);
        if (reuseToken.RuntimeManaged)
        {
            RecordReuseFallbackAllocation(
                reuseToken,
                ptrTemp,
                ctor.Name,
                location,
                ReuseDecisionOutcome.Available,
                ReuseDecisionReason.RuntimeUniquenessFallback,
                ReuseFallbackAllocationKind.RuntimeRc);
        }
    }

    private void EmitFreshConstructorCell(
        ConstructorSymbol ctor,
        int tag,
        int ptrTemp,
        bool stackAllocate,
        bool runtimeManagedCandidate,
        ReuseTokenMatch tokenMatch,
        SourceLocation? location)
    {
        if (stackAllocate)
        {
            Emit(new IrInst.AllocAdtStack(ptrTemp, tag, ctor.Arity, IsTaglessConstructor(ctor)));
            return;
        }

        if (_inSpecialization)
        {
            // New cells in a reuse specialization belong in persistent to-space.
            Emit(new IrInst.AllocAdtToSpace(ptrTemp, tag, ctor.Arity, IsTaglessConstructor(ctor)));
            _reuseResultTemps.Add(ptrTemp);
            RecordReuseFallbackAllocation(
                null,
                ptrTemp,
                ctor.Name,
                location,
                ReuseDecisionOutcome.Allocated,
                ReuseFallbackReason(tokenMatch),
                ReuseFallbackAllocationKind.ToSpace,
                ctor.Arity,
                listCell: false,
                runtimeManaged: false);
            return;
        }

        Emit(new IrInst.AllocAdt(ptrTemp, tag, ctor.Arity, runtimeManagedCandidate, IsTaglessConstructor(ctor)));
        if (ShouldRecordReuseFallback(tokenMatch))
        {
            RecordReuseFallbackAllocation(
                null,
                ptrTemp,
                ctor.Name,
                location,
                ReuseDecisionOutcome.Allocated,
                ReuseFallbackReason(tokenMatch),
                runtimeManagedCandidate
                    ? ReuseFallbackAllocationKind.RuntimeRc
                    : ReuseFallbackAllocationKind.Arena,
                ctor.Arity,
                listCell: false,
                runtimeManagedCandidate);
        }
    }

    private HashSet<int> PrepareRuntimeReuseTransferredChildren(
        IReadOnlyList<Expr> arguments,
        List<int> argumentTemps,
        int tokenTemp,
        RuntimeReuseCleanup? runtimeCleanup)
    {
        var transferredFields = new HashSet<int>();
        if (runtimeCleanup is not { } cleanup)
        {
            return transferredFields;
        }

        for (int i = 0; i < arguments.Count; i++)
        {
            if (arguments[i] is not Expr.Var variable
                || !cleanup.TransferableFields.TryGetValue(variable.Name, out int sourceField))
            {
                continue;
            }

            argumentTemps[i] = EmitRuntimeReuseTransferredChild(
                argumentTemps[i],
                tokenTemp);
            transferredFields.Add(sourceField);
            if (LookupOwnedValue(variable.Name) is { IsDropped: false } info)
            {
                info.ReleaseKind = ResourceReleaseKind.Moved;
            }
        }

        return transferredFields;
    }

    private int EmitRuntimeReuseTransferredChild(int childTemp, int tokenTemp)
    {
        int resultSlot = NewLocal();
        int zeroTemp = NewTemp();
        Emit(new IrInst.LoadConstInt(zeroTemp, 0));
        int hasTokenTemp = NewTemp();
        Emit(new IrInst.CmpIntNe(hasTokenTemp, tokenTemp, zeroTemp));
        string duplicateLabel = NewLabel("reuse_child_duplicate");
        string continueLabel = NewLabel("reuse_child_continue");
        Emit(new IrInst.JumpIfFalse(hasTokenTemp, duplicateLabel));
        Emit(new IrInst.StoreLocal(resultSlot, childTemp));
        Emit(new IrInst.Jump(continueLabel));
        Emit(new IrInst.Label(duplicateLabel));
        int duplicatedTemp = NewTemp();
        Emit(new IrInst.RcDup(duplicatedTemp, childTemp, RuntimeManaged: true));
        Emit(new IrInst.StoreLocal(resultSlot, duplicatedTemp));
        Emit(new IrInst.Label(continueLabel));
        int resultTemp = NewTemp();
        Emit(new IrInst.LoadLocal(resultTemp, resultSlot));
        return resultTemp;
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
    private int MaterializeSpecializationField(Expr argExpr, TypeRef argType, int fieldTemp, int ptrTemp, int fieldIndex, bool reuseNode, int consumedTokenTemp, bool tagless, TypeRef resultType)
    {
        if (!_inSpecialization || _specFreshInputNames is null
            || (argExpr is Expr.Var fieldVar && !_specFreshInputNames.Contains(fieldVar.Name)))
        {
            return fieldTemp;
        }

        var pruned = Prune(argType);
        if (pruned is TypeRef.TStr or TypeRef.TBytes)
        {
            return MaterializeSpecializationStringField(fieldTemp, ptrTemp, fieldIndex, reuseNode, consumedTokenTemp, tagless);
        }

        if (pruned is TypeRef.TTuple tup && tup.Elements.All(CanArenaReset))
        {
            return MaterializeSpecializationTupleField(fieldTemp, ptrTemp, fieldIndex, reuseNode, consumedTokenTemp, tagless, tup.Elements.Count * 8);
        }

        if (pruned is TypeRef.TList list && Prune(list.Element) is TypeRef.TStr elementType)
        {
            // No in-place-reuse primitive exists for a list spine, so always rebuild fresh
            // (matching the tuple insert path above) rather than reuse an update's dead cell.
            return EmitListToSpaceCopy(fieldTemp, elementType);
        }

        // A recursive child (e.g. left/right of a Node) resolves to the accumulator's own type: it is
        // already a to-space/reuse-managed node by construction (the recursive call that produced it
        // is itself part of the specialization), so re-deep-copying it here would be both redundant
        // and — since it synthesizes a self-recursive copier closure — exactly what the loop's reset
        // safety analysis treats as an escaping closure, defeating the per-iteration arena reset this
        // whole mechanism exists to enable. Only a genuinely distinct (non-self) ADT leaf is copied.
        bool isAccumulatorSelfType = pruned is TypeRef.TNamedType selfCandidate
            && Prune(resultType) is TypeRef.TNamedType resultNamed
            && ReferenceEquals(selfCandidate.Symbol, resultNamed.Symbol);
        if (!isAccumulatorSelfType
            && pruned is TypeRef.TNamedType named
            && !BuiltinRegistry.IsResourceTypeName(named.Symbol.Name)
            && IsToSpaceCopySafeType(named))
        {
            // Same reasoning as the list case above: no in-place-reuse primitive exists for an
            // arbitrary record/ADT cell, so always rebuild fresh via the general to-space deep
            // copier rather than reuse an update's dead cell.
            return EmitDeepCopyToSpace(fieldTemp, pruned);
        }

        return fieldTemp;
    }

    private int MaterializeSpecializationStringField(int fieldTemp, int ptrTemp, int fieldIndex, bool reuseNode, int consumedTokenTemp, bool tagless)
    {
        if (reuseNode && ReuseTokenFieldIsDead(consumedTokenTemp, fieldIndex))
        {
            // Update path: reuse the dead old value blob in place when the new string fits and
            // the old blob is provably persistent (a runtime blob-region check in the backend),
            // else materialize fresh. Bounds blob growth to the largest value per cell instead of
            // leaking one blob per update. The variable-size analogue of the tuple CopyFixedInto
            // path below.
            int oldValueTemp = NewTemp();
            Emit(new IrInst.GetAdtField(oldValueTemp, ptrTemp, fieldIndex, tagless));
            int persistentField = NewTemp();
            Emit(new IrInst.CopyStringIntoOrFresh(persistentField, oldValueTemp, fieldTemp));
            return persistentField;
        }

        int freshField = NewTemp();
        Emit(new IrInst.CopyOutArenaToSpace(freshField, fieldTemp, -1));
        return freshField;
    }

    private int MaterializeSpecializationTupleField(int fieldTemp, int ptrTemp, int fieldIndex, bool reuseNode, int consumedTokenTemp, bool tagless, int sizeBytes)
    {
        if (reuseNode && ReuseTokenFieldIsDead(consumedTokenTemp, fieldIndex))
        {
            // Update path: the reused node's old value cell is dead. Overwrite its contents in
            // place when it is provably persistent (a runtime blob-region check in the backend),
            // else materialize fresh — so value storage is reused and the blob stays bounded by
            // distinct keys, without overwriting reclaimable main-arena memory in place.
            int oldValueTemp = NewTemp();
            Emit(new IrInst.GetAdtField(oldValueTemp, ptrTemp, fieldIndex, tagless));
            int persistentField = NewTemp();
            Emit(new IrInst.CopyFixedIntoOrFresh(persistentField, oldValueTemp, fieldTemp, sizeBytes));
            return persistentField;
        }

        // Insert path: no old cell to reuse — materialize a fresh blob cell (bounded by
        // the number of distinct keys).
        int freshField = NewTemp();
        Emit(new IrInst.CopyOutArenaToSpace(freshField, fieldTemp, sizeBytes));
        return freshField;
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

    /// <summary>True when the reuse token's field can no longer be referenced on the current
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
