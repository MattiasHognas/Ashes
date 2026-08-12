using Ashes.Frontend;

namespace Ashes.Semantics;

public sealed partial class Lowering
{
    private static readonly IReadOnlySet<string> SupportedDerivedTraits =
        new HashSet<string>(["Eq", "Ord", "Show", "Hash"], StringComparer.Ordinal);

    private Program ExpandDerivedImplementations(Program program)
    {
        if (!program.TypeDecls.Any(declaration => declaration.Deriving.Count > 0)
            && !program.ZeroCostTypeDecls.Any(declaration => declaration.Deriving.Count > 0))
        {
            return program;
        }

        List<TopLevelItem> items = [];
        foreach (TopLevelItem item in program.Items)
        {
            TypeDecl? sourceDeclaration = GetDerivableTypeDeclaration(item);
            if (sourceDeclaration is null || sourceDeclaration.Deriving.Count == 0)
            {
                items.Add(item);
                continue;
            }
            AddTypeWithoutDeriving(item, items);
            AddDerivedImplementations(sourceDeclaration, items);
        }

        return program with { Items = items };
    }

    private TypeDecl? GetDerivableTypeDeclaration(TopLevelItem item)
    {
        if (item is TopLevelItem.Type ordinaryType)
        {
            return ordinaryType.Decl;
        }
        if (item is not TopLevelItem.ZeroCostType zeroCostType)
        {
            return null;
        }

        TypeDecl declaration = new(
            zeroCostType.Decl.Name,
            zeroCostType.Decl.TypeParameters,
            [zeroCostType.Decl.Constructor])
        {
            Deriving = zeroCostType.Decl.Deriving,
        };
        AstSpans.Set(declaration, GetSpan(zeroCostType.Decl));
        return declaration;
    }

    private static void AddTypeWithoutDeriving(TopLevelItem item, List<TopLevelItem> items)
    {
        if (item is TopLevelItem.Type ordinaryType)
        {
            TypeDecl elaborated = ordinaryType.Decl with { Deriving = [] };
            AstSpans.Set(elaborated, AstSpans.GetOrDefault(ordinaryType.Decl));
            items.Add(new TopLevelItem.Type(elaborated));
            return;
        }

        TopLevelItem.ZeroCostType zeroCostType = (TopLevelItem.ZeroCostType)item;
        ZeroCostTypeDecl zeroCostElaborated = zeroCostType.Decl with { Deriving = [] };
        AstSpans.Set(zeroCostElaborated, AstSpans.GetOrDefault(zeroCostType.Decl));
        items.Add(new TopLevelItem.ZeroCostType(zeroCostElaborated));
    }

    private void AddDerivedImplementations(TypeDecl sourceDeclaration, List<TopLevelItem> items)
    {
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (string writtenTraitName in sourceDeclaration.Deriving)
        {
            string traitName = writtenTraitName.Split('.').Last();
            if (!SupportedDerivedTraits.Contains(traitName))
            {
                ReportDiagnostic(
                    GetSpan(sourceDeclaration),
                    $"Trait '{writtenTraitName}' cannot be derived; supported traits are Eq, Ord, Show, and Hash.",
                    InvalidTraitImplementationDeclarationCode);
                continue;
            }
            if (!seen.Add(traitName))
            {
                ReportDiagnostic(
                    GetSpan(sourceDeclaration),
                    $"Trait '{traitName}' appears more than once in the deriving clause for '{sourceDeclaration.Name}'.",
                    InvalidTraitImplementationDeclarationCode);
                continue;
            }

            TypeDecl declaration = ResolveDerivedTypeDeclaration(sourceDeclaration);
            if (ValidateDerivedFields(declaration, traitName))
            {
                items.Add(new TopLevelItem.Implementation(CreateDerivedImplementation(declaration, traitName)));
            }
        }
    }

    private TypeDecl ResolveDerivedTypeDeclaration(TypeDecl declaration)
    {
        if (!_typeSymbols.TryGetValue(declaration.Name, out TypeSymbol? symbol))
        {
            return declaration;
        }

        TypeDecl resolved = symbol.DeclaringSyntax with { Deriving = declaration.Deriving };
        AstSpans.Set(resolved, GetSpan(declaration));
        return resolved;
    }

