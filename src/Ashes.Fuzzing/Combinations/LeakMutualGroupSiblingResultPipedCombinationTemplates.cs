using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A function of a mutually recursive group pipes its accumulator through a sibling and on into its own
/// next call (<c>ds |> applied(root)(arguments) |> derive(rest)</c>). The sibling is lowered after the
/// caller, so its result placement was not known and was taken unread: the owned list it returned
/// was handed straight to the next call and never released.
/// </summary>
internal sealed class MutualGroupSiblingResultPipedTemplate() : LeakCombinationTemplate(
    "leak.mutual-group-sibling-result-piped", "groupSiblingPiped", GeneratedFeature.MutualRecursion, GeneratedFeature.List, GeneratedFeature.Adt)
{
    protected override string Declarations(LeakNames names)
    {
        string node = names.Of("PipeNode");
        string leaf = names.Of("PLeaf");
        string pick = names.Of("PPick");
        string apply = names.Of("PApply");
        string lam = names.Of("PLam");
        string binders = names.Of("binders");
        string derive = names.Of("derive");
        string applied = names.Of("applied");
        string total = names.Of("total");
        string probe = names.Of("probe");
        string loop = names.Of("loop");
        string partner = names.Coin("partnerSome") ? "Some([parameter])" : "None";
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

            let recursive {{derive}} (nodes: List({{node}})) (ds: List(Maybe(List(Str)))) =
                match nodes with
                    | [] -> ds
                    | {{pick}}(_value, ns) :: rest -> {{derive}}(rest)(Some({{binders}}([])(ns)) :: ds)
                    | {{apply}}(root, arguments) :: rest ->
                        ds
                        |> {{applied}}(root)(arguments)
                        |> {{derive}}(rest)
                    | _other :: rest -> {{derive}}(rest)(ds)
            and {{applied}} (root: {{node}}) (arguments: List({{node}})) (ds: List(Maybe(List(Str)))) =
                match (root, arguments) with
                    | ({{lam}}(parameter, body), _argument :: rest) -> {{applied}}(body)(rest)({{partner}} :: ds)
                    | _ -> ds

            let recursive {{total}} (k: Int) (ds: List(Maybe(List(Str)))) =
                match ds with
                    | [] -> k
                    | Some(ns) :: rest -> {{total}}(k + Ashes.Collection.List.length(ns))(rest)
                    | None :: rest -> {{total}}(k)(rest)

            let {{probe}} (n: Int) =
                {{derive}}([{{pick}}({{leaf}}({{names.Word()}}), [{{names.Word()}}, Ashes.Text.fromInt(n)]), {{apply}}({{lam}}({{names.Word()}}, {{leaf}}({{names.Word()}})), [{{leaf}}({{names.Word()}})]), {{pick}}({{leaf}}(Ashes.Text.fromInt(n)), {{names.Words(names.Between(1, 3))}})])([])
                |> {{total}}(0)

            let recursive {{loop}} (n: Int) (acc: Int) =
                if n == 0
                then acc
                else {{loop}}(n - 1)(acc + {{probe}}(n))

            let {{names.Entry}} (seed: Int) = {{loop}}({{names.Between(3, 9)}})(seed)
            """;
    }
}
