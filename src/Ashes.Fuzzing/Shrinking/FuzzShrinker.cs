using Ashes.Frontend;
using Ashes.Fuzzing.Coverage;
using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Shrinking;

using AshesFormatter = Ashes.Formatter.Formatter;
using FrontendProgram = Ashes.Frontend.Program;

internal sealed record ShrinkResult(GeneratedFuzzCase Case, int Attempts, int Accepted, TimeSpan Duration);

internal sealed class FuzzShrinker
{
    internal async Task<ShrinkResult> ShrinkAsync(
        GeneratedFuzzCase original,
        Func<GeneratedFuzzCase, CancellationToken, ValueTask<bool>> stillFails,
        int maximumAttempts,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        DateTimeOffset started = DateTimeOffset.UtcNow;
        GeneratedFuzzCase current = original;
        int attempts = 0;
        int accepted = 0;
        while (attempts < maximumAttempts && DateTimeOffset.UtcNow - started < timeout)
        {
            bool changed = false;
            foreach (GeneratedFuzzCase candidate in Candidates(current))
            {
                attempts++;
                if (StableSizeMetric.Measure(candidate) >= StableSizeMetric.Measure(current))
                {
                    continue;
                }
                if (await stillFails(candidate, cancellationToken).ConfigureAwait(false))
                {
                    current = candidate;
                    accepted++;
                    changed = true;
                    break;
                }
                if (attempts >= maximumAttempts)
                {
                    break;
                }
            }
            if (!changed)
            {
                break;
            }
        }
        return new ShrinkResult(current, attempts, accepted, DateTimeOffset.UtcNow - started);
    }

    internal IEnumerable<GeneratedFuzzCase> Candidates(GeneratedFuzzCase source)
    {
        HashSet<string> sources = new(StringComparer.Ordinal);
        int originalMetric = StableSizeMetric.Measure(source);
        foreach (GeneratedFuzzCase candidate in UnfilteredCandidates(source))
        {
            if (StableSizeMetric.Measure(candidate) < originalMetric && sources.Add(candidate.Source))
            {
                yield return candidate;
            }
        }
    }

    private static IEnumerable<GeneratedFuzzCase> UnfilteredCandidates(GeneratedFuzzCase source)
    {
        Dictionary<string, AshesType> topLevelScope = TopLevelScope(source.Program.Items);
        foreach (Expr expression in ExpressionCandidates(source.Program.Body, source.Type, topLevelScope))
        {
            yield return CreateCandidate(source, source.Program with { Body = expression }, "shrink:body");
        }

        for (int index = 0; index < source.Program.Items.Count; index++)
        {
            TopLevelItem item = source.Program.Items[index];
            if (item is TopLevelItem.Type typeItem)
            {
                string[] declaredNames = [typeItem.Decl.Name, .. typeItem.Decl.Constructors.Select(constructor => constructor.Name)];
                if (!ReferencesAnyIdentifierAfter(source.Program, index, declaredNames))
                {
                    TopLevelItem[] withoutType = source.Program.Items.Where((_, itemIndex) => itemIndex != index).ToArray();
                    yield return CreateCandidate(source, source.Program with { Items = withoutType }, "shrink:type-remove");
                }
                else if (!typeItem.Decl.IsRecord && typeItem.Decl.Constructors.Count > 1)
                {
                    foreach (TypeConstructor constructor in typeItem.Decl.Constructors)
                    {
                        if (ReferencesAnyIdentifierAfter(source.Program, index, [constructor.Name]))
                        {
                            continue;
                        }

                        TypeConstructor[] constructors = typeItem.Decl.Constructors
                            .Where(candidate => !ReferenceEquals(candidate, constructor))
                            .ToArray();
                        TopLevelItem[] items = [.. source.Program.Items];
                        items[index] = typeItem with { Decl = typeItem.Decl with { Constructors = constructors } };
                        yield return CreateCandidate(source, source.Program with { Items = items }, "shrink:constructor-remove");
                    }
                }
            }
            if (CanRemove(source.Program, index))
            {
                TopLevelItem[] withoutItem = source.Program.Items.Where((_, itemIndex) => itemIndex != index).ToArray();
                yield return CreateCandidate(source, source.Program with { Items = withoutItem }, "shrink:declaration-remove");
            }

            if (item is TopLevelItem.LetDecl binding && TryExpressionType(binding.Value, binding.TypeAnnotation, out AshesType bindingType))
            {
                Dictionary<string, AshesType> declarationScope = ScopeBefore(source.Program.Items, index);
                if (binding.IsRecursive)
                {
                    declarationScope[binding.Name] = bindingType;
                }
                foreach (Expr value in ExpressionCandidates(binding.Value, bindingType, declarationScope))
                {
                    TopLevelItem[] items = [.. source.Program.Items];
                    items[index] = binding with { Value = value };
                    yield return CreateCandidate(source, source.Program with { Items = items }, "shrink:top-level-value");
                }
            }
            else if (item is TopLevelItem.RecursiveGroup group)
            {
                Dictionary<string, AshesType> declarationScope = ScopeBefore(source.Program.Items, index);
                Dictionary<string, AshesType> groupTypes = [];
                foreach ((string name, Expr value) in group.Bindings)
                {
                    if (TryExpressionType(value, null, out AshesType type))
                    {
                        groupTypes[name] = type;
                        declarationScope[name] = type;
                    }
                }
                for (int bindingIndex = 0; bindingIndex < group.Bindings.Count; bindingIndex++)
                {
                    (string name, Expr value) = group.Bindings[bindingIndex];
                    if (!groupTypes.TryGetValue(name, out AshesType? type))
                    {
                        continue;
                    }
                    foreach (Expr shrunk in ExpressionCandidates(value, type, declarationScope))
                    {
                        if (value is Expr.Lambda && shrunk is not Expr.Lambda)
                        {
                            continue;
                        }
                        (string Name, Expr Value)[] bindings = [.. group.Bindings];
                        bindings[bindingIndex] = (name, shrunk);
                        TopLevelItem[] items = [.. source.Program.Items];
                        items[index] = group with { Bindings = bindings };
                        yield return CreateCandidate(source, source.Program with { Items = items }, "shrink:recursive-function");
                    }
                }
            }
        }
    }

