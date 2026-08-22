using Ashes.Frontend;

namespace Ashes.Semantics;

public sealed partial class Lowering
{
    private sealed record TraitDictionaryShape(
        TraitSymbol Trait,
        IReadOnlyList<TraitMethodSymbol> Methods,
        IReadOnlyList<TraitDictionaryShape> Supertraits,
        int ConstraintOrdinal,
        int SourceOrdinal,
        string ConstraintSyntaxKey,
        string Path);

    private sealed record TraitDictionaryFunctionInfo(
        string Name,
        string SourcePath,
        int SourceOffset,
        IReadOnlyList<TraitDictionaryShape> Dictionaries);

    private readonly Dictionary<string, TraitDictionaryFunctionInfo> _traitDictionaryFunctions =
        new(StringComparer.Ordinal);

    private readonly Dictionary<string, Expr.Lambda> _traitOperatorSpecializableFunctions =
        new(StringComparer.Ordinal);

    private readonly Dictionary<(string Name, int Position), TraitDictionaryFunctionInfo>
        _traitDictionaryFunctionsByBinding = [];

    private readonly Dictionary<(string Name, int Position), TraitDictionaryFunctionInfo>
        _traitDictionaryFunctionsByRecursiveBinding = [];

    private readonly Dictionary<TypeScheme, TraitDictionaryFunctionInfo> _traitDictionaryFunctionsByScheme =
        new(ReferenceEqualityComparer.Instance);

    private sealed record TraitDictionaryParameterMetadata(
        TraitDictionaryShape Shape,
        TraitConstraint? Constraint);

    private sealed record ActiveTraitDictionaryParameter(
        string ParameterName,
        TraitDictionaryParameterMetadata Metadata);

    private readonly Dictionary<string, TraitDictionaryParameterMetadata> _traitDictionaryParameterMetadata =
        new(StringComparer.Ordinal);

    private readonly HashSet<string> _borrowedTraitDictionaryBindings = new(StringComparer.Ordinal);

    private readonly HashSet<IrInst> _traitEvidenceConstructionInstructions =
        new(ReferenceEqualityComparer.Instance);

    private readonly Dictionary<string, List<ActiveTraitDictionaryParameter>> _activeTraitDictionaryParameters =
        new(StringComparer.Ordinal);
    private readonly Dictionary<Expr, Dictionary<TraitSymbol, string[]>>
        _selectedTraitMethodDependencies = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<TraitInstanceSymbol, IReadOnlyList<TraitMethodSymbol>>
        _traitMethodConstructionOrders = new(ReferenceEqualityComparer.Instance);
    private int _suppressActiveTraitDictionaryReferenceDepth;

    private sealed record ActiveTraitImplementationMethod(
        TraitConstraint Constraint,
        string Method,
        string BindingName);

    private readonly List<ActiveTraitImplementationMethod> _activeTraitImplementationMethods = [];
    private readonly Dictionary<string, TypeRef> _generatedTraitRecursiveTypes =
        new(StringComparer.Ordinal);

    private sealed record InferredTraitBindingElaboration(
        TypeExpr TypeAnnotation,
        IReadOnlyList<TraitConstraintSyntax> Requirements);

    private readonly Dictionary<(string Name, int Position), InferredTraitBindingElaboration>
        _inferredTraitBindingElaborations = [];

    // The discovery pass can fully resolve a trait-bearing binding to a concrete monotype even when
    // no residual trait constraint remains to elaborate. Preserve that type solely as an emission
    // hint: operators and evidence calls earlier in the body must see types learned later by HM
    // inference, but the binding must not become a user-written annotation or acquire a hidden
    // dictionary parameter.
    private readonly Dictionary<(string Name, int Position), TypeExpr>
        _inferredTraitBindingTypeHints = [];

    // The discovery pass lowers the unelaborated source and therefore sees the operations that
    // justify every written requires clause. Dictionary emission replaces those operations with
    // ordinary closure calls, so the emitting pass must not try to validate the same boundary from
    // the rewritten body a second time.
    private bool _sourceTraitConstraintBoundariesValidated;

    private TraitEvidenceAnnotations BuildTraitEvidenceAnnotations()
    {
        TraitDictionaryFunctionInfo[] functions = _traitDictionaryFunctions.Values
            .Concat(_traitDictionaryFunctionsByBinding.Values)
            .Concat(_traitDictionaryFunctionsByRecursiveBinding.Values)
            .DistinctBy(info => $"{info.SourcePath}:{info.SourceOffset}:{info.Name}:{string.Join(",", info.Dictionaries.Select(dictionary => dictionary.Trait.QualifiedName))}", StringComparer.Ordinal)
            .OrderBy(info => info.SourcePath, StringComparer.Ordinal)
            .ThenBy(info => info.SourceOffset)
            .ThenBy(info => info.Name, StringComparer.Ordinal)
            .ToArray();
        TraitDictionaryAbiAnnotation[] parameters = functions
            .SelectMany(info => info.Dictionaries.Select((dictionary, index) =>
                new TraitDictionaryAbiAnnotation(
                    info.Name,
                    info.SourcePath,
                    info.SourceOffset,
                    index,
                    dictionary.Trait.QualifiedName,
                    dictionary.Methods.Select(method => method.Name).ToArray(),
                    dictionary.Supertraits.Select(supertrait => supertrait.Trait.QualifiedName).ToArray())))
            .ToArray();
        TraitResolutionAnnotation[] resolutions = _concreteTraitEvidenceCache.Values
            .OfType<TraitEvidencePlan.Instance>()
            .OrderBy(plan => TraitConstraint.StableKey(plan.Goal), StringComparer.Ordinal)
            .Select(plan => new TraitResolutionAnnotation(
                FormatTraitConstraint(plan.Goal),
                plan.SelectedInstance.Provenance.ModuleName,
                plan.SelectedInstance.Provenance.SourcePath,
                AstSpans.GetOrDefault(plan.SelectedInstance.DeclaringSyntax).Start))
            .ToArray();
        return new TraitEvidenceAnnotations(parameters, resolutions);
    }

    private void RegisterTraitDictionaryFunctions(Program program)
    {
        RegisterDeclaredTraitDictionaryFunctions(program.Items);
        RegisterEntryTraitDictionaryFunctions(program.Body);
        RegisterDeepTraitDictionaryFunctions(program);
    }

    private void RegisterDeclaredTraitDictionaryFunctions(IReadOnlyList<TopLevelItem> items)
    {
        foreach (TopLevelItem item in items)
        {
            switch (item)
            {
                case TopLevelItem.LetDecl { IsRecursive: true } binding:
                    RegisterTraitOperatorSpecializableFunction(binding.Name, binding.Value);
                    RegisterTraitDictionaryFunction(
                        binding.Name,
                        binding.Requires,
                        GetSpan(binding.Value),
                        recursiveBindingKey: (
                            binding.Name,
                            AstSpans.GetOrDefault(binding.Value).Start));
                    break;
                case TopLevelItem.LetDecl binding:
                    RegisterTraitOperatorSpecializableFunction(binding.Name, binding.Value);
                    RegisterTraitDictionaryFunction(
                        binding.Name,
                        binding.Requires,
                        GetSpan(binding.Value),
                        (binding.Name, AstSpans.GetOrDefault(binding.Value).Start));
                    break;
                case TopLevelItem.RecursiveGroup group:
                    for (int index = 0; index < group.Bindings.Count; index++)
                    {
                        RegisterTraitOperatorSpecializableFunction(
                            group.Bindings[index].Name,
                            group.Bindings[index].Value);
                        RegisterTraitDictionaryFunction(
                            group.Bindings[index].Name,
                            index < group.Requires.Count ? group.Requires[index] : [],
                            GetSpan(group.Bindings[index].Value),
                            recursiveBindingKey: (
                                group.Bindings[index].Name,
                                AstSpans.GetOrDefault(group.Bindings[index].Value).Start));
                    }
                    break;
            }
        }

    }

    private void RegisterEntryTraitDictionaryFunctions(Expr body)
    {
        Expr cursor = body;
        while (cursor is Expr.Let or Expr.LetRecursive)
        {
            switch (cursor)
            {
                case Expr.Let binding:
                    RegisterTraitOperatorSpecializableFunction(binding.Name, binding.Value);
                    RegisterTraitDictionaryFunction(
                        binding.Name,
                        binding.Requires,
                        GetSpan(binding),
                        TraitBindingKey(binding));
                    cursor = binding.Body;
                    break;
                case Expr.LetRecursive binding:
                    RegisterTraitOperatorSpecializableFunction(binding.Name, binding.Value);
                    RegisterTraitDictionaryFunction(
                        binding.Name,
                        binding.Requires,
                        GetSpan(binding),
                        recursiveBindingKey: TraitBindingKey(binding));
                    cursor = binding.Body;
                    break;
            }
        }
    }

    private void RegisterTraitOperatorSpecializableFunction(string name, Expr value)
    {
        if (RegisterInlinableStrip(value) is Expr.Lambda lambda
            && ExpressionContainsMappedTraitOperator(lambda))
        {
            _traitOperatorSpecializableFunctions[name] = lambda;
        }
    }

    private void RegisterDeepTraitDictionaryFunctions(Program program)
    {
        void Visit(Expr expression)
        {
            switch (expression)
            {
                case Expr.Let binding:
                    RegisterTraitDictionaryFunction(
                        binding.Name,
                        binding.Requires,
                        GetSpan(binding),
                        TraitBindingKey(binding),
                        overwriteGlobal: false);
                    break;
                case Expr.LetRecursive binding:
                    RegisterTraitDictionaryFunction(
                        binding.Name,
                        binding.Requires,
                        GetSpan(binding),
                        recursiveBindingKey: TraitBindingKey(binding),
                        overwriteGlobal: false);
                    break;
            }
            _ = MapChildExpressions(expression, child =>
            {
                Visit(child);
                return child;
            });
        }

        foreach (TopLevelItem item in program.Items)
        {
            if (item is TopLevelItem.LetDecl binding)
            {
                Visit(binding.Value);
            }
            else if (item is TopLevelItem.RecursiveGroup group)
            {
                foreach ((_, Expr value) in group.Bindings)
                {
                    Visit(value);
                }
            }
        }
        if (program.Body is not null)
        {
            Visit(program.Body);
        }
    }

    private Program ElaborateInferredTraitBindings(Program program)
    {
        var discoveryDiagnostics = new Diagnostics();
        var discovery = new Lowering(
            discoveryDiagnostics,
            _importedStdModules,
            _moduleAliases,
            _constructorModulesByName,
            _configuration with { EnableReuse = false },
            enableInferredTraitElaboration: false,
            collectInferredTraitElaboration: true,
            enableTraitValidationPass: false,
            includeTraitValidationBindings: true,
            emitTraitDictionaries: false,
            isTraitValidationSubpass: false);
        CopySourceContextTo(discovery);
        _ = discovery.Lower(program);
        if (discoveryDiagnostics.StructuredErrors.Count > 0)
        {
            return program;
        }

        _traitValidationCompletedDuringElaboration = true;
        _sourceTraitConstraintBoundariesValidated = true;

        if (discovery._inferredTraitBindingElaborations.Count == 0
            && discovery._inferredTraitBindingTypeHints.Count == 0)
        {
            return program;
        }

        CopyInferredTraitElaborations(discovery);
        return _inferredTraitBindingElaborations.Count == 0
            ? program
            : RewriteInferredTraitBindings(program);
    }

    private void CopyInferredTraitElaborations(Lowering source)
    {
        _inferredTraitBindingElaborations.Clear();
        foreach (((string name, int position) key, InferredTraitBindingElaboration value) in
                 source._inferredTraitBindingElaborations)
        {
            _inferredTraitBindingElaborations[key] = value;
        }

        _inferredTraitBindingTypeHints.Clear();
        foreach (((string name, int position) key, TypeExpr value) in
                 source._inferredTraitBindingTypeHints)
        {
            _inferredTraitBindingTypeHints[key] = value;
        }
    }

