using Ashes.Frontend;
using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Execution;

using AshesFormatter = Ashes.Formatter.Formatter;

internal static class ObservableValueRenderer
{
    internal static string RenderProgram(GeneratedFuzzCase testCase)
    {
        ObservationNames names = new();
        Expr rendered = Render(testCase.Program.Body, testCase.Type, names);
        Expr print = new Expr.Call(new Expr.QualifiedVar("Ashes.IO", "print"), rendered);
        Ashes.Frontend.Program observable = testCase.Program with { Body = print };
        return AshesFormatter.Format(observable);
    }

    private static Expr Render(Expr value, AshesType type, ObservationNames names) => type switch
    {
        AshesType.Primitive { Name: "Int" } => Call("Ashes.Text", "fromInt", value),
        AshesType.Primitive { Name: "Bool" } => new Expr.If(value, Text("true"), Text("false")),
        AshesType.Primitive { Name: "Str" } => Concat(Text("\""), value, Text("\"")),
        AshesType.Primitive { Name: "Float" } => Call("Ashes.Text", "fromFloat", value),
        AshesType.Primitive { Name: "BigInt" } => Call("Ashes.Text", "fromBigInt", value),
        AshesType.UInt => Call("Ashes.Text", "fromInt", Call("Ashes.Number.UInt", "toInt", value)),
        AshesType.Tuple tuple => RenderTuple(value, tuple, names),
        AshesType.List list => RenderList(value, list, names),
        AshesType.Record { Name: "FuzzRecord" } record => RenderRecord(value, record, names),
        AshesType.Result result => RenderResult(value, result, names),
        AshesType.Adt { Name: "FuzzTree" } tree => RenderTree(value, tree, names),
        AshesType.Adt { Name: "FuzzMaybe" } maybe => RenderMaybe(value, maybe, names),
        _ => throw new InvalidOperationException($"No canonical native observation renderer is registered for '{type}'."),
    };

    private static Expr RenderTuple(Expr value, AshesType.Tuple tuple, ObservationNames names)
    {
        string[] bindings = tuple.Elements.Select(_ => names.Next()).ToArray();
        List<Expr> parts = [Text("(")];
        for (int index = 0; index < bindings.Length; index++)
        {
            if (index != 0)
            {
                parts.Add(Text(","));
            }
            parts.Add(Render(new Expr.Var(bindings[index]), tuple.Elements[index], names));
        }
        parts.Add(Text(")"));
        Pattern pattern = new Pattern.Tuple(bindings.Select(name => (Pattern)new Pattern.Var(name)).ToArray());
        return new Expr.Match(value, [new MatchCase(pattern, Concat(parts))]);
    }

    private static Expr RenderList(Expr value, AshesType.List list, ObservationNames names)
    {
        string renderer = names.Next();
        string items = names.Next();
        string head = names.Next();
        string tail = names.Next();
        Expr body = new Expr.Match(new Expr.Var(items),
        [
            new MatchCase(new Pattern.EmptyList(), Text("]")),
            new MatchCase(
                new Pattern.Cons(new Pattern.Var(head), new Pattern.Var(tail)),
                Concat(
                    Render(new Expr.Var(head), list.Element, names),
                    new Expr.Match(new Expr.Var(tail),
                    [
                        new MatchCase(new Pattern.EmptyList(), Text("]")),
                        new MatchCase(new Pattern.Wildcard(), Concat(Text(","), new Expr.Call(new Expr.Var(renderer), new Expr.Var(tail)))),
                    ]))),
        ]);
        Expr lambda = new Expr.Lambda(items, body) { ParamAnnotation = list.ToSyntax() };
        Expr rendered = Concat(Text("["), new Expr.Call(new Expr.Var(renderer), value));
        return new Expr.LetRecursive(renderer, lambda, rendered)
        {
            TypeAnnotation = new AshesType.Function(list, AshesType.Str).ToSyntax(),
        };
    }