    private static GeneratedFuzzCase CreateCandidate(GeneratedFuzzCase source, FrontendProgram program, string trace)
    {
        string formatted = AshesFormatter.Format(program);
        int nodes = AstCoverageMetrics.Measure(program).Nodes;
        return source with
        {
            Program = program,
            Source = formatted,
            NodeCount = nodes,
            Trace = source.Trace.Append(trace),
        };
    }

    private static IEnumerable<Expr> ExpressionCandidates(
        Expr expression,
        AshesType type,
        IReadOnlyDictionary<string, AshesType> scope)
    {
        if (TryLeaf(type, out Expr literal) && literal != expression)
        {
            yield return literal;
        }
        foreach ((string name, AshesType variableType) in scope.OrderBy(binding => binding.Key, StringComparer.Ordinal))
        {
            if (variableType == type &&
                (expression is not Expr.Var current || !string.Equals(current.Name, name, StringComparison.Ordinal)))
            {
                yield return new Expr.Var(name);
            }
        }

        switch (expression)
        {
            case Expr.If conditional:
                yield return conditional.Then;
                yield return conditional.Else;
                foreach (Expr child in ExpressionCandidates(conditional.Cond, AshesType.Bool, scope))
                {
                    yield return conditional with { Cond = child };
                }
                foreach (Expr child in ExpressionCandidates(conditional.Then, type, scope))
                {
                    yield return conditional with { Then = child };
                }
                foreach (Expr child in ExpressionCandidates(conditional.Else, type, scope))
                {
                    yield return conditional with { Else = child };
                }
                break;
            case Expr.Let let:
                if (!ReferencesFreeName(let.Body, let.Name))
                {
                    yield return let.Body;
                }
                if (FreeVariables(let.Value).Count == 0)
                {
                    Expr inlined = Substitute(let.Body, let.Name, let.Value);
                    if (!ReferencesFreeName(inlined, let.Name))
                    {
                        yield return inlined;
                    }
                }
                if (TryExpressionType(let.Value, let.TypeAnnotation, out AshesType boundType))
                {
                    foreach (Expr child in ExpressionCandidates(let.Value, boundType, scope))
                    {
                        yield return let with { Value = child };
                    }
                    Dictionary<string, AshesType> bodyScope = new(scope, StringComparer.Ordinal)
                    {
                        [let.Name] = boundType,
                    };
                    foreach (Expr child in ExpressionCandidates(let.Body, type, bodyScope))
                    {
                        yield return let with { Body = child };
                    }
                }
                else
                {
                    foreach (Expr child in ExpressionCandidates(let.Body, type, scope))
                    {
                        yield return let with { Body = child };
                    }
                }
                break;
            case Expr.LetRecursive recursive when TryExpressionType(recursive.Value, recursive.TypeAnnotation, out AshesType recursiveType):
                Dictionary<string, AshesType> recursiveScope = new(scope, StringComparer.Ordinal)
                {
                    [recursive.Name] = recursiveType,
                };
                foreach (Expr child in ExpressionCandidates(recursive.Value, recursiveType, recursiveScope))
                {
                    yield return recursive with { Value = child };
                }
                foreach (Expr child in ExpressionCandidates(recursive.Body, type, recursiveScope))
                {
                    yield return recursive with { Body = child };
                }
                break;
            case Expr.Lambda lambda when type is AshesType.Function function:
                Dictionary<string, AshesType> lambdaScope = new(scope, StringComparer.Ordinal)
                {
                    [lambda.ParamName] = function.Parameter,
                };
                foreach (Expr child in ExpressionCandidates(lambda.Body, function.Return, lambdaScope))
                {
                    yield return lambda with { Body = child };
                }
                break;
            case Expr.TupleLit tuple when type is AshesType.Tuple tupleType && tuple.Elements.Count == tupleType.Elements.Count:
                for (int index = 0; index < tuple.Elements.Count; index++)
                {
                    foreach (Expr child in ExpressionCandidates(tuple.Elements[index], tupleType.Elements[index], scope))
                    {
                        Expr[] elements = [.. tuple.Elements];
                        elements[index] = child;
                        yield return new Expr.TupleLit(elements);
                    }
                }
                break;
            case Expr.ListLit list when type is AshesType.List listType:
                if (list.Elements.Count > 0)
                {
                    yield return new Expr.ListLit(list.Elements.Take(list.Elements.Count - 1).ToArray());
                }
                for (int index = 0; index < list.Elements.Count; index++)
                {
                    foreach (Expr child in ExpressionCandidates(list.Elements[index], listType.Element, scope))
                    {
                        Expr[] elements = [.. list.Elements];
                        elements[index] = child;
                        yield return new Expr.ListLit(elements);
                    }
                }
                break;
            case Expr.RecordLit record when type is AshesType.Record { Name: "FuzzRecord" }:
                AshesType[] fieldTypes = [AshesType.Int, AshesType.Bool];
                for (int index = 0; index < record.Fields.Count && index < fieldTypes.Length; index++)
                {
                    foreach (Expr child in ExpressionCandidates(record.Fields[index].Value, fieldTypes[index], scope))
                    {
                        (string Name, Expr Value)[] fields = [.. record.Fields];
                        fields[index] = (fields[index].Name, child);
                        yield return new Expr.RecordLit(record.TypeName, fields);
                    }
                }
                break;
            case Expr.Match match:
                int wildcardIndex = -1;
                for (int index = 0; index < match.Cases.Count; index++)
                {
                    if (match.Cases[index].Guard is null && match.Cases[index].Pattern is Pattern.Wildcard)
                    {
                        wildcardIndex = index;
                        break;
                    }
                }
                if (wildcardIndex >= 0)
                {
                    for (int index = 0; index < wildcardIndex; index++)
                    {
                        if (match.Cases[index].Guard is null && EquivalentExpression(
                            match.Cases[index].Body,
                            match.Cases[wildcardIndex].Body))
                        {
                            yield return match with
                            {
                                Cases = match.Cases.Where((_, caseIndex) => caseIndex != index).ToArray(),
                            };
                        }
                    }
                }
                for (int index = 0; index < match.Cases.Count; index++)
                {
                    MatchCase matchCase = match.Cases[index];
                    foreach (Expr child in ExpressionCandidates(matchCase.Body, type, scope))
                    {
                        MatchCase[] cases = [.. match.Cases];
                        cases[index] = matchCase with { Body = child };
                        yield return match with { Cases = cases };
                    }
                }
                break;
            case Expr.Cons cons when type is AshesType.List listType:
                yield return cons.Tail;
                foreach (Expr child in ExpressionCandidates(cons.Head, listType.Element, scope))
                {
                    yield return cons with { Head = child };
                }
                foreach (Expr child in ExpressionCandidates(cons.Tail, type, scope))
                {
                    yield return cons with { Tail = child };
                }
                break;
            case Expr.Add add:
                foreach (Expr child in ExpressionCandidates(add.Left, type, scope))
                {
                    yield return add with { Left = child };
                }
                foreach (Expr child in ExpressionCandidates(add.Right, type, scope))
                {
                    yield return add with { Right = child };
                }
                break;
            case Expr.Subtract subtract:
                foreach (Expr child in ExpressionCandidates(subtract.Left, type, scope))
                {
                    yield return subtract with { Left = child };
                }
                foreach (Expr child in ExpressionCandidates(subtract.Right, type, scope))
                {
                    yield return subtract with { Right = child };
                }
                break;
            case Expr.Multiply multiply:
                foreach (Expr child in ExpressionCandidates(multiply.Left, type, scope))
                {
                    yield return multiply with { Left = child };
                }
                foreach (Expr child in ExpressionCandidates(multiply.Right, type, scope))
                {
                    yield return multiply with { Right = child };
                }
                break;
            case Expr.IntLit integer when integer.Value != 0:
                yield return new Expr.IntLit(integer.Value / 2);
                break;
            case Expr.BigIntLit integer when !string.Equals(integer.Digits, "0", StringComparison.Ordinal):
                yield return new Expr.BigIntLit("0");
                break;
            case Expr.UIntLit integer when integer.Value != 0:
                yield return integer with { Value = integer.Value / 2 };
                break;
            case Expr.StrLit text when text.Value.Length > 0:
                yield return new Expr.StrLit(text.Value[..(text.Value.Length / 2)]);
                break;
        }
    }