    private Program RewriteInferredTraitBindings(Program program)
    {
        Expr Rewrite(Expr expression)
        {
            Expr rewritten = MapChildExpressions(expression, Rewrite);
            AstSpans.Set(rewritten, AstSpans.GetOrDefault(expression));
            if (rewritten is Expr.Let binding
                && _inferredTraitBindingElaborations.TryGetValue(
                    TraitBindingKey(binding),
                    out InferredTraitBindingElaboration? letElaboration))
            {
                return CopyLetSpans(
                    binding,
                    new Expr.Let(binding.Name, binding.Value, binding.Body)
                    {
                        TypeAnnotation = letElaboration.TypeAnnotation,
                        Requires = letElaboration.Requirements,
                        SugarParams = binding.SugarParams,
                    });
            }
            if (rewritten is Expr.LetRecursive recursive
                && _inferredTraitBindingElaborations.TryGetValue(
                    TraitBindingKey(recursive),
                    out InferredTraitBindingElaboration? recursiveElaboration))
            {
                return CopyLetRecursiveSpans(
                    recursive,
                    new Expr.LetRecursive(recursive.Name, recursive.Value, recursive.Body)
                    {
                        TypeAnnotation = recursiveElaboration.TypeAnnotation,
                        Requires = recursiveElaboration.Requirements,
                        SugarParams = recursive.SugarParams,
                    });
            }
            return rewritten;
        }

        IReadOnlyList<TopLevelItem> items = program.Items
            .Select(item => RewriteInferredTraitBindingItem(item, Rewrite))
            .ToArray();
        // Declaration-only modules deliberately have no trailing expression. Program.Body keeps a
        // non-null facade for historical consumers, but its backing value is null in that case, so
        // the trait elaboration rewrite must preserve the absence instead of descending into it.
        Expr? body = program.Body;
        return program with { Items = items, Body = body is null ? null! : Rewrite(body) };
    }

    private TopLevelItem RewriteInferredTraitBindingItem(
        TopLevelItem item,
        Func<Expr, Expr> rewrite)
    {
        return item switch
        {
            TopLevelItem.LetDecl binding => RewriteInferredTraitLet(binding, rewrite),
            TopLevelItem.RecursiveGroup group => RewriteInferredTraitRecursiveGroup(group, rewrite),
            TopLevelItem.Provide provider => RewriteInferredTraitProvider(provider, rewrite),
            TopLevelItem.Trait trait => RewriteInferredTraitDefaults(trait, rewrite),
            TopLevelItem.Implementation implementation => RewriteInferredTraitImplementation(implementation, rewrite),
            _ => item,
        };
    }

    private TopLevelItem.LetDecl RewriteInferredTraitLet(
        TopLevelItem.LetDecl binding,
        Func<Expr, Expr> rewrite)
    {
        TopLevelItem.LetDecl rewritten = binding with { Value = rewrite(binding.Value) };
        if (_inferredTraitBindingElaborations.TryGetValue(
                (binding.Name, AstSpans.GetOrDefault(binding.Value).Start),
                out InferredTraitBindingElaboration? elaboration))
        {
            rewritten = rewritten with
            {
                TypeAnnotation = elaboration.TypeAnnotation,
                Requires = elaboration.Requirements,
            };
        }
        AstSpans.Set(rewritten, AstSpans.GetOrDefault(binding));
        return rewritten;
    }

    private TopLevelItem.RecursiveGroup RewriteInferredTraitRecursiveGroup(
        TopLevelItem.RecursiveGroup group,
        Func<Expr, Expr> rewrite)
    {
        TypeExpr?[] annotations = Enumerable.Range(0, group.Bindings.Count)
            .Select(index => index < group.TypeAnnotations.Count
                ? group.TypeAnnotations[index]
                : null)
            .ToArray();
        IReadOnlyList<TraitConstraintSyntax>[] requirements = Enumerable.Range(0, group.Bindings.Count)
            .Select(index => index < group.Requires.Count
                ? group.Requires[index]
                : (IReadOnlyList<TraitConstraintSyntax>)[])
            .ToArray();
        for (int index = 0; index < group.Bindings.Count; index++)
        {
            (string name, Expr value) = group.Bindings[index];
            if (_inferredTraitBindingElaborations.TryGetValue(
                    (name, AstSpans.GetOrDefault(value).Start),
                    out InferredTraitBindingElaboration? elaboration))
            {
                annotations[index] = elaboration.TypeAnnotation;
                requirements[index] = elaboration.Requirements;
            }
        }
        TopLevelItem.RecursiveGroup rewritten = group with
        {
            Bindings = group.Bindings.Select(binding =>
                (binding.Name, Value: rewrite(binding.Value))).ToArray(),
            TypeAnnotations = annotations,
            Requires = requirements,
        };
        AstSpans.Set(rewritten, AstSpans.GetOrDefault(group));
        AstSpans.SetRecursiveGroupBindingNames(
            rewritten,
            AstSpans.GetRecursiveGroupBindingNamesOrDefault(group));
        return rewritten;
    }

    private static TopLevelItem.Provide RewriteInferredTraitProvider(
        TopLevelItem.Provide provider,
        Func<Expr, Expr> rewrite)
    {
        ProvideDecl declaration = provider.Decl with
        {
            Bindings = provider.Decl.Bindings.Select(binding =>
                binding with { Implementation = rewrite(binding.Implementation) }).ToArray(),
        };
        AstSpans.Set(declaration, AstSpans.GetOrDefault(provider.Decl));
        return provider with { Decl = declaration };
    }

    private static TopLevelItem.Trait RewriteInferredTraitDefaults(
        TopLevelItem.Trait trait,
        Func<Expr, Expr> rewrite)
    {
        TraitMethodDecl RewriteMethod(TraitMethodDecl method)
        {
            if (method.DefaultImplementation is null) return method;
            TraitMethodDecl rewritten = method with
            {
                DefaultImplementation = rewrite(method.DefaultImplementation),
            };
            AstSpans.Set(rewritten, AstSpans.GetOrDefault(method));
            return rewritten;
        }

        TraitDecl declaration = trait.Decl with
        {
            Methods = trait.Decl.Methods.Select(RewriteMethod).ToArray(),
        };
        AstSpans.Set(declaration, AstSpans.GetOrDefault(trait.Decl));
        return trait with { Decl = declaration };
    }

    private static TopLevelItem.Implementation RewriteInferredTraitImplementation(
        TopLevelItem.Implementation implementation,
        Func<Expr, Expr> rewrite)
    {
        TraitImplementationMethodBinding RewriteBinding(TraitImplementationMethodBinding binding)
        {
            TraitImplementationMethodBinding rewritten = binding with
            {
                Implementation = rewrite(binding.Implementation),
            };
            AstSpans.Set(rewritten, AstSpans.GetOrDefault(binding));
            return rewritten;
        }

        TraitImplementationDecl declaration = implementation.Decl with
        {
            Bindings = implementation.Decl.Bindings.Select(RewriteBinding).ToArray(),
        };
        AstSpans.Set(declaration, AstSpans.GetOrDefault(implementation.Decl));
        return implementation with { Decl = declaration };
    }

    private void RecordInferredTraitBindingElaboration(
        Expr.Let binding,
        TypeScheme scheme,
        IReadOnlyList<TraitConstraint> writtenRequirements,
        IReadOnlyList<TraitConstraint> encounteredRequirements,
        bool needsLateTypeHint = false)
    {
        if (_collectInferredTraitElaboration
            && !_topLevelBindingNames.Contains(binding.Name)
            && binding.Value is Expr.Var)
        {
            return;
        }
        if (_collectInferredTraitElaboration
            && IsSyntheticModuleValueBinding(binding))
        {
            return;
        }
        RecordInferredTraitBindingElaboration(
            TraitBindingKey(binding),
            scheme,
            writtenRequirements,
            encounteredRequirements,
            needsLateTypeHint);
    }

    private static bool IsSyntheticModuleValueBinding(Expr.Let binding) =>
        binding.Value is Expr.Let
        {
            Name: var aliasName,
            Value: Expr.Var { Name: var exportedName },
            Body: Expr.Var { Name: var returnedName },
        }
        && string.Equals(aliasName, returnedName, StringComparison.Ordinal)
        && exportedName.StartsWith($"{binding.Name}_", StringComparison.Ordinal);

    private void RecordInferredTraitBindingElaboration(
        Expr.LetRecursive binding,
        TypeScheme scheme,
        IReadOnlyList<TraitConstraint> writtenRequirements,
        IReadOnlyList<TraitConstraint> encounteredRequirements,
        bool needsLateTypeHint = false)
        => RecordInferredTraitBindingElaboration(
            TraitBindingKey(binding),
            scheme,
            writtenRequirements,
            encounteredRequirements,
            needsLateTypeHint);

    private void RecordInferredTraitBindingElaboration(
        (string Name, int Position) key,
        TypeScheme scheme,
        IReadOnlyList<TraitConstraint> writtenRequirements,
        IReadOnlyList<TraitConstraint> encounteredRequirements,
        bool needsLateTypeHint = false)
    {
        bool closedMonomorphicHint = encounteredRequirements.Count > 0
            && !ValueTypeRemainsAbstract(scheme.Body)
            && TraitTypeOperations.FreeVariables(scheme).Count == 0;
        if (!_collectInferredTraitElaboration
            || writtenRequirements.Count > 0
            || scheme.Constraints.Count == 0 && !needsLateTypeHint && !closedMonomorphicHint)
        {
            return;
        }

        Dictionary<int, string> variableNames = scheme.Quantified
            .Select((variable, index) => (variable.Id, Name: $"inferredTraitT{index}"))
            .ToDictionary(item => item.Id, item => item.Name);
        TypeExpr annotation = ConvertInferredTypeToSyntax(scheme.Body, variableNames);
        if (scheme.Constraints.Count == 0)
        {
            _inferredTraitBindingTypeHints[key] = annotation;
            return;
        }
        TraitConstraintSyntax[] requirements = scheme.Constraints
            .Select(constraint => new TraitConstraintSyntax(
                constraint.Trait.QualifiedName,
                constraint.TypeArgs.Select(argument =>
                    ConvertInferredTypeToSyntax(argument, variableNames)).ToArray()))
            .ToArray();
        _inferredTraitBindingElaborations[key] =
            new InferredTraitBindingElaboration(annotation, requirements);
    }

    private TypeRef? ResolveInferredTraitBindingTypeHint(string name, Expr value)
    {
        bool found = _inferredTraitBindingTypeHints.TryGetValue(
            (name, AstSpans.GetOrDefault(value).Start),
            out TypeExpr? annotation);
        TypeRef? resolved = found
            ? OpenInferredTraitHintFunctionRows(
                ResolveBindingSignature(annotation!, [], GetSpan(value)).Type!)
            : null;
        return resolved;
    }

    private TypeRef OpenInferredTraitHintFunctionRows(TypeRef type) =>
        type switch
        {
            TypeRef.TFun function => new TypeRef.TFun(
                OpenInferredTraitHintFunctionRows(function.Arg),
                OpenInferredTraitHintFunctionRows(function.Ret))
            {
                Row = NewTypeVar(),
            },
            TypeRef.TList list => new TypeRef.TList(
                OpenInferredTraitHintFunctionRows(list.Element)),
            TypeRef.TTuple tuple => new TypeRef.TTuple(
                tuple.Elements.Select(OpenInferredTraitHintFunctionRows).ToArray()),
            TypeRef.TNamedType named => new TypeRef.TNamedType(
                named.Symbol,
                named.TypeArgs.Select(OpenInferredTraitHintFunctionRows).ToArray()),
            TypeRef.TPtr pointer => new TypeRef.TPtr(
                OpenInferredTraitHintFunctionRows(pointer.Pointee)),
            _ => type,
        };

    private static (string Name, int Position) TraitBindingKey(Expr.Let binding) =>
        (binding.Name, AstSpans.GetOrDefault(binding.Value).Start);

    private static (string Name, int Position) TraitBindingKey(Expr.LetRecursive binding) =>
        (binding.Name, AstSpans.GetOrDefault(binding.Value).Start);

    private bool IsInferredTraitBinding(Expr.LetRecursive binding) =>
        _inferredTraitBindingElaborations.ContainsKey(TraitBindingKey(binding));

    private bool IsInferredTraitBinding(string name, Expr value) =>
        _inferredTraitBindingElaborations.ContainsKey(
            (name, AstSpans.GetOrDefault(value).Start));

    private (bool Uses, bool Inferred, TraitDictionaryFunctionInfo? Info) GetLetTraitEvidence(
        Expr.Let binding)
    {
        bool inferred = _inferredTraitBindingElaborations.ContainsKey(TraitBindingKey(binding));
        bool registered = _traitDictionaryFunctionsByBinding.TryGetValue(
            TraitBindingKey(binding),
            out TraitDictionaryFunctionInfo? info);
        return (registered, inferred, info);
    }

    private void RegisterTraitDictionaryScheme(
        TypeScheme scheme,
        TraitDictionaryFunctionInfo? info)
    {
        if (info is not null)
        {
            _traitDictionaryFunctionsByScheme[scheme] = info;
        }
    }