    private bool ValidateDerivedFields(TypeDecl declaration, string traitName)
    {
        if (!_typeSymbols.TryGetValue(declaration.Name, out TypeSymbol? symbol))
        {
            return false;
        }

        bool valid = true;
        for (int constructorIndex = 0; constructorIndex < symbol.Constructors.Count; constructorIndex++)
        {
            ConstructorSymbol constructor = symbol.Constructors[constructorIndex];
            for (int fieldIndex = 0; fieldIndex < constructor.ParameterTypes.Count; fieldIndex++)
            {
                if (TryDescribeUnsupportedDerivedType(
                        constructor.ParameterTypes[fieldIndex],
                        symbol,
                        out string? reason))
                {
                    string field = declaration.IsRecord
                        ? $"field '{declaration.Constructors[constructorIndex].FieldNames[fieldIndex]}'"
                        : $"payload {fieldIndex + 1} of constructor '{constructor.Name}'";
                    ReportDiagnostic(
                        GetSpan(declaration.Constructors[constructorIndex]),
                        $"Cannot derive '{traitName}' for '{declaration.Name}': {field} has {reason}.",
                        InvalidTraitImplementationDeclarationCode);
                    valid = false;
                }
            }
        }
        return valid;
    }

    private bool TryDescribeUnsupportedDerivedType(
        TypeRef type,
        TypeSymbol declaringType,
        out string? reason)
    {
        type = Prune(type);
        switch (type)
        {
            case TypeRef.TFun:
                reason = "an unsupported function type";
                return true;
            case TypeRef.TOpaque opaque:
                reason = _externalResourceTypes.ContainsKey(opaque.Name)
                    ? $"the resource type '{opaque.Name}'"
                    : $"the opaque external type '{opaque.Name}'";
                return true;
            case TypeRef.TPtr:
                reason = "an unsupported external pointer type";
                return true;
            case TypeRef.TCapability capability:
                reason = $"the capability type '{capability.Symbol.Name}'";
                return true;
            case TypeRef.TRow:
                reason = "an unsupported capability row";
                return true;
            case TypeRef.TList list:
                return TryDescribeUnsupportedDerivedType(list.Element, declaringType, out reason);
            case TypeRef.TTuple tuple:
                return TryDescribeUnsupportedDerivedTypes(tuple.Elements, declaringType, out reason);
            case TypeRef.TNamedType named when ReferenceEquals(named.Symbol, declaringType):
                if (!IsRegularDerivedRecursion(named, declaringType))
                {
                    reason = "unsupported non-regular recursion";
                    return true;
                }
                reason = null;
                return false;
            case TypeRef.TNamedType named:
                if (string.Equals(named.Symbol.Name, "Task", StringComparison.Ordinal))
                {
                    reason = "the unsupported task type 'Task'";
                    return true;
                }
                if (BuiltinRegistry.IsResourceTypeName(named.Symbol.Name))
                {
                    reason = $"the resource type '{named.Symbol.Name}'";
                    return true;
                }
                return TryDescribeUnsupportedDerivedTypes(named.TypeArgs, declaringType, out reason);
            case TypeRef.TTypeParam parameter when !declaringType.TypeParameters.Contains(parameter.Symbol):
                reason = $"the unbound type variable '{parameter.Symbol.Name}'";
                return true;
            default:
                reason = null;
                return false;
        }
    }

    private bool TryDescribeUnsupportedDerivedTypes(
        IReadOnlyList<TypeRef> types,
        TypeSymbol declaringType,
        out string? reason)
    {
        foreach (TypeRef type in types)
        {
            if (TryDescribeUnsupportedDerivedType(type, declaringType, out reason))
            {
                return true;
            }
        }
        reason = null;
        return false;
    }

    private static bool IsRegularDerivedRecursion(TypeRef.TNamedType occurrence, TypeSymbol declaringType)
    {
        if (occurrence.TypeArgs.Count != declaringType.TypeParameters.Count)
        {
            return false;
        }
        for (int index = 0; index < occurrence.TypeArgs.Count; index++)
        {
            if (occurrence.TypeArgs[index] is not TypeRef.TTypeParam parameter
                || !ReferenceEquals(parameter.Symbol, declaringType.TypeParameters[index]))
            {
                return false;
            }
        }
        return true;
    }

    private TraitImplementationDecl CreateDerivedImplementation(TypeDecl declaration, string traitName)
    {
        TextSpan span = GetSpan(declaration);
        TypeExpr head = declaration.TypeParameters.Count == 0
            ? new TypeExpr.Named(declaration.Name)
            : new TypeExpr.Applied(
                declaration.Name,
                declaration.TypeParameters.Select(parameter =>
                    (TypeExpr)new TypeExpr.Named(parameter.Name)).ToArray());
        HashSet<string> usedParameters = CollectDerivedTypeParameters(declaration);
        TraitConstraintSyntax[] requirements = declaration.TypeParameters
            .Where(parameter => usedParameters.Contains(parameter.Name))
            .Select(parameter => new TraitConstraintSyntax(
                StandardDerivedTraitName(traitName),
                [(TypeExpr)new TypeExpr.Named(parameter.Name)]))
            .ToArray();
        foreach (TraitConstraintSyntax requirement in requirements)
        {
            AstSpans.Set(requirement, span);
        }

        string methodName = traitName switch
        {
            "Eq" => "equal",
            "Ord" => "compare",
            "Show" => "show",
            "Hash" => "hash",
            _ => throw new InvalidOperationException($"Unsupported derived trait '{traitName}'."),
        };
        Expr implementation = traitName switch
        {
            "Eq" => CreateDerivedEqBody(declaration),
            "Ord" => CreateDerivedOrdBody(declaration),
            "Show" => CreateDerivedShowBody(declaration),
            "Hash" => CreateDerivedHashBody(declaration),
            _ => throw new InvalidOperationException($"Unsupported derived trait '{traitName}'."),
        };
        SetDerivedExpressionSpans(implementation, span);

        TraitImplementationMethodBinding binding = new(methodName, implementation);
        AstSpans.Set(binding, span);
        TraitImplementationDecl result = new(
            StandardDerivedTraitName(traitName),
            [head],
            requirements,
            [binding]);
        AstSpans.Set(result, span);
        return result;
    }