    private static Dictionary<string, AshesType> TopLevelScope(IReadOnlyList<TopLevelItem> items) => ScopeBefore(items, items.Count);

    private static Dictionary<string, AshesType> ScopeBefore(IReadOnlyList<TopLevelItem> items, int end)
    {
        Dictionary<string, AshesType> scope = new(StringComparer.Ordinal);
        for (int index = 0; index < end; index++)
        {
            switch (items[index])
            {
                case TopLevelItem.LetDecl binding when TryExpressionType(binding.Value, binding.TypeAnnotation, out AshesType type):
                    scope[binding.Name] = type;
                    break;
                case TopLevelItem.RecursiveGroup group:
                    foreach ((string name, Expr value) in group.Bindings)
                    {
                        if (TryExpressionType(value, null, out AshesType recursiveType))
                        {
                            scope[name] = recursiveType;
                        }
                    }
                    break;
            }
        }
        return scope;
    }

    private static bool CanRemove(FrontendProgram program, int index)
    {
        string[] names = program.Items[index] switch
        {
            TopLevelItem.LetDecl binding => [binding.Name],
            TopLevelItem.RecursiveGroup group => group.Bindings.Select(binding => binding.Name).ToArray(),
            _ => [],
        };
        if (names.Length == 0)
        {
            return false;
        }
        IEnumerable<Expr> laterExpressions = program.Items.Skip(index + 1).SelectMany(ItemExpressions).Append(program.Body);
        return names.All(name => laterExpressions.All(expression => !ReferencesFreeName(expression, name)));
    }