    private void CopyTraitDictionarySchemeRegistration(TypeScheme source, TypeScheme target)
    {
        if (_traitDictionaryFunctionsByScheme.TryGetValue(source, out TraitDictionaryFunctionInfo? info))
        {
            _traitDictionaryFunctionsByScheme[target] = info;
        }
    }

    private void RegisterTraitDictionarySchemeForLet(
        Expr.Let binding,
        TypeScheme scheme)
    {
        (bool uses, _, TraitDictionaryFunctionInfo? info) = GetLetTraitEvidence(binding);
        if (uses)
        {
            RegisterTraitDictionaryScheme(scheme, info);
            return;
        }

        // Project stitching introduces ordinary aliases for exported functions. The alias stores
        // the same raw closure, including its hidden evidence parameters, so its freshly generalized
        // scheme must retain the original dictionary ABI. Do not copy the ABI inside an active
        // dictionary scope: there LowerVar has already applied the ambient evidence and the stored
        // value is an ordinary source-level function.
        if (_activeTraitDictionaryParameters.Count == 0
            && ResolveSpecializableCalleeName(binding.Value) is { } aliasTarget
            && TryGetTraitDictionaryInfo(
                aliasTarget,
                Lookup(aliasTarget),
                out TraitDictionaryFunctionInfo? aliasInfo))
        {
            RegisterTraitDictionaryScheme(scheme, aliasInfo);
        }
    }

    private bool TryGetTraitDictionaryInfo(
        string name,
        Binding? binding,
        out TraitDictionaryFunctionInfo? info)
    {
        TypeScheme? scheme = binding switch
        {
            Binding.Scheme local => local.S,
            Binding.EnvScheme environment => environment.S,
            _ => null,
        };
        if (scheme is not null)
        {
            return _traitDictionaryFunctionsByScheme.TryGetValue(scheme, out info);
        }
        if (binding is Binding.Self && _topLevelBindingNames.Contains(name) || binding is null)
        {
            return _traitDictionaryFunctions.TryGetValue(name, out info);
        }
        info = null;
        return false;
    }

    private TypeExpr ConvertInferredTypeToSyntax(
        TypeRef type,
        IDictionary<int, string> variableNames)
    {
        type = PruneConstraintType(type);
        return type switch
        {
            TypeRef.TInt => new TypeExpr.Named("Int"),
            TypeRef.TUInt unsigned => new TypeExpr.Named($"u{unsigned.Bits}"),
            TypeRef.TFloat => new TypeExpr.Named("Float"),
            TypeRef.TBigInt => new TypeExpr.Named("BigInt"),
            TypeRef.TStr => new TypeExpr.Named("Str"),
            TypeRef.TRune => new TypeExpr.Named("Rune"),
            TypeRef.TBytes => new TypeExpr.Named("Bytes"),
            TypeRef.TBool => new TypeExpr.Named("Bool"),
            TypeRef.TNever => new TypeExpr.Named("Never"),
            TypeRef.TList list => new TypeExpr.Applied(
                "List",
                [ConvertInferredTypeToSyntax(list.Element, variableNames)]),
            TypeRef.TTuple { Elements.Count: 0 } => new TypeExpr.UnitType(),
            TypeRef.TTuple tuple => new TypeExpr.TupleType(tuple.Elements.Select(element =>
                ConvertInferredTypeToSyntax(element, variableNames)).ToArray()),
            TypeRef.TFun function => ConvertInferredFunctionTypeToSyntax(function, variableNames),
            TypeRef.TVar variable => new TypeExpr.Named(GetInferredTypeVariableName(variable.Id, variableNames)),
            TypeRef.TNamedType named when named.TypeArgs.Count == 0 => new TypeExpr.Named(named.Symbol.Name),
            TypeRef.TNamedType named => new TypeExpr.Applied(
                named.Symbol.Name,
                named.TypeArgs.Select(argument => ConvertInferredTypeToSyntax(argument, variableNames)).ToArray()),
            TypeRef.TTypeParam parameter => new TypeExpr.Named(parameter.Symbol.Name),
            TypeRef.TOpaque opaque => new TypeExpr.Named(opaque.Name),
            TypeRef.TPtr pointer => new TypeExpr.Applied(
                "Ptr",
                [ConvertInferredTypeToSyntax(pointer.Pointee, variableNames)]),
            _ => throw new InvalidOperationException(
                $"Cannot elaborate inferred trait evidence for type '{Pretty(type)}'."),
        };
    }

    private TypeExpr ConvertInferredFunctionTypeToSyntax(
        TypeRef.TFun function,
        IDictionary<int, string> variableNames)
    {
        var arrow = new TypeExpr.Arrow(
            ConvertInferredTypeToSyntax(function.Arg, variableNames),
            ConvertInferredTypeToSyntax(function.Ret, variableNames));
        TypeRef? row = function.Row is null ? null : PruneConstraintType(function.Row);
        if (row is null)
        {
            return arrow;
        }

        if (row is TypeRef.TVar tail)
        {
            return arrow with
            {
                Needs = new NeedsRowSyntax([], GetInferredTypeVariableName(tail.Id, variableNames)),
            };
        }

        if (row is not TypeRef.TRow capabilityRow)
        {
            throw new InvalidOperationException("A function capability row must be a row or row variable.");
        }

        CapabilityRefSyntax[] capabilities = capabilityRow.Capabilities
            .OrderBy(capability => capability.Symbol.Name, StringComparer.Ordinal)
            .Select(capability => new CapabilityRefSyntax(
                capability.Symbol.Name,
                capability.Args.Select(argument => ConvertInferredTypeToSyntax(argument, variableNames)).ToArray()))
            .ToArray();
        string? tailName = capabilityRow.Tail is TypeRef.TVar rowTail
            ? GetInferredTypeVariableName(rowTail.Id, variableNames)
            : null;
        return arrow with { Needs = new NeedsRowSyntax(capabilities, tailName) };
    }

    private static string GetInferredTypeVariableName(
        int id,
        IDictionary<int, string> variableNames)
    {
        if (!variableNames.TryGetValue(id, out string? name))
        {
            name = $"inferredTraitT{variableNames.Count}";
            variableNames[id] = name;
        }
        return name;
    }

    private void RegisterTraitDictionaryFunction(
        string name,
        IReadOnlyList<TraitConstraintSyntax> requirements,
        TextSpan span,
        (string Name, int Position)? bindingKey = null,
        (string Name, int Position)? recursiveBindingKey = null,
        bool overwriteGlobal = true)
    {
        if (requirements.Count == 0)
        {
            return;
        }

        TraitDictionaryShape[] shapes = requirements
            .Select((requirement, ordinal) => CreateTraitDictionaryShape(requirement, ordinal, span))
            .Where(shape => shape is not null)
            .Cast<TraitDictionaryShape>()
            .OrderBy(shape => shape.Trait.QualifiedName, StringComparer.Ordinal)
            .ThenBy(shape => shape.ConstraintSyntaxKey, StringComparer.Ordinal)
            .Select((shape, ordinal) => ReordinalTraitDictionaryShape(shape, ordinal))
            .ToArray();
        if (shapes.Length > 0)
        {
            TraitDeclarationProvenance provenance = ResolveDeclarationProvenance(span);
            var info = new TraitDictionaryFunctionInfo(
                name,
                provenance.SourcePath,
                span.Start,
                shapes);
            if (overwriteGlobal)
            {
                _traitDictionaryFunctions[name] = info;
            }
            if (bindingKey is not null)
            {
                _traitDictionaryFunctionsByBinding[bindingKey.Value] = info;
            }
            if (recursiveBindingKey is not null)
            {
                _traitDictionaryFunctionsByRecursiveBinding[recursiveBindingKey.Value] = info;
            }
        }
    }

    private TraitDictionaryShape? CreateTraitDictionaryShape(
        TraitConstraintSyntax requirement,
        int ordinal,
        TextSpan span)
    {
        TraitSymbol? trait = LookupTrait(requirement.TraitName, span);
        return trait is null
            ? null
            : CreateTraitDictionaryShape(
                trait,
                ordinal,
                TraitConstraintSyntaxStableKey(requirement),
                "root",
                new HashSet<string>(StringComparer.Ordinal));
    }

    private static TraitDictionaryShape CreateTraitDictionaryShape(
        TraitSymbol trait,
        int ordinal,
        string constraintSyntaxKey,
        string path,
        HashSet<string> includedTraits)
    {
        includedTraits.Add(trait.QualifiedName);
        TraitDictionaryShape[] supertraits = trait.Supertraits
            .Select(constraint => constraint.Trait)
            .Where(supertrait => !includedTraits.Contains(supertrait.QualifiedName))
            .OrderBy(supertrait => supertrait.QualifiedName, StringComparer.Ordinal)
            .Select((supertrait, index) => CreateTraitDictionaryShape(
                supertrait,
                ordinal,
                constraintSyntaxKey,
                $"{path}_{index}",
                includedTraits))
            .ToArray();
        return new TraitDictionaryShape(
            trait,
            trait.Methods.Values.OrderBy(method => method.Name, StringComparer.Ordinal).ToArray(),
            supertraits,
            ordinal,
            ordinal,
            constraintSyntaxKey,
            path);
    }

    private static TraitDictionaryShape ReordinalTraitDictionaryShape(
        TraitDictionaryShape shape,
        int ordinal) =>
        shape with
        {
            ConstraintOrdinal = ordinal,
            Supertraits = shape.Supertraits.Select(supertrait =>
                ReordinalTraitDictionaryShape(supertrait, ordinal)).ToArray(),
        };

    private static string TraitConstraintSyntaxStableKey(TraitConstraintSyntax constraint) =>
        $"{constraint.TraitName}({string.Join(",", constraint.TypeArgs.Select(TraitTypeSyntaxStableKey))})";

    private static string TraitTypeSyntaxStableKey(TypeExpr type) => type switch
    {
        TypeExpr.Named named => named.Name,
        TypeExpr.Applied applied =>
            $"{applied.Name}({string.Join(",", applied.Args.Select(TraitTypeSyntaxStableKey))})",
        TypeExpr.Arrow arrow =>
            $"({TraitTypeSyntaxStableKey(arrow.From)}->{TraitTypeSyntaxStableKey(arrow.To)})",
        TypeExpr.TupleType tuple =>
            $"({string.Join(",", tuple.Elements.Select(TraitTypeSyntaxStableKey))})",
        TypeExpr.UnitType => "()",
        _ => type.GetType().Name,
    };

    private Expr TransformTraitDictionaryValue(
        Expr value,
        TraitDictionaryFunctionInfo info,
        bool threadRecursiveSelf = false,
        bool threadDictionaryFunctions = false,
        bool rewriteMethodReferences = true)
    {
        Dictionary<(string Trait, string Method), string> methodParameters = [];
        HashSet<(string Trait, string Method)> ambiguousMethodParameters = [];
        foreach (TraitDictionaryShape dictionary in info.Dictionaries)
        {
            CollectTraitMethodParameterNames(
                dictionary,
                methodParameters,
                ambiguousMethodParameters);
        }

        // Operators are resolved while lowering, when their inferred operand type is available.
        // Rewriting every operator of a supplied trait here would incorrectly route concrete
        // operations through a generic dictionary (for example Int exponent arithmetic inside a
        // Multiply(a)-constrained power function).
        Expr body = rewriteMethodReferences
            ? RewriteTraitMethodReferences(value, methodParameters)
            : value;
        if (threadRecursiveSelf)
        {
            body = RewriteRecursiveTraitSelfReferences(body, info);
        }
        if (threadDictionaryFunctions)
        {
            body = RewriteTraitDictionaryFunctionReferences(body, info);
        }
        string[] parameterNames = info.Dictionaries
            .Select((_, index) => StaticEvidenceParameterName("trait", index))
            .ToArray();
        for (int index = 0; index < parameterNames.Length; index++)
        {
            RegisterTraitDictionaryParameterMetadata(parameterNames[index], info.Dictionaries[index]);
        }
        return PrependStaticEvidenceParameters(
            body,
            parameterNames,
            (index, parameterName, inner) => DestructureTraitDictionary(
                info.Dictionaries[index],
                new Expr.Var(parameterName),
                inner));
    }

    private void RegisterTraitDictionaryParameterMetadata(
        string parameterName,
        TraitDictionaryShape shape)
    {
        _traitDictionaryParameterMetadata[parameterName] = new TraitDictionaryParameterMetadata(
            shape,
            Constraint: null);
        foreach (TraitMethodSymbol method in shape.Methods)
        {
            _borrowedTraitDictionaryBindings.Add(TraitRawMethodParameterName(shape, method.Name));
            _borrowedTraitDictionaryBindings.Add(TraitMethodParameterName(shape, method.Name));
        }
        for (int index = 0; index < shape.Supertraits.Count; index++)
        {
            _borrowedTraitDictionaryBindings.Add(TraitSuperDictionaryParameterName(shape, index));
            RegisterTraitDictionaryParameterMetadata(
                TraitSuperDictionaryParameterName(shape, index),
                shape.Supertraits[index]);
        }
    }