    private static Expr RenderRecord(Expr value, AshesType.Record record, ObservationNames names)
    {
        string first = names.Next();
        string second = names.Next();
        Pattern pattern = new Pattern.Constructor("FuzzRecord", [new Pattern.Var(first), new Pattern.Var(second)]);
        Expr body = Concat(
            Text("FuzzRecord{"),
            Render(new Expr.Var(first), AshesType.Int, names),
            Text(","),
            Render(new Expr.Var(second), AshesType.Bool, names),
            Text("}"));
        return new Expr.Match(value, [new MatchCase(pattern, body)]);
    }

    private static Expr RenderResult(Expr value, AshesType.Result result, ObservationNames names)
    {
        string payload = names.Next();
        return new Expr.Match(value,
        [
            new MatchCase(
                new Pattern.Constructor("Error", [new Pattern.Var(payload)]),
                Concat(Text("Error("), Render(new Expr.Var(payload), result.Error, names), Text(")"))),
            new MatchCase(
                new Pattern.Constructor("Ok", [new Pattern.Var(payload)]),
                Concat(Text("Ok("), Render(new Expr.Var(payload), result.Value, names), Text(")"))),
        ]);
    }

    private static Expr RenderTree(Expr value, AshesType.Adt tree, ObservationNames names)
    {
        AshesType payloadType = tree.Arguments[0];
        string renderer = names.Next();
        string node = names.Next();
        string payload = names.Next();
        string left = names.Next();
        string right = names.Next();
        Expr body = new Expr.Match(new Expr.Var(node),
        [
            new MatchCase(new Pattern.Constructor("FuzzEmpty", []), Text("FuzzEmpty")),
            new MatchCase(
                new Pattern.Constructor("FuzzLeaf", [new Pattern.Var(payload)]),
                Concat(Text("FuzzLeaf("), Render(new Expr.Var(payload), payloadType, names), Text(")"))),
            new MatchCase(
                new Pattern.Constructor("FuzzBranch", [new Pattern.Var(left), new Pattern.Var(right)]),
                Concat(
                    Text("FuzzBranch("),
                    new Expr.Call(new Expr.Var(renderer), new Expr.Var(left)),
                    Text(","),
                    new Expr.Call(new Expr.Var(renderer), new Expr.Var(right)),
                    Text(")"))),
        ]);
        Expr lambda = new Expr.Lambda(node, body) { ParamAnnotation = tree.ToSyntax() };
        return new Expr.LetRecursive(renderer, lambda, new Expr.Call(new Expr.Var(renderer), value))
        {
            TypeAnnotation = new AshesType.Function(tree, AshesType.Str).ToSyntax(),
        };
    }

    private static Expr RenderMaybe(Expr value, AshesType.Adt maybe, ObservationNames names)
    {
        string payload = names.Next();
        return new Expr.Match(value,
        [
            new MatchCase(new Pattern.Constructor("FuzzNone", []), Text("FuzzNone")),
            new MatchCase(
                new Pattern.Constructor("FuzzSome", [new Pattern.Var(payload)]),
                Concat(Text("FuzzSome("), Render(new Expr.Var(payload), maybe.Arguments[0], names), Text(")"))),
        ]);
    }

    private static Expr Call(string module, string name, Expr argument) =>
        new Expr.Call(new Expr.QualifiedVar(module, name), argument);

    private static Expr Text(string value) => new Expr.StrLit(value);

    private static Expr Concat(params Expr[] values) => Concat((IReadOnlyList<Expr>)values);

    private static Expr Concat(IReadOnlyList<Expr> values)
    {
        if (values.Count == 0)
        {
            return Text("");
        }
        Expr result = values[0];
        for (int index = 1; index < values.Count; index++)
        {
            result = new Expr.Add(result, values[index]);
        }
        return result;
    }

    private sealed class ObservationNames
    {
        private int _next;

        internal string Next() => "fuzzObserved" + (_next++).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