    private static bool ReferencesAnyIdentifierAfter(
        FrontendProgram program,
        int index,
        IReadOnlyCollection<string> names)
    {
        FrontendProgram later = new(program.Items.Skip(index + 1).ToArray(), program.Body);
        string source = AshesFormatter.Format(later);
        int position = 0;
        while (position < source.Length)
        {
            if (!char.IsLetter(source[position]) && source[position] != '_')
            {
                position++;
                continue;
            }

            int end = position + 1;
            while (end < source.Length && (char.IsLetterOrDigit(source[end]) || source[end] == '_'))
            {
                end++;
            }
            if (names.Contains(source[position..end], StringComparer.Ordinal))
            {
                return true;
            }
            position = end;
        }
        return false;
    }

    private static bool EquivalentExpression(Expr left, Expr right) => string.Equals(
        AshesFormatter.Format(new FrontendProgram(Array.Empty<TopLevelItem>(), left)),
        AshesFormatter.Format(new FrontendProgram(Array.Empty<TopLevelItem>(), right)),
        StringComparison.Ordinal);

    private static IEnumerable<Expr> ItemExpressions(TopLevelItem item) => item switch
    {
        TopLevelItem.LetDecl binding => [binding.Value],
        TopLevelItem.RecursiveGroup group => group.Bindings.Select(binding => binding.Value),
        TopLevelItem.Provide provide => provide.Decl.Bindings.Select(binding => binding.Implementation),
        _ => [],
    };