    private void BindTraitDictionaryParameterConstraints(
        TraitDictionaryFunctionInfo info,
        IReadOnlyList<TraitConstraint> sourceOrderedRequirements)
    {
        for (int index = 0; index < info.Dictionaries.Count; index++)
        {
            TraitDictionaryShape shape = info.Dictionaries[index];
            TraitConstraint? constraint = shape.SourceOrdinal < sourceOrderedRequirements.Count
                ? sourceOrderedRequirements[shape.SourceOrdinal]
                : null;
            BindTraitDictionaryParameterConstraint(
                StaticEvidenceParameterName("trait", index),
                shape,
                constraint);
        }
    }

    private void BindTraitDictionaryParameterConstraintsInAbiOrder(
        TraitDictionaryFunctionInfo info,
        IReadOnlyList<TraitConstraint> requirements)
    {
        for (int index = 0; index < info.Dictionaries.Count; index++)
        {
            BindTraitDictionaryParameterConstraint(
                StaticEvidenceParameterName("trait", index),
                info.Dictionaries[index],
                index < requirements.Count ? requirements[index] : null);
        }
    }

    private void BindTraitDictionaryParameterConstraint(
        string parameterName,
        TraitDictionaryShape shape,
        TraitConstraint? constraint)
    {
        _traitDictionaryParameterMetadata[parameterName] = new TraitDictionaryParameterMetadata(
            shape,
            constraint);
        IReadOnlyDictionary<string, TypeRef>? substitution = constraint is null
            ? null
            : shape.Trait.TypeParameters
                .Select((parameter, index) => (parameter.Name, Type: constraint.TypeArgs[index]))
                .ToDictionary(item => item.Name, item => item.Type, StringComparer.Ordinal);
        for (int index = 0; index < shape.Supertraits.Count; index++)
        {
            TraitDictionaryShape superShape = shape.Supertraits[index];
            TraitConstraint? superConstraint = substitution is null
                ? null
                : shape.Trait.Supertraits
                    .Where(candidate => string.Equals(
                        candidate.Trait.QualifiedName,
                        superShape.Trait.QualifiedName,
                        StringComparison.Ordinal))
                    .Select(candidate => new TraitConstraint(
                        candidate.Trait,
                        candidate.TypeArgs.Select(type =>
                            SubstituteTraitParameters(type, substitution)).ToArray()))
                    .FirstOrDefault();
            BindTraitDictionaryParameterConstraint(
                TraitSuperDictionaryParameterName(shape, index),
                superShape,
                superConstraint);
        }
    }

    private Expr RewriteRecursiveTraitSelfReferences(Expr expression, TraitDictionaryFunctionInfo info)
    {
        Expr Rewrite(Expr current)
        {
            if (current is Expr.Var reference
                && string.Equals(reference.Name, info.Name, StringComparison.Ordinal))
            {
                Expr applied = reference;
                for (int index = 0; index < info.Dictionaries.Count; index++)
                {
                    Expr.Var evidence = new(StaticEvidenceParameterName("trait", index));
                    AstSpans.Set(evidence, AstSpans.GetOrDefault(reference));
                    applied = new Expr.Call(
                        applied,
                        evidence);
                    AstSpans.Set(applied, AstSpans.GetOrDefault(reference));
                }
                return applied;
            }
            if (current is Expr.Lambda lambda
                && string.Equals(lambda.ParamName, info.Name, StringComparison.Ordinal))
            {
                return current;
            }
            return MapChildExpressions(current, Rewrite);
        }
        return Rewrite(expression);
    }

    private static string? GetMappedOperatorTraitName(Expr expression) => expression switch
    {
        Expr.Add => "Add",
        Expr.Subtract { Left: Expr.IntLit { Value: 0 } } => "Negate",
        Expr.Subtract => "Subtract",
        Expr.Multiply => "Multiply",
        Expr.Divide => "Divide",
        Expr.Modulo => "Remainder",
        Expr.LogicalNot => "Not",
        Expr.BitwiseAnd => "BitAnd",
        Expr.BitwiseOr => "BitOr",
        Expr.BitwiseXor => "BitXor",
        Expr.ShiftLeft => "ShiftLeft",
        Expr.ShiftRight => "ShiftRight",
        Expr.BitwiseNot => "BitwiseNot",
        Expr.Equal or Expr.NotEqual => "Eq",
        Expr.LessThan or Expr.LessOrEqual or Expr.GreaterThan or Expr.GreaterOrEqual => "Ord",
        _ => null,
    };

    private static void CollectTraitMethodParameterNames(
        TraitDictionaryShape shape,
        IDictionary<(string Trait, string Method), string> names,
        ISet<(string Trait, string Method)> ambiguous)
    {
        foreach (TraitMethodSymbol method in shape.Methods)
        {
            (string QualifiedName, string Name) key = (shape.Trait.QualifiedName, method.Name);
            if (!ambiguous.Contains(key)
                && !names.TryAdd(key, TraitMethodParameterName(shape, method.Name)))
            {
                names.Remove(key);
                ambiguous.Add(key);
            }
        }
        foreach (TraitDictionaryShape supertrait in shape.Supertraits)
        {
            CollectTraitMethodParameterNames(supertrait, names, ambiguous);
        }
    }

    private static string TraitMethodParameterName(TraitDictionaryShape shape, string method) =>
        $"__trait_{shape.ConstraintOrdinal}_{shape.Path}_{shape.Trait.Name}_{method}";

    private static string TraitRawMethodParameterName(TraitDictionaryShape shape, string method) =>
        TraitMethodParameterName(shape, method) + "_raw";

    private static string TraitSuperDictionaryParameterName(TraitDictionaryShape shape, int ordinal) =>
        $"__trait_{shape.ConstraintOrdinal}_{shape.Path}_super_{ordinal}";

    private Expr RewriteTraitMethodReferences(
        Expr expression,
        IReadOnlyDictionary<(string Trait, string Method), string> methodParameters)
    {
        Expr Rewrite(Expr current)
        {
            if (current is Expr.QualifiedVar reference
                && TryGetTraitMethod(reference, out TraitSymbol trait, out TraitMethodSymbol method)
                && methodParameters.TryGetValue((trait.QualifiedName, method.Name), out string? parameterName))
            {
                Expr replacement = new Expr.Var(parameterName);
                AstSpans.Set(replacement, AstSpans.GetOrDefault(reference));
                return replacement;
            }
            return MapChildExpressions(current, Rewrite);
        }
        return Rewrite(expression);
    }

    private Expr RewriteTraitDictionaryFunctionReferences(
        Expr expression,
        TraitDictionaryFunctionInfo enclosing)
    {
        Expr Rewrite(Expr current)
        {
            if (current is Expr.Var or Expr.QualifiedVar
                && ResolveSpecializableCalleeName(current) is { } calleeName
                && _traitDictionaryFunctions.TryGetValue(calleeName, out TraitDictionaryFunctionInfo? callee)
                && TryMapTraitDictionaries(callee, enclosing, out int[]? dictionaryIndexes))
            {
                Expr applied = current;
                foreach (int dictionaryIndex in dictionaryIndexes)
                {
                    Expr.Var evidence = new(StaticEvidenceParameterName("trait", dictionaryIndex));
                    AstSpans.Set(evidence, AstSpans.GetOrDefault(current));
                    applied = new Expr.Call(
                        applied,
                        evidence);
                    AstSpans.Set(applied, AstSpans.GetOrDefault(current));
                }
                return applied;
            }
            if (current is Expr.Lambda lambda
                && _traitDictionaryFunctions.ContainsKey(lambda.ParamName))
            {
                return current;
            }
            return MapChildExpressions(current, Rewrite);
        }
        return Rewrite(expression);
    }

    private static bool TryMapTraitDictionaries(
        TraitDictionaryFunctionInfo callee,
        TraitDictionaryFunctionInfo enclosing,
        out int[] indexes)
    {
        List<int> mapped = [];
        foreach (TraitDictionaryShape needed in callee.Dictionaries)
        {
            int[] candidates = enclosing.Dictionaries
                .Select((candidate, ordinal) => (candidate, ordinal))
                .Where(item => string.Equals(
                    item.candidate.Trait.QualifiedName,
                    needed.Trait.QualifiedName,
                    StringComparison.Ordinal))
                .Select(item => item.ordinal)
                .ToArray();
            // A syntax-only pre-pass cannot safely map two evidence values for the same trait: the
            // callee's type variables may have different names from the caller's. Leave that call
            // untouched so lowering can match instantiated semantic constraints after its real
            // arguments have unified them.
            if (candidates.Length != 1)
            {
                indexes = [];
                return false;
            }
            mapped.Add(candidates[0]);
        }
        indexes = mapped.ToArray();
        return true;
    }

    private Expr DestructureTraitDictionary(
        TraitDictionaryShape shape,
        Expr dictionary,
        Expr body)
    {
        for (int index = shape.Supertraits.Count - 1; index >= 0; index--)
        {
            body = DestructureTraitDictionary(
                shape.Supertraits[index],
                new Expr.Var(TraitSuperDictionaryParameterName(shape, index)),
                body);
        }

        foreach (TraitMethodSymbol method in shape.Methods.Reverse())
        {
            TraitMethodDecl declaration = shape.Trait.DeclaringSyntax.Methods.Single(candidate =>
                string.Equals(candidate.Name, method.Name, StringComparison.Ordinal));
            body = new Expr.Let(
                TraitMethodParameterName(shape, method.Name),
                new Expr.Var(TraitRawMethodParameterName(shape, method.Name)),
                body)
            {
                TypeAnnotation = declaration.Signature,
            };
        }

        List<Pattern> fields = shape.Methods
            .Select(method => (Pattern)new Pattern.Var(TraitRawMethodParameterName(shape, method.Name)))
            .ToList();
        fields.AddRange(shape.Supertraits.Select((_, index) =>
            (Pattern)new Pattern.Var(TraitSuperDictionaryParameterName(shape, index))));
        Pattern pattern = fields.Count == 1 ? fields[0] : new Pattern.Tuple(fields);
        return new Expr.Match(dictionary, [new MatchCase(pattern, body)], GetSpan(dictionary).Start);
    }

    private IReadOnlyList<string>? EnterTraitDictionaryParameterScope(string parameterName)
    {
        if (!_traitDictionaryParameterMetadata.TryGetValue(
                parameterName,
                out TraitDictionaryParameterMetadata? metadata))
        {
            return null;
        }
        List<string> enteredTraits = [];
        void Enter(TraitDictionaryParameterMetadata current, string currentParameter)
        {
            if (!_activeTraitDictionaryParameters.TryGetValue(
                    current.Shape.Trait.QualifiedName,
                    out List<ActiveTraitDictionaryParameter>? parameters))
            {
                parameters = [];
                _activeTraitDictionaryParameters[current.Shape.Trait.QualifiedName] = parameters;
            }
            parameters.Add(new ActiveTraitDictionaryParameter(currentParameter, current));
            enteredTraits.Add(current.Shape.Trait.QualifiedName);
            for (int index = 0; index < current.Shape.Supertraits.Count; index++)
            {
                string superParameter = TraitSuperDictionaryParameterName(current.Shape, index);
                TraitDictionaryParameterMetadata superMetadata =
                    _traitDictionaryParameterMetadata[superParameter];
                Enter(
                    superMetadata,
                    superParameter);
            }
        }
        Enter(metadata, parameterName);
        return enteredTraits;
    }

    private void ExitTraitDictionaryParameterScope(IReadOnlyList<string>? traitNames)
    {
        if (traitNames is null)
        {
            return;
        }
        foreach (string traitName in traitNames.Reverse())
        {
            List<ActiveTraitDictionaryParameter> parameters =
                _activeTraitDictionaryParameters[traitName];
            parameters.RemoveAt(parameters.Count - 1);
            if (parameters.Count == 0)
            {
                _activeTraitDictionaryParameters.Remove(traitName);
            }
        }
    }

    private (int Temp, TypeRef Type)? TryLowerActiveTraitMethod(
        TraitConstraint constraint,
        TraitMethodSymbol method)
    {
        ActiveTraitDictionaryParameter? active = FindActiveTraitDictionaryParameter(constraint);
        if (active is null)
        {
            return null;
        }
        return LowerExpr(new Expr.Var(
            TraitMethodParameterName(active.Metadata.Shape, method.Name))).AsPair();
    }