    private static string StandardDerivedTraitName(string traitName) => $"Ashes.Trait.{traitName}";

    private static HashSet<string> CollectDerivedTypeParameters(TypeDecl declaration)
    {
        HashSet<string> declared = declaration.TypeParameters
            .Select(parameter => parameter.Name)
            .ToHashSet(StringComparer.Ordinal);
        HashSet<string> used = new(StringComparer.Ordinal);
        foreach (TypeConstructor constructor in declaration.Constructors)
        {
            foreach (TypeExpr parameter in constructor.Parameters)
            {
                foreach (string name in parameter.MentionedNames())
                {
                    if (declared.Contains(name))
                    {
                        used.Add(name);
                    }
                }
            }
        }
        return used;
    }

    private Expr CreateDerivedEqBody(TypeDecl declaration)
    {
        Expr left = new Expr.Var("__derived_left");
        Expr right = new Expr.Var("__derived_right");
        List<MatchCase> cases = [];
        for (int index = 0; index < declaration.Constructors.Count; index++)
        {
            TypeConstructor constructor = declaration.Constructors[index];
            (Pattern leftPattern, string[] leftFields) = CreateDerivedConstructorPattern(
                constructor,
                $"__derived_left_{index}",
                bindFields: true);
            (Pattern rightPattern, string[] rightFields) = CreateDerivedConstructorPattern(
                constructor,
                $"__derived_right_{index}",
                bindFields: true);
            Expr result = new Expr.BoolLit(true);
            for (int field = leftFields.Length - 1; field >= 0; field--)
            {
                result = new Expr.If(
                    CreateDerivedTraitCall("Eq", "equal", new Expr.Var(leftFields[field]), new Expr.Var(rightFields[field])),
                    result,
                    new Expr.BoolLit(false));
            }
            cases.Add(new MatchCase(new Pattern.Tuple([leftPattern, rightPattern]), result));
        }
        cases.Add(new MatchCase(new Pattern.Wildcard(), new Expr.BoolLit(false)));
        Expr body = new Expr.Match(new Expr.TupleLit([left, right]), cases);
        return new Expr.Lambda("__derived_left", new Expr.Lambda("__derived_right", body));
    }

    private Expr CreateDerivedOrdBody(TypeDecl declaration)
    {
        Expr left = new Expr.Var("__derived_left");
        Expr right = new Expr.Var("__derived_right");
        List<MatchCase> cases = [];
        for (int leftIndex = 0; leftIndex < declaration.Constructors.Count; leftIndex++)
        {
            for (int rightIndex = 0; rightIndex < declaration.Constructors.Count; rightIndex++)
            {
                TypeConstructor leftConstructor = declaration.Constructors[leftIndex];
                TypeConstructor rightConstructor = declaration.Constructors[rightIndex];
                bool same = leftIndex == rightIndex;
                (Pattern leftPattern, string[] leftFields) = CreateDerivedConstructorPattern(
                    leftConstructor,
                    $"__derived_left_{leftIndex}",
                    bindFields: same);
                (Pattern rightPattern, string[] rightFields) = CreateDerivedConstructorPattern(
                    rightConstructor,
                    $"__derived_right_{rightIndex}",
                    bindFields: same);
                Expr result;
                if (!same)
                {
                    result = new Expr.Var(leftIndex < rightIndex ? "Less" : "Greater");
                }
                else
                {
                    result = new Expr.Var("Equal");
                    for (int field = leftFields.Length - 1; field >= 0; field--)
                    {
                        string ordering = $"__derived_ordering_{field}";
                        result = new Expr.Match(
                            CreateDerivedTraitCall(
                                "Ord",
                                "compare",
                                new Expr.Var(leftFields[field]),
                                new Expr.Var(rightFields[field])),
                            [
                                new MatchCase(new Pattern.Constructor("Equal", []), result),
                                new MatchCase(new Pattern.Var(ordering), new Expr.Var(ordering)),
                            ]);
                    }
                }
                cases.Add(new MatchCase(new Pattern.Tuple([leftPattern, rightPattern]), result));
            }
        }
        Expr body = new Expr.Match(new Expr.TupleLit([left, right]), cases);
        return new Expr.Lambda("__derived_left", new Expr.Lambda("__derived_right", body));
    }