    private static bool TryExpressionType(Expr expression, TypeExpr? annotation, out AshesType type)
    {
        AshesType? inferred = annotation is null ? null : FromSyntax(annotation);
        inferred ??= expression switch
        {
            Expr.IntLit => AshesType.Int,
            Expr.BoolLit => AshesType.Bool,
            Expr.StrLit => AshesType.Str,
            Expr.FloatLit => AshesType.Float,
            Expr.BigIntLit => AshesType.BigInt,
            Expr.UIntLit unsigned => new AshesType.UInt(unsigned.Bits),
            Expr.Lambda { ParamAnnotation: not null } lambda when TryExpressionType(lambda.Body, null, out AshesType bodyType) =>
                new AshesType.Function(FromSyntax(lambda.ParamAnnotation)!, bodyType),
            Expr.If conditional when TryExpressionType(conditional.Then, null, out AshesType branchType) => branchType,
            _ => null,
        };
        if (inferred is null)
        {
            type = null!;
            return false;
        }
        type = inferred;
        return true;
    }

    private static AshesType? FromSyntax(TypeExpr syntax) => syntax switch
    {
        TypeExpr.Named { Name: "Int" } => AshesType.Int,
        TypeExpr.Named { Name: "Bool" } => AshesType.Bool,
        TypeExpr.Named { Name: "Str" } => AshesType.Str,
        TypeExpr.Named { Name: "Float" } => AshesType.Float,
        TypeExpr.Named { Name: "BigInt" } => AshesType.BigInt,
        TypeExpr.Named named when named.Name.Length > 1 && named.Name[0] == 'u' && int.TryParse(named.Name[1..], System.Globalization.CultureInfo.InvariantCulture, out int bits) => new AshesType.UInt(bits),
        TypeExpr.Named named => new AshesType.Record(named.Name),
        TypeExpr.TupleType tuple => new AshesType.Tuple(tuple.Elements.Select(FromSyntax).OfType<AshesType>().ToArray()),
        TypeExpr.Arrow arrow when FromSyntax(arrow.From) is AshesType parameter && FromSyntax(arrow.To) is AshesType result => new AshesType.Function(parameter, result),
        TypeExpr.Applied { Name: "List", Args.Count: 1 } applied when FromSyntax(applied.Args[0]) is AshesType element => new AshesType.List(element),
        TypeExpr.Applied { Name: "Result", Args.Count: 2 } applied when FromSyntax(applied.Args[0]) is AshesType error && FromSyntax(applied.Args[1]) is AshesType value => new AshesType.Result(error, value),
        TypeExpr.Applied { Name: "Task", Args.Count: 2 } applied when FromSyntax(applied.Args[0]) is AshesType error && FromSyntax(applied.Args[1]) is AshesType value => new AshesType.Task(error, value),
        TypeExpr.Applied applied => new AshesType.Adt(applied.Name, applied.Args.Select(FromSyntax).OfType<AshesType>().ToArray()),
        _ => null,
    };

    private static bool ReferencesFreeName(Expr expression, string name) => FreeVariables(expression).Contains(name);

    private static HashSet<string> FreeVariables(Expr expression)
    {
        HashSet<string> result = new(StringComparer.Ordinal);
        VisitFreeVariables(expression, new HashSet<string>(StringComparer.Ordinal), result);
        return result;
    }