    private ActiveTraitDictionaryParameter? FindActiveTraitDictionaryParameter(
        TraitConstraint constraint)
    {
        if (!_activeTraitDictionaryParameters.TryGetValue(
                constraint.Trait.QualifiedName,
                out List<ActiveTraitDictionaryParameter>? activeParameters))
        {
            return null;
        }
        ActiveTraitDictionaryParameter? exact = activeParameters.LastOrDefault(parameter =>
            parameter.Metadata.Constraint is { } activeConstraint
            && TraitResolutionConstraintsEqual(
                PruneTraitConstraint(activeConstraint),
                PruneTraitConstraint(constraint)));
        return exact ?? (activeParameters.Count == 1 ? activeParameters[0] : null);
    }

    private (int Temp, TypeRef Type)? TryLowerTraitDictionaryFunctionCall(
        Expr rootExpression,
        IReadOnlyList<Expr> arguments,
        TextSpan span)
    {
        (int Temp, TypeRef Type)? explicitCall = TryLowerExplicitTraitDictionaryFunctionCall(rootExpression, arguments, span);
        if (explicitCall is not null) return explicitCall;
        if (arguments.Count == 0
            || ResolveSpecializableCalleeName(rootExpression) is not { } functionName
            || !TryGetTraitDictionaryInfo(functionName, Lookup(functionName), out TraitDictionaryFunctionInfo? info))
        {
            return null;
        }
        ((int functionTemp, TypeRef functionType) function, IReadOnlyList<TraitConstraint> instantiatedConstraints) =
            LowerTraitDictionaryCallRoot(rootExpression);
        bool evidenceNeedsLaterArgumentTypes = TraitEvidenceNeedsLaterArgumentTypes(instantiatedConstraints);
        (List<int> Temps, TypeRef Result)? loweredArguments = LowerTraitDictionaryRealArguments(
            arguments,
            function.functionType,
            functionName,
            span);
        if (loweredArguments is null)
        {
            return ReturnNeverWithDummyTemp();
        }
        MarkLateTraitTypeHintIfNeeded(evidenceNeedsLaterArgumentTypes);
        if (TryLowerConcreteTraitOperatorSpecializedCall(
                functionName,
                function.functionType,
                arguments,
                loweredArguments.Value,
                instantiatedConstraints,
                span) is { } specialized)
        {
            return specialized;
        }
        List<int> dictionaries = BuildResolvedTraitDictionaryArguments(
            functionName,
            info!,
            instantiatedConstraints,
            span);

        int applied = ApplyStaticEvidenceArguments(function.functionTemp, dictionaries);
        foreach (int argumentTemp in loweredArguments.Value.Temps)
        {
            int next = NewTemp();
            Emit(new IrInst.CallClosure(next, applied, argumentTemp));
            applied = next;
        }
        TypeRef resultType = Prune(loweredArguments.Value.Result);
        return (NormalizeStaticEvidenceResult(applied, resultType), resultType);
    }

    private (int Temp, TypeRef Type)? TryLowerConcreteTraitOperatorSpecializedCall(
        string functionName,
        TypeRef functionType,
        IReadOnlyList<Expr> arguments,
        (List<int> Temps, TypeRef Result) loweredArguments,
        IReadOnlyList<TraitConstraint> constraints,
        TextSpan span)
    {
        if (!TrySelectTraitOperatorSpecializationLambda(
                functionName,
                arguments.Count,
                constraints,
                span,
                out Expr.Lambda specializationLambda))
        {
            return null;
        }

        List<TypeRef> concreteParameterTypes = CollectAppliedParameterTypes(
            functionType,
            arguments.Count);
        if (concreteParameterTypes.Count != arguments.Count
            || concreteParameterTypes.Any(ValueTypeRemainsAbstract)
            || constraints.Count == 0
            || constraints.Any(constraint => !CanPrimitiveSpecializeConstraint(constraint, span)))
        {
            return null;
        }

        string label = GetOrCreateTraitOperatorSpecialization(
            functionName,
            specializationLambda,
            functionType,
            concreteParameterTypes);
        int applied = LowerTraitOperatorSpecializationClosure(label);
        foreach (int argumentTemp in loweredArguments.Temps)
        {
            int next = NewTemp();
            Emit(new IrInst.CallClosure(next, applied, argumentTemp));
            applied = next;
        }
        TypeRef resultType = Prune(loweredArguments.Result);
        return (NormalizeStaticEvidenceResult(applied, resultType), resultType);
    }

    private bool TrySelectTraitOperatorSpecializationLambda(
        string functionName,
        int argumentCount,
        IReadOnlyList<TraitConstraint> constraints,
        TextSpan span,
        out Expr.Lambda lambda)
    {
        lambda = null!;
        if (!_configuration.EnableTraitOperatorSpecialization || _inTraitOperatorSpecialization)
        {
            return false;
        }
        if (_specializableFunctions.TryGetValue(functionName, out var reuseSpecialization))
        {
            if (_inSpecialization
                || reuseSpecialization.ArgCount != argumentCount
                || !ExpressionContainsMappedTraitOperator(reuseSpecialization.Lambda)
                || !PreservesAffineStringAppendOptimization(reuseSpecialization.Lambda, constraints, span))
            {
                return false;
            }
            lambda = reuseSpecialization.Lambda;
            return true;
        }

        if (constraints.Count != 1)
        {
            return false;
        }
        Expr.Lambda? registered = _traitOperatorSpecializableFunctions.GetValueOrDefault(functionName);
        if (registered is null
            || CountLambdaChain(registered) != argumentCount
            || CollectReuseSpecializationCaptures(registered, functionName).Count > 0)
        {
            return false;
        }
        lambda = registered;
        return true;
    }

    private bool PreservesAffineStringAppendOptimization(
        Expr.Lambda lambda,
        IReadOnlyList<TraitConstraint> constraints,
        TextSpan span)
    {
        bool resolvesStringAdd = constraints.Any(constraint =>
            string.Equals(constraint.Trait.Name, "Add", StringComparison.Ordinal)
            && constraint.TypeArgs.Count == 1
            && Prune(constraint.TypeArgs[0]) is TypeRef.TStr
            && ResolveTraitEvidence(PruneTraitConstraint(constraint), span, [], 0)
                is TraitEvidencePlan.Instance);
        if (!resolvesStringAdd
            || !_maFunctionKeyByLambda.TryGetValue(lambda, out FuncKey ownershipFunction))
        {
            return false;
        }
        return GetTcoParameterOrdinalFacts(ownershipFunction).AffineSelfAppendOnly.Count > 0;
    }

    private List<TypeRef> CollectAppliedParameterTypes(TypeRef functionType, int argumentCount)
    {
        List<TypeRef> types = [];
        TypeRef cursor = Prune(functionType);
        for (int index = 0; index < argumentCount; index++)
        {
            if (cursor is not TypeRef.TFun function)
            {
                return [];
            }
            types.Add(Prune(function.Arg));
            cursor = Prune(function.Ret);
        }
        return types;
    }

    private bool CanPrimitiveSpecializeConstraint(TraitConstraint constraint, TextSpan span)
    {
        TraitConstraint concrete = PruneTraitConstraint(constraint);
        return concrete.TypeArgs.Count == 1
            && !ValueTypeRemainsAbstract(concrete.TypeArgs[0])
            && SupportsPrimitiveOperatorSpecialization(
                concrete.Trait.Name,
                Prune(concrete.TypeArgs[0]))
            && ResolveTraitEvidence(concrete, span, [], 0) is TraitEvidencePlan.Instance;
    }

    private string GetOrCreateTraitOperatorSpecialization(
        string functionName,
        Expr.Lambda lambda,
        TypeRef functionType,
        IReadOnlyList<TypeRef> concreteParameterTypes)
    {
        string cacheKey = functionName + "|" + string.Join(
            ",",
            concreteParameterTypes.Select(type => Pretty(Prune(type))));
        if (_traitOperatorSpecializations.TryGetValue(cacheKey, out string? cached))
        {
            return cached;
        }

        string label = _traitOperatorSpecializations.Count == 0
            ? $"{functionName}__trait"
            : $"{functionName}__trait${_traitOperatorSpecializations.Count}";
        _traitOperatorSpecializations[cacheKey] = label;
        _traitOperatorSpecializationCaptures[label] = CollectReuseSpecializationCaptures(
            lambda,
            functionName);

        bool savedInSpecialization = _inSpecialization;
        bool savedInTraitOperatorSpecialization = _inTraitOperatorSpecialization;
        bool savedSuppressTraitConstraintCollection = _suppressTraitConstraintCollection;
        IReadOnlyList<TypeRef>? savedConcreteTypes = _specializationConcreteParamTypes;
        int savedParameterCursor = _specializationParamCursor;
        TcoContext? savedTco = _tcoCtx;
        _inSpecialization = true;
        _inTraitOperatorSpecialization = true;
        _suppressTraitConstraintCollection = true;
        _specializationConcreteParamTypes = concreteParameterTypes;
        _specializationParamCursor = 0;
        _tcoCtx = CreateTraitOperatorSpecializationTcoContext(lambda, functionName);
        try
        {
            int instructionsBefore = _inst.Count;
            Dictionary<int, LoweredTempOwnershipFact> savedTempOwnershipFacts =
                SnapshotTempOwnershipFacts();
            LowerTraitOperatorSpecializationLambda(
                lambda,
                functionName,
                functionType,
                label,
                cacheKey);
            if (_inst.Count > instructionsBefore)
            {
                _inst.RemoveRange(instructionsBefore, _inst.Count - instructionsBefore);
            }
            RestoreTempOwnershipFacts(savedTempOwnershipFacts);
        }
        finally
        {
            _inSpecialization = savedInSpecialization;
            _inTraitOperatorSpecialization = savedInTraitOperatorSpecialization;
            _suppressTraitConstraintCollection = savedSuppressTraitConstraintCollection;
            _specializationConcreteParamTypes = savedConcreteTypes;
            _specializationParamCursor = savedParameterCursor;
            _tcoCtx = savedTco;
        }
        return label;
    }

    private TcoContext? CreateTraitOperatorSpecializationTcoContext(
        Expr.Lambda lambda,
        string functionName)
    {
        int parameterCount = CountLambdaChain(lambda);
        if (!HasTailSelfCalls(GetInnermostBody(lambda), functionName, parameterCount))
        {
            return null;
        }

        FuncKey? ownershipFunction = _maFunctionKeyByLambda.TryGetValue(
            lambda,
            out FuncKey registeredFunction)
                ? registeredFunction
                : null;
        var facts = GetTcoParameterOrdinalFacts(ownershipFunction);
        return new TcoContext(
            functionName,
            parameterCount,
            CollectLambdaParams(lambda),
            facts.LoopInvariant,
            facts.ArenaSelfContainedListRebuild,
            facts.FreshClosureRebuild,
            facts.BytesProvenanceSafeListRebuild,
            facts.AffineConsList,
            facts.ConsumedListTail,
            facts.BorrowInspectOnly,
            facts.AffineSelfAppendOnly,
            GetPatternBindingOwnershipFacts(ownershipFunction))
        {
            InTailPosition = false,
            OwnershipFunction = ownershipFunction,
        };
    }

    private int LowerTraitOperatorSpecializationClosure(string label)
    {
        IReadOnlyList<string> captures = _traitOperatorSpecializationCaptures[label];
        int environmentSize = captures.Count * 8;
        int environment = NewTemp();
        if (captures.Count == 0)
        {
            Emit(new IrInst.LoadConstInt(environment, 0));
        }
        else
        {
            Emit(new IrInst.Alloc(environment, environmentSize));
            for (int index = 0; index < captures.Count; index++)
            {
                (int captureTemp, _) = LowerVar(new Expr.Var(captures[index]));
                Emit(new IrInst.StoreMemOffset(environment, index * 8, captureTemp));
            }
        }

        int closure = NewTemp();
        Emit(new IrInst.MakeClosure(closure, label, environment, environmentSize));
        return closure;
    }

    private static bool TraitEvidenceNeedsLaterArgumentTypes(
        IEnumerable<TraitConstraint> constraints) =>
        constraints.Any(constraint => TraitTypeOperations.FreeVariables(
            new TypeScheme([], new TypeRef.TTuple(constraint.TypeArgs))).Count > 0);

    private void MarkLateTraitTypeHintIfNeeded(bool needed)
    {
        if (needed)
        {
            RequireLateTraitTypeHint();
        }
    }

