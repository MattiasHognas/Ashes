using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A function of a mutually recursive group conses a fresh tuple (a part of its input beside a list
/// a helper builds) onto the accumulator it passes to its own next call. In a group the self call is
/// a closure call rather than a loop, so the cell built for the argument is an arena cell, which
/// never releases its head: the tuple and the strings its list holds outlived every reader.
/// </summary>
internal sealed class MutualGroupFreshTupleCellTemplate() : LeakCombinationTemplate(
    "leak.mutual-group-fresh-tuple-cell", "groupTupleCell", GeneratedFeature.MutualRecursion, GeneratedFeature.Tuple, GeneratedFeature.List, GeneratedFeature.Adt)
{
    protected override string Declarations(LeakNames names)
    {
        string node = names.Of("GroupNode");
        string leaf = names.Of("GLeaf");
        string pick = names.Of("GPick");
        string apply = names.Of("GApply");
        string lam = names.Of("GLam");
        string binders = names.Of("binders");
        string derive = names.Of("derive");
        string applied = names.Of("applied");
        string total = names.Of("total");
        string probe = names.Of("probe");
        string loop = names.Of("loop");
        string names2 = names.Coin("reversed") ? $"{binders}([])(ns)" : "ns";
        string firstPart = names.Coin("leafPart") ? "value" : $"{leaf}({names.Word()})";
        return $$"""
            type {{node}} =
                | {{leaf}}(Str)
                | {{pick}}({{node}}, List(Str))
                | {{apply}}({{node}}, List({{node}}))
                | {{lam}}(Str, {{node}})

            let recursive {{binders}} (acc: List(Str)) (xs: List(Str)) =
                match xs with
                    | [] -> acc
                    | x :: rest -> {{binders}}(x :: acc)(rest)

            let recursive {{derive}} (nodes: List({{node}})) (ds: List(({{node}}, List(Str)))) =
                match nodes with
                    | [] -> ds
                    | {{pick}}(value, ns) :: rest -> {{derive}}(rest)(({{firstPart}}, {{binders}}([])({{names2}})) :: ds)
                    | {{apply}}(root, arguments) :: rest ->
                        ds
                        |> {{applied}}(root)(arguments)
                        |> {{derive}}(rest)
                    | _other :: rest -> {{derive}}(rest)(ds)
            and {{applied}} (root: {{node}}) (arguments: List({{node}})) (ds: List(({{node}}, List(Str)))) =
                match (root, arguments) with
                    | ({{lam}}(parameter, body), argument :: rest) -> {{applied}}(body)(rest)((argument, [parameter]) :: ds)
                    | _ -> ds

            let recursive {{total}} (k: Int) (ds: List(({{node}}, List(Str)))) =
                match ds with
                    | [] -> k
                    | (_v, ns) :: rest -> {{total}}(k + Ashes.Collection.List.length(ns))(rest)

            let {{probe}} (n: Int) =
                {{derive}}([{{pick}}({{leaf}}({{names.Word()}}), [{{names.Word()}}, Ashes.Text.fromInt(n)]), {{leaf}}({{names.Word()}}), {{pick}}({{leaf}}(Ashes.Text.fromInt(n)), {{names.Words(names.Between(1, 3))}})])([])
                |> {{total}}(0)

            let recursive {{loop}} (n: Int) (acc: Int) =
                if n == 0
                then acc
                else {{loop}}(n - 1)(acc + {{probe}}(n))

            let {{names.Entry}} (seed: Int) = {{loop}}({{names.Between(3, 9)}})(seed)
            """;
    }
}