    private static void VisitFreeVariables(Expr expression, HashSet<string> bound, HashSet<string> result)
    {
        switch (expression)
        {
            case Expr.Var variable:
                if (!bound.Contains(variable.Name))
                {
                    result.Add(variable.Name);
                }
                return;
            case Expr.Let let:
                VisitFreeVariables(let.Value, bound, result);
                VisitFreeVariables(let.Body, Add(bound, let.Name), result);
                return;
            case Expr.LetRecursive recursive:
                HashSet<string> recursiveBound = Add(bound, recursive.Name);
                VisitFreeVariables(recursive.Value, recursiveBound, result);
                VisitFreeVariables(recursive.Body, recursiveBound, result);
                return;
            case Expr.Lambda lambda:
                VisitFreeVariables(lambda.Body, Add(bound, lambda.ParamName), result);
                return;
            case Expr.Match match:
                VisitFreeVariables(match.Value, bound, result);
                foreach (MatchCase matchCase in match.Cases)
                {
                    HashSet<string> caseBound = new(bound, StringComparer.Ordinal);
                    AddPatternNames(matchCase.Pattern, caseBound);
                    if (matchCase.Guard is not null)
                    {
                        VisitFreeVariables(matchCase.Guard, caseBound, result);
                    }
                    VisitFreeVariables(matchCase.Body, caseBound, result);
                }
                return;
        }
        foreach (Expr child in Children(expression))
        {
            VisitFreeVariables(child, bound, result);
        }
    }

    private static Expr Substitute(Expr expression, string name, Expr replacement) => expression switch
    {
        Expr.Var variable when string.Equals(variable.Name, name, StringComparison.Ordinal) => replacement,
        Expr.Let let => let with
        {
            Value = Substitute(let.Value, name, replacement),
            Body = string.Equals(let.Name, name, StringComparison.Ordinal) ? let.Body : Substitute(let.Body, name, replacement),
        },
        Expr.LetRecursive recursive when string.Equals(recursive.Name, name, StringComparison.Ordinal) => recursive,
        Expr.LetRecursive recursive => recursive with { Value = Substitute(recursive.Value, name, replacement), Body = Substitute(recursive.Body, name, replacement) },
        Expr.Lambda lambda when string.Equals(lambda.ParamName, name, StringComparison.Ordinal) => lambda,
        Expr.Lambda lambda => lambda with { Body = Substitute(lambda.Body, name, replacement) },
        Expr.If conditional => conditional with { Cond = Substitute(conditional.Cond, name, replacement), Then = Substitute(conditional.Then, name, replacement), Else = Substitute(conditional.Else, name, replacement) },
        Expr.Call call => call with { Func = Substitute(call.Func, name, replacement), Arg = Substitute(call.Arg, name, replacement) },
        Expr.TupleLit tuple => tuple with { Elements = tuple.Elements.Select(element => Substitute(element, name, replacement)).ToArray() },
        Expr.ListLit list => list with { Elements = list.Elements.Select(element => Substitute(element, name, replacement)).ToArray() },
        Expr.Cons cons => cons with { Head = Substitute(cons.Head, name, replacement), Tail = Substitute(cons.Tail, name, replacement) },
        Expr.Add binary => binary with { Left = Substitute(binary.Left, name, replacement), Right = Substitute(binary.Right, name, replacement) },
        Expr.Subtract binary => binary with { Left = Substitute(binary.Left, name, replacement), Right = Substitute(binary.Right, name, replacement) },
        Expr.Multiply binary => binary with { Left = Substitute(binary.Left, name, replacement), Right = Substitute(binary.Right, name, replacement) },
        Expr.Divide binary => binary with { Left = Substitute(binary.Left, name, replacement), Right = Substitute(binary.Right, name, replacement) },
        Expr.Modulo binary => binary with { Left = Substitute(binary.Left, name, replacement), Right = Substitute(binary.Right, name, replacement) },
        Expr.RecordLit record => record with { Fields = record.Fields.Select(field => (field.Name, Substitute(field.Value, name, replacement))).ToArray() },
        Expr.RecordUpdate update => update with { Target = Substitute(update.Target, name, replacement), Updates = update.Updates.Select(field => (field.Name, Substitute(field.Value, name, replacement))).ToArray() },
        _ => expression,
    };