    private (int Temp, TypeRef Type)? TryLowerTraitDictionaryReuseSpecialization(
        Expr.Call call,
        Expr rootExpression,
        List<Expr> arguments)
    {
        if (!_configuration.EnableReuse
            || ResolveSpecializableCalleeName(rootExpression) is not { } functionName
            || !TryGetTraitDictionaryInfo(functionName, Lookup(functionName), out TraitDictionaryFunctionInfo? info))
        {
            return null;
        }

        ReuseSpecializationQualification? qualification =
            QualifyReuseSpecializationCall(rootExpression, arguments);
        if (qualification is null)
        {
            return null;
        }
        if (qualification.Accepted && !CannotAttemptHigherOrderReuse(arguments))
        {
            ((int functionTemp, TypeRef functionType) function, IReadOnlyList<TraitConstraint> constraints) =
                LowerTraitDictionaryCallRoot(rootExpression);
            Unify(function.functionType, qualification.FunctionType!);
            return LowerReuseSpecializedCall(
                qualification.TargetFunction,
                function.functionType,
                arguments,
                call,
                info,
                constraints,
                function.functionTemp);
        }
        if (ShouldRecordReuseSpecializationCandidateRejection(
                rootExpression,
                qualification.TargetFunction))
        {
            RecordReuseSpecializationCandidateRejection(qualification, call);
        }
        return null;
    }

    private ((int Temp, TypeRef Type) Function, IReadOnlyList<TraitConstraint> Constraints)
        LowerTraitDictionaryCallRoot(Expr rootExpression)
    {
        PushTraitConstraintScope();
        _suppressActiveTraitDictionaryReferenceDepth++;
        try
        {
            (int Temp, TypeRef Type) function = LowerExpr(rootExpression).AsPair();
            IReadOnlyList<TraitConstraint> constraints = PopTraitConstraintScope();
            return (function, constraints);
        }
        finally
        {
            _suppressActiveTraitDictionaryReferenceDepth--;
        }
    }

    private (int Temp, TypeRef Type)? TryLowerExplicitTraitDictionaryFunctionCall(
        Expr rootExpression,
        IReadOnlyList<Expr> arguments,
        TextSpan span)
    {
        if (arguments.Count == 0
            || arguments[0] is not Expr.Var evidence
            || !evidence.Name.StartsWith("__trait_evidence_", StringComparison.Ordinal)
            || ResolveSpecializableCalleeName(rootExpression) is not { } functionName
            || !TryGetTraitDictionaryInfo(functionName, Lookup(functionName), out TraitDictionaryFunctionInfo? info)
            || arguments.Count < info!.Dictionaries.Count)
        {
            return null;
        }

        ((int functionTemp, TypeRef exposedType) function, _) =
            LowerTraitDictionaryCallRoot(rootExpression);

        int applied = function.functionTemp;
        TypeRef exposedType = function.exposedType;
        bool typeIncludesHiddenParameters = Lookup(functionName) is Binding.Self;
        for (int index = 0; index < info.Dictionaries.Count; index++)
        {
            int dictionaryTemp = LowerExpr(arguments[index]).Temp;
            applied = ApplyStaticEvidenceArguments(applied, [dictionaryTemp]);
            if (typeIncludesHiddenParameters)
            {
                exposedType = Prune(exposedType) is TypeRef.TFun hiddenParameter
                    ? hiddenParameter.Ret
                    : new TypeRef.TNever();
            }
        }
        IReadOnlyList<Expr> realArguments = arguments.Skip(info.Dictionaries.Count).ToArray();
        (List<int> Temps, TypeRef Result)? loweredArguments = LowerTraitDictionaryRealArguments(
            realArguments,
            exposedType,
            functionName,
            span);
        if (loweredArguments is null)
        {
            return ReturnNeverWithDummyTemp();
        }
        foreach (int argumentTemp in loweredArguments.Value.Temps)
        {
            int next = NewTemp();
            Emit(new IrInst.CallClosure(next, applied, argumentTemp));
            applied = next;
        }
        TypeRef resultType = Prune(loweredArguments.Value.Result);
        return (NormalizeStaticEvidenceResult(applied, resultType), resultType);
    }

    private (int Temp, TypeRef Type)? TryLowerActiveTraitDictionaryReference(
        Expr.Var reference,
        Binding binding)
    {
        if (_suppressActiveTraitDictionaryReferenceDepth > 0
            || !TryGetTraitDictionaryInfo(reference.Name, binding, out TraitDictionaryFunctionInfo? info))
        {
            return null;
        }

        int functionTemp = NewTemp();
        InstantiatedTypeScheme instantiated;
        switch (binding)
        {
            case Binding.Scheme local:
                Emit(new IrInst.LoadLocal(functionTemp, local.Slot));
                instantiated = InstantiateScheme(local.S);
                break;
            case Binding.EnvScheme environment:
                Emit(new IrInst.LoadEnv(functionTemp, environment.Index));
                instantiated = InstantiateScheme(environment.S);
                break;
            default:
                return null;
        }

        if (instantiated.Constraints.Count != info!.Dictionaries.Count
            || instantiated.Constraints.Any(constraint =>
                FindActiveTraitDictionaryParameter(constraint) is null))
        {
            return null;
        }

        List<int> dictionaries = [];
        foreach (TraitConstraint constraint in instantiated.Constraints)
        {
            ActiveTraitDictionaryParameter active = FindActiveTraitDictionaryParameter(constraint)!;
            dictionaries.Add(LowerExpr(new Expr.Var(active.ParameterName)).Temp);
        }
        RequireTraitConstraints(instantiated.Constraints);
        int applied = ApplyStaticEvidenceArguments(functionTemp, dictionaries);
        return (NormalizeStaticEvidenceResult(applied, instantiated.Body), instantiated.Body);
    }

    private (int Temp, TypeRef Type)? TryLowerTraitDictionaryFunctionValue(
        Expr expression,
        TypeRef expectedType)
    {
        if (!IsTraitDictionaryFunctionValue(expression, out string functionName)
            || !TryGetTraitDictionaryInfo(functionName, Lookup(functionName), out TraitDictionaryFunctionInfo? info))
        {
            return null;
        }

        ((int functionTemp, TypeRef functionType) function, IReadOnlyList<TraitConstraint> constraints) =
            LowerTraitDictionaryCallRoot(expression);
        Unify(expectedType, function.functionType);
        List<int> dictionaries = BuildResolvedTraitDictionaryArguments(
            functionName,
            info!,
            constraints,
            GetSpan(expression));
        TypeRef resultType = Prune(function.functionType);
        int applied = ApplyStaticEvidenceArguments(function.functionTemp, dictionaries);
        return (NormalizeStaticEvidenceResult(applied, resultType), resultType);
    }

    private void PreconstrainKnownCallArgumentTypes(
        Expr rootExpression,
        IReadOnlyList<Expr> arguments,
        TypeRef functionType)
    {
        TypeRef cursor = functionType;
        for (int index = 0; index < arguments.Count; index++)
        {
            cursor = Prune(cursor);
            if (cursor is TypeRef.TVar)
            {
                Unify(cursor, new TypeRef.TFun(NewTypeVar(), NewTypeVar()) { Row = AmbientRow });
                cursor = Prune(cursor);
            }
            if (cursor is not TypeRef.TFun function)
            {
                return;
            }

            string? calleeName = TryGetCalleeDisplayName(rootExpression);
            string context = calleeName is null
                ? $"in argument #{index + 1} of function call"
                : $"in argument #{index + 1} of call to '{calleeName}'";
            using (PushDiagnosticContext(context))
            {
                ConstrainKnownExpressionType(arguments[index], function.Arg);
            }
            cursor = function.Ret;
        }
    }

    private void PreconstrainCallResultType(
        TypeRef functionType,
        int argumentCount,
        TypeRef? expectedType)
    {
        if (expectedType is null)
        {
            return;
        }
        TypeRef cursor = functionType;
        for (int index = 0; index < argumentCount; index++)
        {
            cursor = Prune(cursor);
            if (cursor is TypeRef.TVar)
            {
                Unify(cursor, new TypeRef.TFun(NewTypeVar(), NewTypeVar()) { Row = AmbientRow });
                cursor = Prune(cursor);
            }
            if (cursor is not TypeRef.TFun function)
            {
                return;
            }
            cursor = function.Ret;
        }
        Unify(cursor, expectedType);
    }

    private void UnifyExpectedType(TypeRef actualType, TypeRef? expectedType)
    {
        if (expectedType is not null)
        {
            Unify(actualType, expectedType);
        }
    }

    private void ConstrainKnownExpressionType(Expr expression, TypeRef expectedType)
    {
        TypeRef? known = TryGetKnownExpressionType(expression);
        if (known is not null)
        {
            Unify(expectedType, known);
            return;
        }

        TypeRef pruned = Prune(expectedType);
        if (expression is Expr.TupleLit tuple)
        {
            TypeRef[] elements = tuple.Elements.Select(_ => NewTypeVar()).ToArray();
            Unify(pruned, new TypeRef.TTuple(elements));
            for (int index = 0; index < tuple.Elements.Count; index++)
            {
                ConstrainKnownExpressionType(tuple.Elements[index], elements[index]);
            }
        }
        else if (expression is Expr.ListLit list)
        {
            TypeRef element = NewTypeVar();
            Unify(pruned, new TypeRef.TList(element));
            foreach (Expr item in list.Elements)
            {
                ConstrainKnownExpressionType(item, element);
            }
        }
    }

    private TypeRef? TryGetKnownExpressionType(Expr expression)
    {
        return expression switch
        {
            Expr.IntLit => new TypeRef.TInt(),
            Expr.UIntLit unsigned => new TypeRef.TUInt(unsigned.Bits),
            Expr.BigIntLit => new TypeRef.TBigInt(),
            Expr.FloatLit => new TypeRef.TFloat(),
            Expr.StrLit => new TypeRef.TStr(),
            Expr.RuneLit => new TypeRef.TRune(),
            Expr.BoolLit => new TypeRef.TBool(),
            Expr.Var variable => TryGetKnownBindingType(Lookup(variable.Name)),
            Expr.QualifiedVar qualified when ResolveSpecializableCalleeName(qualified) is { } resolved =>
                TryGetKnownBindingType(Lookup(resolved)),
            _ => null,
        };
    }

    private TypeRef? TryGetKnownBindingType(Binding? binding) => binding switch
    {
        Binding.Local local => local.Type,
        Binding.Env environment => environment.Type,
        Binding.Self self => self.Type,
        Binding.ExternalFunction external => external.Type,
        Binding.Scheme scheme => InstantiateScheme(scheme.S).Body,
        Binding.EnvScheme environment => InstantiateScheme(environment.S).Body,
        Binding.Intrinsic intrinsic => InstantiateScheme(intrinsic.S).Body,
        Binding.PreludeValue prelude => InstantiateScheme(prelude.S).Body,
        _ => null,
    };

    private bool IsTraitDictionaryFunctionValue(Expr expression, out string functionName)
    {
        functionName = string.Empty;
        if (expression is not (Expr.Var or Expr.QualifiedVar)
            || ResolveSpecializableCalleeName(expression) is not { } resolved
            || !TryGetTraitDictionaryInfo(resolved, Lookup(resolved), out _))
        {
            return false;
        }

        functionName = resolved;
        return true;
    }

    private List<int> BuildResolvedTraitDictionaryArguments(
        string functionName,
        TraitDictionaryFunctionInfo info,
        IReadOnlyList<TraitConstraint> constraints,
        TextSpan span) =>
        BuildResolvedTraitDictionaryValues(functionName, info, constraints, span)
            .Select(value => value.Temp)
            .ToList();

    private List<(int Temp, TypeRef Type)> BuildResolvedTraitDictionaryValues(
        string functionName,
        TraitDictionaryFunctionInfo info,
        IReadOnlyList<TraitConstraint> constraints,
        TextSpan span)
    {
        List<(int Temp, TypeRef Type)> dictionaries = [];
        foreach (TraitDictionaryShape shape in info.Dictionaries)
        {
            TraitConstraint? constraint = shape.ConstraintOrdinal < constraints.Count
                ? PruneTraitConstraint(constraints[shape.ConstraintOrdinal])
                : null;
            if (constraint is not null
                && !string.Equals(
                    constraint.Trait.QualifiedName,
                    shape.Trait.QualifiedName,
                    StringComparison.Ordinal))
            {
                constraint = null;
            }
            if (constraint is null)
            {
                ReportDiagnostic(
                    span,
                    $"Internal trait evidence ABI for '{functionName}' has no '{shape.Trait.QualifiedName}' constraint.",
                    InvalidTraitDeclarationCode);
                dictionaries.Add((EmitDummyTemp(), new TypeRef.TNever()));
                continue;
            }
            ActiveTraitDictionaryParameter? active = FindActiveTraitDictionaryParameter(constraint);
            if (active is not null)
            {
                dictionaries.Add(LowerExpr(new Expr.Var(active.ParameterName)).AsPair());
                continue;
            }
            TraitEvidencePlan? plan = ResolveTraitEvidence(constraint, span, [], 0);
            dictionaries.Add(plan is null
                ? (EmitDummyTemp(), new TypeRef.TNever())
                : BuildTraitDictionary(plan, span));
        }
        return dictionaries;
    }

