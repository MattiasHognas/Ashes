using System.Diagnostics;
using Ashes.Frontend;

namespace Ashes.Semantics;

public sealed partial class Lowering
{
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
            var parameterTypes = function.ParameterTypes.Select(t => ResolveExternParsedType(function, t)).ToList();
            var returnType = ResolveExternParsedType(function, function.ReturnType);
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
                resolvedParameterTypes.Select(ToFfiType).ToList(),
                ToFfiType(returnType),
                string.IsNullOrWhiteSpace(libraryName) ? null : libraryName);
            _externFunctions.Add(irFunction);

            var type = BuildFunctionType(resolvedParameterTypes, returnType);
            _scopes.Peek()[function.Name] = new Binding.ExternFunction(irFunction, type);
        }
    }

    private TypeRef? ResolveExternParsedType(ExternDecl externDecl, ParsedType parsedType)
    {
        if (parsedType is not ParsedType.Named named)
        {
            ReportDiagnostic(GetSpan(externDecl), "Unsupported extern type syntax.");
            return null;
        }

        return named.Name switch
        {
            "Int" => new TypeRef.TInt(),
            "Float" => new TypeRef.TFloat(),
            "Bool" => new TypeRef.TBool(),
            "Str" => new TypeRef.TStr(),
            _ when _externOpaqueTypes.Contains(named.Name) => new TypeRef.TOpaque(named.Name),
            _ => ReportUnsupportedExternType(externDecl, named.Name)
        };
    }

    private TypeRef? ReportUnsupportedExternType(ExternDecl externDecl, string name)
    {
        ReportDiagnostic(GetSpan(externDecl), $"Type '{name}' is not supported in extern declarations.");
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

    private static FfiType ToFfiType(TypeRef type)
    {
        return type switch
        {
            TypeRef.TInt => new FfiType.Int(),
            TypeRef.TFloat => new FfiType.Float(),
            TypeRef.TBool => new FfiType.Bool(),
            TypeRef.TStr => new FfiType.Str(),
            TypeRef.TOpaque opaque => new FfiType.Opaque(opaque.Name),
            _ => throw new InvalidOperationException($"Type '{type}' is not supported in extern declarations.")
        };
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
}