    private static IEnumerable<Expr> Children(Expr expression) => expression switch
    {
        Expr.Add binary => [binary.Left, binary.Right],
        Expr.Subtract binary => [binary.Left, binary.Right],
        Expr.Multiply binary => [binary.Left, binary.Right],
        Expr.Divide binary => [binary.Left, binary.Right],
        Expr.Modulo binary => [binary.Left, binary.Right],
        Expr.BitwiseAnd binary => [binary.Left, binary.Right],
        Expr.BitwiseOr binary => [binary.Left, binary.Right],
        Expr.BitwiseXor binary => [binary.Left, binary.Right],
        Expr.ShiftLeft binary => [binary.Left, binary.Right],
        Expr.ShiftRight binary => [binary.Left, binary.Right],
        Expr.BitwiseNot unary => [unary.Operand],
        Expr.GreaterThan binary => [binary.Left, binary.Right],
        Expr.LessThan binary => [binary.Left, binary.Right],
        Expr.GreaterOrEqual binary => [binary.Left, binary.Right],
        Expr.LessOrEqual binary => [binary.Left, binary.Right],
        Expr.Equal binary => [binary.Left, binary.Right],
        Expr.NotEqual binary => [binary.Left, binary.Right],
        Expr.ResultPipe pipe => [pipe.Left, pipe.Right],
        Expr.ResultMapErrorPipe pipe => [pipe.Left, pipe.Right],
        Expr.LetResult let => [let.Value, let.Body],
        Expr.Await awaitExpression => [awaitExpression.Task],
        Expr.Perform perform => [perform.Operation],
        Expr.Handle handle => [handle.Body, .. handle.Arms.Select(arm => arm.Body)],
        _ => [],
    };

    private static HashSet<string> Add(HashSet<string> scope, string name) => new(scope, StringComparer.Ordinal) { name };

    private static void AddPatternNames(Pattern pattern, HashSet<string> names)
    {
        switch (pattern)
        {
            case Pattern.Var variable:
                names.Add(variable.Name);
                break;
            case Pattern.Constructor constructor:
                foreach (Pattern child in constructor.Patterns)
                {
                    AddPatternNames(child, names);
                }
                break;
            case Pattern.Tuple tuple:
                foreach (Pattern child in tuple.Elements)
                {
                    AddPatternNames(child, names);
                }
                break;
            case Pattern.Cons cons:
                AddPatternNames(cons.Head, names);
                AddPatternNames(cons.Tail, names);
                break;
        }
    }

    private static bool TryLeaf(AshesType type, out Expr expression)
    {
        Expr? candidate = type switch
        {
            AshesType.Primitive { Name: "Int" } => new Expr.IntLit(0),
            AshesType.Primitive { Name: "Bool" } => new Expr.BoolLit(false),
            AshesType.Primitive { Name: "Str" } => new Expr.StrLit(""),
            AshesType.Primitive { Name: "Float" } => new Expr.FloatLit(0.0),
            AshesType.Primitive { Name: "BigInt" } => new Expr.BigIntLit("0"),
            AshesType.UInt unsigned => new Expr.UIntLit(0, unsigned.Bits),
            AshesType.List => new Expr.ListLit([]),
            AshesType.Tuple tuple when TryLeaves(tuple.Elements, out Expr[] elements) => new Expr.TupleLit(elements),
            AshesType.Function function when TryLeaf(function.Return, out Expr body) =>
                new Expr.Lambda("shrunk", body) { ParamAnnotation = function.Parameter.ToSyntax() },
            AshesType.Record { Name: "FuzzRecord" } => new Expr.RecordLit("FuzzRecord", [("first", new Expr.IntLit(0)), ("second", new Expr.BoolLit(false))]),
            AshesType.Result result when TryLeaf(result.Value, out Expr value) => new Expr.Call(new Expr.Var("Ok"), value),
            AshesType.Adt { Name: "FuzzTree" } => new Expr.Var("FuzzEmpty"),
            AshesType.Adt { Name: "FuzzMaybe" } => new Expr.Var("FuzzNone"),
            AshesType.Task { Error: AshesType.Primitive { Name: "Str" } } task when TryLeaf(task.Value, out Expr value) =>
                new Expr.Call(new Expr.Var("async"), value),
            _ => null,
        };
        if (candidate is null)
        {
            expression = null!;
            return false;
        }
        expression = candidate;
        return true;
    }

    private static bool TryLeaves(IReadOnlyList<AshesType> types, out Expr[] expressions)
    {
        expressions = new Expr[types.Count];
        for (int index = 0; index < types.Count; index++)
        {
            if (!TryLeaf(types[index], out Expr expression))
            {
                expressions = [];
                return false;
            }
            expressions[index] = expression;
        }
        return true;
    }

}