    private (List<int> Temps, TypeRef Result)? LowerTraitDictionaryRealArguments(
        IReadOnlyList<Expr> arguments,
        TypeRef functionType,
        string functionName,
        TextSpan span)
    {
        PreconstrainKnownCallArgumentTypes(new Expr.Var(functionName), arguments, functionType);
        List<int> argumentTemps = [];
        TypeRef resultType = functionType;
        foreach (Expr argument in arguments)
        {
            resultType = Prune(resultType);
            if (resultType is TypeRef.TVar)
            {
                Unify(resultType, new TypeRef.TFun(NewTypeVar(), NewTypeVar()));
                resultType = Prune(resultType);
            }
            if (resultType is not TypeRef.TFun function)
            {
                ReportDiagnostic(span, $"'{functionName}' applied to too many arguments.", DiagnosticCodes.TypeMismatch);
                return null;
            }
            (int argumentTemp, TypeRef argumentType) =
                TryLowerTraitDictionaryFunctionValue(argument, function.Arg)
                ?? LowerExpr(
                    argument,
                    LoweredValueRequest.None.WithExpectedType(function.Arg)).AsPair();
            argumentTemps.Add(argumentTemp);
            Unify(function.Arg, argumentType);
            SubsumeCalleeRow(function.Row, span);
            resultType = function.Ret;
        }
        return (argumentTemps, resultType);
    }

    private (int Temp, TypeRef Type) BuildTraitDictionary(TraitEvidencePlan plan, TextSpan span)
    {
        // Static evidence is compiler-generated support data, not a reconstruction of the source
        // value currently being matched. It must never consume a live Perceus token from that
        // source value: a dictionary tuple or one of its default-method closures can have a wholly
        // different layout and lifetime. Preserve the source tokens for the surrounding expression.
        ReuseToken[] sourceReuseTokens = _reuseTokens.ToArray();
        bool sourceInSpecialization = _inSpecialization;
        string? sourceSpecializingLinearParam = _specializingLinearParam;
        string? sourceSpecializingReuseLabel = _specializingReuseLabel;
        IReadOnlyList<TypeRef>? sourceConcreteParamTypes = _specializationConcreteParamTypes;
        int sourceParamCursor = _specializationParamCursor;
        HashSet<string>? sourceFreshInputNames = _specFreshInputNames;
        int instructionStart = _inst.Count;
        _reuseTokens.Clear();
        _inSpecialization = false;
        _specializingLinearParam = null;
        _specializingReuseLabel = null;
        _specializationConcreteParamTypes = null;
        _specializationParamCursor = 0;
        _specFreshInputNames = null;
        try
        {
            (int Temp, TypeRef Type) dictionary =
                BuildTraitDictionary(plan, span, new HashSet<string>(StringComparer.Ordinal));
            foreach (IrInst instruction in _inst.Skip(instructionStart))
            {
                if (instruction is IrInst.Alloc or IrInst.MakeClosure or IrInst.MakeClosureStack)
                {
                    _traitEvidenceConstructionInstructions.Add(instruction);
                }
            }
            return dictionary;
        }
        finally
        {
            _reuseTokens.Clear();
            _reuseTokens.AddRange(sourceReuseTokens);
            _inSpecialization = sourceInSpecialization;
            _specializingLinearParam = sourceSpecializingLinearParam;
            _specializingReuseLabel = sourceSpecializingReuseLabel;
            _specializationConcreteParamTypes = sourceConcreteParamTypes;
            _specializationParamCursor = sourceParamCursor;
            _specFreshInputNames = sourceFreshInputNames;
        }
    }

    private (int Temp, TypeRef Type) BuildTraitDictionary(
        TraitEvidencePlan plan,
        TextSpan span,
        HashSet<string> includedTraits)
    {
        if (plan is TraitEvidencePlan.Parameter parameter)
        {
            ActiveTraitDictionaryParameter? active =
                FindActiveTraitDictionaryParameter(parameter.Constraint);
            if (active is not null)
            {
                return LowerExpr(new Expr.Var(active.ParameterName)).AsPair();
            }
            ReportDiagnostic(
                span,
                $"No hidden dictionary parameter supplies abstract requirement '{FormatTraitConstraint(parameter.Constraint)}'.",
                InvalidTraitDeclarationCode);
            return (EmitDummyTemp(), new TypeRef.TNever());
        }

        TraitEvidencePlan.Instance implementation = (TraitEvidencePlan.Instance)plan;
        includedTraits.Add(implementation.Goal.Trait.QualifiedName);
        Dictionary<string, (int Temp, TypeRef Type)> methodValues = new(StringComparer.Ordinal);
        _scopes.Push(_scopes.Peek());
        foreach (TraitMethodSymbol method in OrderTraitMethodsForConstruction(implementation, span))
        {
            (int Temp, TypeRef Type) methodValue = BuildTraitImplementationMethod(implementation, method, span);
            methodValues[method.Name] = methodValue;
            int slot = NewLocal();
            Emit(new IrInst.StoreLocal(slot, methodValue.Temp));
            string bindingName = TraitImplementationMethodBindingName(implementation.Goal.Trait, method.Name);
            SetCurrentScopeBinding(
                bindingName,
                new Binding.Local(slot, methodValue.Type, method.Span));
        }
        _scopes.Pop();
        List<(int Temp, TypeRef Type)> fields = implementation.Goal.Trait.Methods.Values
            .OrderBy(candidate => candidate.Name, StringComparer.Ordinal)
            .Select(method => methodValues[method.Name])
            .ToList();
        foreach (TraitEvidencePlan supertrait in implementation.Supertraits)
        {
            if (includedTraits.Add(supertrait.Goal.Trait.QualifiedName))
            {
                fields.Add(BuildTraitDictionary(supertrait, span, includedTraits));
            }
        }
        return PackTraitDictionaryFields(fields);
    }

    private IReadOnlyList<TraitMethodSymbol> OrderTraitMethodsForConstruction(
        TraitEvidencePlan.Instance plan,
        TextSpan span)
    {
        if (_traitMethodConstructionOrders.TryGetValue(
                plan.SelectedInstance,
                out IReadOnlyList<TraitMethodSymbol>? cached))
        {
            return cached;
        }

        List<TraitMethodSymbol> ordered = [];
        Dictionary<string, int> states = new(StringComparer.Ordinal);
        bool hasCycle = false;
        void Visit(TraitMethodSymbol method, List<string> trace)
        {
            int state = states.GetValueOrDefault(method.Name);
            if (state == 2)
            {
                return;
            }
            if (state == 1)
            {
                hasCycle = true;
                ReportDiagnostic(
                    span,
                    $"Trait method dependency cycle while constructing '{plan.Goal.Trait.Name}': {string.Join(" -> ", trace.Append(method.Name))}.",
                    TraitCycleCode);
                return;
            }
            states[method.Name] = 1;
            Expr? body = plan.SelectedInstance.MethodImplementations.GetValueOrDefault(method.Name)
                ?? method.DefaultImplementation;
            foreach (string dependency in CollectSelectedTraitMethodDependencies(body, plan.Goal.Trait)
                         .OrderBy(name => name, StringComparer.Ordinal))
            {
                if (string.Equals(dependency, method.Name, StringComparison.Ordinal))
                {
                    if (!plan.SelectedInstance.MethodImplementations.ContainsKey(method.Name))
                    {
                        ReportDiagnostic(
                            span,
                            $"Default method '{plan.Goal.Trait.Name}.{method.Name}' depends on itself without an implementation override.",
                            TraitCycleCode);
                    }
                    continue;
                }
                if (plan.Goal.Trait.Methods.TryGetValue(dependency, out TraitMethodSymbol? dependencyMethod))
                {
                    Visit(dependencyMethod, [.. trace, method.Name]);
                }
            }
            states[method.Name] = 2;
            ordered.Add(method);
        }
        foreach (TraitMethodSymbol method in plan.Goal.Trait.Methods.Values
                     .OrderBy(candidate => candidate.Name, StringComparer.Ordinal))
        {
            Visit(method, []);
        }
        return CacheTraitMethodConstructionOrder(plan.SelectedInstance, ordered, hasCycle);
    }

    private IReadOnlyList<TraitMethodSymbol> CacheTraitMethodConstructionOrder(
        TraitInstanceSymbol instance,
        IEnumerable<TraitMethodSymbol> ordered,
        bool hasCycle)
    {
        IReadOnlyList<TraitMethodSymbol> result = ordered
            .DistinctBy(method => method.Name, StringComparer.Ordinal)
            .ToArray();
        if (!hasCycle)
        {
            _traitMethodConstructionOrders[instance] = result;
        }
        return result;
    }

    private IEnumerable<string> CollectSelectedTraitMethodDependencies(Expr? expression, TraitSymbol trait)
    {
        if (expression is null)
        {
            return [];
        }
        if (_selectedTraitMethodDependencies.TryGetValue(
                expression,
                out Dictionary<TraitSymbol, string[]>? byTrait)
            && byTrait.TryGetValue(trait, out string[]? cached))
        {
            return cached;
        }

        HashSet<string> dependencies = new(StringComparer.Ordinal);
        void Visit(Expr current)
        {
            if (current is Expr.QualifiedVar reference
                && TryGetTraitMethod(reference, out TraitSymbol referencedTrait, out TraitMethodSymbol method)
                && string.Equals(referencedTrait.QualifiedName, trait.QualifiedName, StringComparison.Ordinal))
            {
                dependencies.Add(method.Name);
            }
            _ = MapChildExpressions(current, child =>
            {
                Visit(child);
                return child;
            });
        }
        Visit(expression);
        string[] result = [.. dependencies];
        byTrait ??= new Dictionary<TraitSymbol, string[]>(
            ReferenceEqualityComparer.Instance);
        byTrait[trait] = result;
        _selectedTraitMethodDependencies[expression] = byTrait;
        return result;
    }

    private static string TraitImplementationMethodBindingName(TraitSymbol trait, string method) =>
        $"__trait_selected_{trait.Name}_{method}";

    private (int Temp, TypeRef Type) BuildTraitImplementationMethod(
        TraitEvidencePlan.Instance plan,
        TraitMethodSymbol method,
        TextSpan span)
    {
        Expr? implementation = plan.SelectedInstance.MethodImplementations.GetValueOrDefault(method.Name)
            ?? method.DefaultImplementation;
        if (implementation is null)
        {
            ReportDiagnostic(
                span,
                $"Implementation of '{plan.Goal.Trait.Name}' has no method '{method.Name}'.",
                InvalidTraitImplementationDeclarationCode);
            return (EmitDummyTemp(), new TypeRef.TNever());
        }

        IReadOnlyDictionary<string, TypeRef> traitSubstitution = plan.Goal.Trait.TypeParameters
            .Select((parameter, index) => (parameter.Name, Type: plan.Goal.TypeArgs[index]))
            .ToDictionary(item => item.Name, item => item.Type, StringComparer.Ordinal);
        TypeRef expectedType = SubstituteTraitParameters(method.Scheme.Body, traitSubstitution);
        Expr loweredImplementation = RewriteSelectedTraitMethodReferences(
            implementation,
            plan,
            method);
        IReadOnlyList<TypeRef>? savedTypes = _annotationParamTypes;
        int savedCursor = _annotationParamCursor;
        Expr.Lambda? savedTarget = _annotationTargetLambda;
        Expr.Lambda? loweredLambda = FindSelectedImplementationLambda(loweredImplementation);
        if (loweredLambda is not null)
        {
            _annotationParamTypes = PeelAnnotationParamTypes(expectedType, CountLambdaChain(loweredLambda));
            _annotationParamCursor = 0;
            _annotationTargetLambda = loweredLambda;
        }
        PushTraitConstraintScope();
        Dictionary<string, TypeRef>? savedTypeParameterScope = _typeExprParamScope;
        _typeExprParamScope = ResolveSelectedMethodTypeParameterScope(
            plan, method, traitSubstitution);
        string selfName = EnterActiveTraitImplementation(plan.Goal, method.Name, expectedType);
        (int methodTemp, TypeRef methodType) methodValue;
        try
        {
            methodValue = LowerExpr(loweredImplementation).AsPair();
        }
        finally
        {
            _activeTraitImplementationMethods.RemoveAt(_activeTraitImplementationMethods.Count - 1);
            _generatedTraitRecursiveTypes.Remove(selfName);
            _typeExprParamScope = savedTypeParameterScope;
        }
        IReadOnlyList<TraitConstraint> methodRequirements = PopTraitConstraintScope();
        _annotationParamTypes = savedTypes;
        _annotationParamCursor = savedCursor;
        _annotationTargetLambda = savedTarget;
        using (PushDiagnosticSpan(GetSpan(implementation)))
        {
            Unify(expectedType, methodValue.methodType);
        }
        foreach (TraitConstraint requirement in methodRequirements)
        {
            _ = ResolveTraitEvidence(requirement, span, [], 0);
        }
        return (methodValue.methodTemp, Prune(expectedType));
    }