    private Expr CreateDerivedShowBody(TypeDecl declaration)
    {
        List<MatchCase> cases = [];
        for (int index = 0; index < declaration.Constructors.Count; index++)
        {
            TypeConstructor constructor = declaration.Constructors[index];
            (Pattern pattern, string[] fields) = CreateDerivedConstructorPattern(
                constructor,
                $"__derived_field_{index}",
                bindFields: true);
            Expr result = new Expr.StrLit(constructor.Name);
            if (fields.Length > 0)
            {
                result = new Expr.StrLit($"{constructor.Name}(");
                for (int field = 0; field < fields.Length; field++)
                {
                    string prefix = declaration.IsRecord
                        ? $"{constructor.FieldNames[field]} = "
                        : field == 0 ? "" : ", ";
                    if (declaration.IsRecord && field > 0)
                    {
                        prefix = $", {prefix}";
                    }
                    result = new Expr.Add(result, new Expr.StrLit(prefix));
                    result = new Expr.Add(
                        result,
                        CreateDerivedTraitCall("Show", "show", new Expr.Var(fields[field])));
                }
                result = new Expr.Add(result, new Expr.StrLit(")"));
            }
            cases.Add(new MatchCase(pattern, result));
        }
        return new Expr.Lambda(
            "__derived_value",
            new Expr.Match(new Expr.Var("__derived_value"), cases));
    }

    private Expr CreateDerivedHashBody(TypeDecl declaration)
    {
        List<MatchCase> cases = [];
        for (int index = 0; index < declaration.Constructors.Count; index++)
        {
            TypeConstructor constructor = declaration.Constructors[index];
            (Pattern pattern, string[] fields) = CreateDerivedConstructorPattern(
                constructor,
                $"__derived_field_{index}",
                bindFields: true);
            Expr result = new Expr.IntLit(index + 1);
            foreach (string field in fields)
            {
                result = new Expr.Add(
                    new Expr.Multiply(result, new Expr.IntLit(16777619)),
                    CreateDerivedTraitCall("Hash", "hash", new Expr.Var(field)));
            }
            cases.Add(new MatchCase(pattern, result));
        }
        return new Expr.Lambda(
            "__derived_value",
            new Expr.Match(new Expr.Var("__derived_value"), cases));
    }

    private static (Pattern Pattern, string[] Fields) CreateDerivedConstructorPattern(
        TypeConstructor constructor,
        string prefix,
        bool bindFields)
    {
        string[] fields = bindFields
            ? Enumerable.Range(0, constructor.Parameters.Count)
                .Select(index => $"{prefix}_{index}")
                .ToArray()
            : [];
        Pattern[] patterns = bindFields
            ? fields.Select(name => (Pattern)new Pattern.Var(name)).ToArray()
            : Enumerable.Range(0, constructor.Parameters.Count)
                .Select(_ => (Pattern)new Pattern.Wildcard())
                .ToArray();
        return (new Pattern.Constructor(constructor.Name, patterns), fields);
    }

    private static Expr CreateDerivedTraitCall(string trait, string method, params Expr[] arguments)
    {
        Expr result = new Expr.QualifiedVar(StandardDerivedTraitName(trait), method);
        foreach (Expr argument in arguments)
        {
            result = new Expr.Call(result, argument);
        }
        return result;
    }

    private void SetDerivedExpressionSpans(Expr expression, TextSpan span)
    {
        void VisitPattern(Pattern pattern)
        {
            AstSpans.Set(pattern, span);
            switch (pattern)
            {
                case Pattern.Cons cons:
                    VisitPattern(cons.Head);
                    VisitPattern(cons.Tail);
                    break;
                case Pattern.Tuple tuple:
                    foreach (Pattern element in tuple.Elements) VisitPattern(element);
                    break;
                case Pattern.Constructor constructor:
                    foreach (Pattern child in constructor.Patterns) VisitPattern(child);
                    break;
            }
        }

        void Visit(Expr current)
        {
            AstSpans.Set(current, span);
            switch (current)
            {
                case Expr.Lambda lambda:
                    AstSpans.SetLambdaParameter(lambda, span);
                    break;
                case Expr.Match match:
                    foreach (MatchCase matchCase in match.Cases)
                    {
                        VisitPattern(matchCase.Pattern);
                    }
                    break;
            }
            _ = MapChildExpressions(current, child =>
            {
                Visit(child);
                return child;
            });
        }

        Visit(expression);
    }
}
