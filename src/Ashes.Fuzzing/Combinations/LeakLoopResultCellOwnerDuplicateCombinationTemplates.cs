using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A function that loops through wrappers of its variant parameter and, in its catch-all arm, builds
/// a record around the whole parameter (`| root -> Spine(root = root, arguments = [])`), the shape of
/// a call spine collected from a syntax tree. The record is built in the arena with a duplicate of the
/// parameter, the return copies it into a reference-counted record that retains the field itself,
/// and the arena cell's duplicate was never released. It goes to an owned slot released behind the
/// result. At a call node the other arm conses the call's argument, a part of the parameter, onto
/// the rebuilt record's list, and the duplicate that consed cell takes of it is kept the same way.
/// </summary>
internal sealed class LoopResultCellOwnerDuplicateTemplate() : LeakCombinationTemplate(
    "leak.loop-result-cell-owner-duplicate", "loopResultCellOwnerDuplicate", GeneratedFeature.Match, GeneratedFeature.Adt, GeneratedFeature.Record)
{
    protected override string Declarations(LeakNames names)
    {
        string node = names.Of("Node");
        string spine = names.Of("Spine");
        string collect = names.Of("collectSpine");
        string isName = names.Of("isName");
        string prefix = names.Pick("walk", "go", "loop", "fold");
        int wraps = names.Between(0, 3);
        bool numbered = names.Coin("numbered-name");
        string tree = $"NodeName(\"{prefix}\" + Ashes.Text.fromInt(seed))";
        if (numbered)
        {
            tree = $"NodeName(\"{prefix}\" + Ashes.Text.fromInt(seed * 2))";
        }

        // A call node makes the other arm cons a part of the parameter onto the rebuilt record's list.
        int calls = names.Between(0, 2);
        for (int i = 0; i < calls; i++)
        {
            tree = $"NodeCall({tree}, NodeName(\"arg{i}\" + Ashes.Text.fromInt(seed)))";
        }

        for (int i = 0; i < wraps; i++)
        {
            tree = $"NodeAt({i}, {tree})";
        }

        return $$"""
            type {{node}} =
                | NodeName(Str)
                | NodeInt(Int)
                | NodeAt(Int, {{node}})
                | NodeCall({{node}}, {{node}})

            type {{spine}} =
                | root: {{node}}
                | arguments: List({{node}})

            let recursive {{collect}} (expression: {{node}}) =
                match expression with
                    | NodeAt(_span, inner) -> {{collect}}(inner)
                    | NodeCall(function, argument) ->
                        match {{collect}}(function) with
                            | {{spine}} { root = root, arguments = arguments } -> {{spine}}(root = root, arguments = argument :: arguments)
                    | root -> {{spine}}(root = root, arguments = [])

            let {{isName}} (expression: {{node}}) =
                match expression with
                    | NodeName(_name) -> 1
                    | _ -> 0

            let {{names.Entry}} (seed: Int) =
                match {{collect}}({{tree}}) with
                    | {{spine}} { root = root, arguments = arguments } -> {{isName}}(root) + Ashes.Collection.List.length(arguments)
            """;
    }
}