    private Dictionary<string, TypeRef> ResolveSelectedImplementationSubstitution(
        TraitEvidencePlan.Instance plan)
    {
        var substitution = new Dictionary<string, TypeRef>(StringComparer.Ordinal);
        if (MatchInstanceHeadTypes(
                plan.SelectedInstance.Head.TypeArgs,
                plan.Goal.TypeArgs,
                substitution))
        {
            return substitution;
        }

        throw new InvalidOperationException(
            $"Selected implementation head no longer matches '{FormatTraitConstraint(plan.Goal)}'.");
    }

    private Dictionary<string, TypeRef> ResolveSelectedMethodTypeParameterScope(
        TraitEvidencePlan.Instance plan,
        TraitMethodSymbol method,
        IReadOnlyDictionary<string, TypeRef> traitSubstitution) =>
        plan.SelectedInstance.MethodImplementations.ContainsKey(method.Name)
            ? ResolveSelectedImplementationSubstitution(plan)
            : traitSubstitution.ToDictionary(
                item => item.Key,
                item => item.Value,
                StringComparer.Ordinal);

    private static Expr.Lambda? FindSelectedImplementationLambda(Expr expression) =>
        expression switch
        {
            Expr.Lambda lambda => lambda,
            Expr.Let binding => FindSelectedImplementationLambda(binding.Body),
            Expr.LetRecursive binding => FindSelectedImplementationLambda(binding.Value),
            _ => null,
        };

    private string EnterActiveTraitImplementation(
        TraitConstraint constraint,
        string method,
        TypeRef expectedType)
    {
        string selfName = $"__trait_impl_{constraint.Trait.Name}_{method}";
        _activeTraitImplementationMethods.Add(new ActiveTraitImplementationMethod(constraint, method, selfName));
        _generatedTraitRecursiveTypes[selfName] = expectedType;
        return selfName;
    }

    private Expr RewriteSelectedTraitMethodReferences(
        Expr implementation,
        TraitEvidencePlan.Instance plan,
        TraitMethodSymbol method)
    {
        TraitSymbol trait = plan.Goal.Trait;
        string selfName = $"__trait_impl_{trait.Name}_{method.Name}";
        bool hasSelfReference = false;
        Expr Rewrite(Expr current)
        {
            if (current is Expr.QualifiedVar reference
                && IsSelectedTraitMethodReference(reference, trait, out TraitMethodSymbol referencedMethod))
            {
                if (string.Equals(referencedMethod.Name, method.Name, StringComparison.Ordinal))
                {
                    hasSelfReference = true;
                    return current;
                }
                return new Expr.Var(TraitImplementationMethodBindingName(trait, referencedMethod.Name));
            }

            if (current is Expr.Lambda or Expr.Let or Expr.LetResult or Expr.LetRecursive)
            {
                return RewriteTraitBindingChain(current, Rewrite);
            }

            return MapChildExpressions(current, Rewrite);
        }

        Expr rewritten = Rewrite(implementation);
        if (!hasSelfReference)
        {
            return rewritten;
        }

        TextSpan span = AstSpans.GetOrDefault(implementation);
        Expr.Var selfReference = new(selfName);
        AstSpans.Set(selfReference, span);
        Expr.LetRecursive recursiveImplementation = new(selfName, rewritten, selfReference);
        AstSpans.Set(recursiveImplementation, span);
        AstSpans.SetLetRecursiveName(recursiveImplementation, span);
        return recursiveImplementation;
    }

    private static Expr RewriteTraitBindingChain(Expr first, Func<Expr, Expr> rewrite)
    {
        List<Func<Expr, Expr>> rebuild = [];
        Expr current = first;
        while (TryPrepareTraitBindingRewrite(current, rewrite, rebuild, out Expr body))
        {
            current = body;
        }

        Expr rewritten = rewrite(current);
        for (int index = rebuild.Count - 1; index >= 0; index--)
        {
            rewritten = rebuild[index](rewritten);
        }
        return rewritten;
    }

    private static bool TryPrepareTraitBindingRewrite(
        Expr current,
        Func<Expr, Expr> rewrite,
        List<Func<Expr, Expr>> rebuild,
        out Expr body)
    {
        switch (current)
        {
            case Expr.Lambda lambda:
                rebuild.Add(next => CopyLambdaSpans(lambda, new Expr.Lambda(lambda.ParamName, next)
                {
                    ParamAnnotation = lambda.ParamAnnotation,
                }));
                body = lambda.Body;
                return true;
            case Expr.Let binding:
                Expr value = rewrite(binding.Value);
                rebuild.Add(next => CopyLetSpans(binding, new Expr.Let(binding.Name, value, next)
                {
                    TypeAnnotation = binding.TypeAnnotation,
                    Requires = binding.Requires,
                    SugarParams = binding.SugarParams,
                }));
                body = binding.Body;
                return true;
            case Expr.LetResult binding:
                Expr resultValue = rewrite(binding.Value);
                rebuild.Add(next => CopyLetResultSpans(
                    binding,
                    new Expr.LetResult(binding.Name, resultValue, next)));
                body = binding.Body;
                return true;
            case Expr.LetRecursive binding:
                Expr recursiveValue = rewrite(binding.Value);
                rebuild.Add(next => CopyLetRecursiveSpans(
                    binding,
                    new Expr.LetRecursive(binding.Name, recursiveValue, next)
                    {
                        TypeAnnotation = binding.TypeAnnotation,
                        Requires = binding.Requires,
                        SugarParams = binding.SugarParams,
                    }));
                body = binding.Body;
                return true;
            default:
                body = current;
                return false;
        }
    }

    private bool IsSelectedTraitMethodReference(
        Expr.QualifiedVar reference,
        TraitSymbol selectedTrait,
        out TraitMethodSymbol method)
    {
        if (TryGetTraitMethod(reference, out TraitSymbol referencedTrait, out method)
            && string.Equals(
                referencedTrait.QualifiedName,
                selectedTrait.QualifiedName,
                StringComparison.Ordinal))
        {
            return true;
        }

        bool namesSelectedTrait = string.Equals(
                reference.Module,
                selectedTrait.Name,
                StringComparison.Ordinal)
            || string.Equals(
                reference.Module,
                selectedTrait.QualifiedName,
                StringComparison.Ordinal)
            || reference.Module.EndsWith($".{selectedTrait.Name}", StringComparison.Ordinal);
        return namesSelectedTrait
            && selectedTrait.Methods.TryGetValue(reference.Name, out method!);
    }

    private IEnumerable<string> CollectActiveTraitMethodCaptures(Expr expression)
    {
        if (_activeTraitImplementationMethods.Count == 0)
        {
            return [];
        }

        HashSet<string> captures = new(StringComparer.Ordinal);
        Stack<Expr> pending = new();
        pending.Push(expression);
        while (pending.TryPop(out Expr? current))
        {
            if (current is Expr.QualifiedVar reference)
            {
                foreach (ActiveTraitImplementationMethod active in _activeTraitImplementationMethods)
                {
                    if (string.Equals(reference.Name, active.Method, StringComparison.Ordinal)
                        && IsSelectedTraitMethodReference(reference, active.Constraint.Trait, out _))
                    {
                        captures.Add(active.BindingName);
                    }
                }
            }
            _ = MapChildExpressions(current, child =>
            {
                pending.Push(child);
                return child;
            });
        }
        return captures;
    }

    private IEnumerable<string> CollectActiveTraitDictionaryOperatorCaptures(Expr expression)
    {
        if (_activeTraitDictionaryParameters.Count == 0)
        {
            return [];
        }

        return CollectActiveTraitDictionaryOperatorCapturesCore(expression);
    }

    private IEnumerable<string> CollectActiveTraitDictionaryOperatorCapturesCore(Expr expression)
    {
        HashSet<string> captures = new(StringComparer.Ordinal);
        void Visit(Expr current)
        {
            (string Trait, string Method)? mapped = current is Expr.QualifiedVar reference
                && TryGetTraitMethod(reference, out TraitSymbol explicitTrait, out TraitMethodSymbol explicitMethod)
                    ? (explicitTrait.QualifiedName, explicitMethod.Name)
                    : current switch
                    {
                        Expr.Add => ("Add", "add"),
                        Expr.Subtract { Left: Expr.IntLit { Value: 0 } } => ("Negate", "negate"),
                        Expr.Subtract => ("Subtract", "subtract"),
                        Expr.Multiply => ("Multiply", "multiply"),
                        Expr.Divide => ("Divide", "divide"),
                        Expr.Modulo => ("Remainder", "remainder"),
                        Expr.LogicalNot => ("Not", "not"),
                        Expr.BitwiseAnd => ("BitAnd", "bitAnd"),
                        Expr.BitwiseOr => ("BitOr", "bitOr"),
                        Expr.BitwiseXor => ("BitXor", "bitXor"),
                        Expr.ShiftLeft => ("ShiftLeft", "shiftLeft"),
                        Expr.ShiftRight => ("ShiftRight", "shiftRight"),
                        Expr.BitwiseNot => ("BitwiseNot", "bitwiseNot"),
                        Expr.Equal => ("Eq", "equal"),
                        Expr.NotEqual => ("Eq", "notEqual"),
                        Expr.LessThan => ("Ord", "less"),
                        Expr.LessOrEqual => ("Ord", "lessOrEqual"),
                        Expr.GreaterThan => ("Ord", "greater"),
                        Expr.GreaterOrEqual => ("Ord", "greaterOrEqual"),
                        _ => null,
                    };
            if (mapped is { } operation)
            {
                foreach ((string qualifiedName, List<ActiveTraitDictionaryParameter> parameters) in
                         _activeTraitDictionaryParameters)
                {
                    if (!string.Equals(qualifiedName, operation.Trait, StringComparison.Ordinal)
                        && !qualifiedName.EndsWith($".{operation.Trait}", StringComparison.Ordinal))
                    {
                        continue;
                    }
                    foreach (ActiveTraitDictionaryParameter parameter in parameters)
                    {
                        captures.Add(TraitMethodParameterName(
                            parameter.Metadata.Shape,
                            operation.Method));
                    }
                }
            }
            _ = MapChildExpressions(current, child =>
            {
                Visit(child);
                return child;
            });
        }
        Visit(expression);
        return captures;
    }

    private (int Temp, TypeRef Type) PackTraitDictionaryFields(
        IReadOnlyList<(int Temp, TypeRef Type)> fields)
    {
        if (fields.Count == 1)
        {
            return fields[0];
        }
        int dictionaryTemp = NewTemp();
        Emit(new IrInst.Alloc(dictionaryTemp, fields.Count * 8));
        for (int index = 0; index < fields.Count; index++)
        {
            Emit(new IrInst.StoreMemOffset(dictionaryTemp, index * 8, fields[index].Temp));
        }
        return (dictionaryTemp, new TypeRef.TTuple(fields.Select(field => field.Type).ToArray()));
    }

    private (int Temp, TypeRef Type) SelectTraitDictionaryMethod(
        (int Temp, TypeRef Type) dictionary,
        TraitSymbol trait,
        TraitMethodSymbol method)
    {
        TraitMethodSymbol[] methods = trait.Methods.Values
            .OrderBy(candidate => candidate.Name, StringComparer.Ordinal)
            .ToArray();
        int index = Array.FindIndex(methods, candidate => string.Equals(
            candidate.Name,
            method.Name,
            StringComparison.Ordinal));
        if (methods.Length + trait.Supertraits.Count == 1)
        {
            return dictionary;
        }
        TypeRef fieldType = dictionary.Type is TypeRef.TTuple tuple
            ? tuple.Elements[index]
            : new TypeRef.TNever();
        int methodTemp = NewTemp();
        Emit(new IrInst.LoadMemOffset(methodTemp, dictionary.Temp, index * 8));
        return (methodTemp, fieldType);
    }
}
