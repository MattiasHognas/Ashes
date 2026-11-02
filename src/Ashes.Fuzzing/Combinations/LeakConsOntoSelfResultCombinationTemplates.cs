using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A function that returns its list parameter on one arm and conses onto its own recursive call on
/// the other, walked over fresh lists of optional constructors through a record state. Its result
/// is promised reference-counted, so the cons arm's last arena cell holds an owned tail the join
/// takes over.
/// </summary>
internal sealed class ConsOntoSelfResultTemplate() : LeakCombinationTemplate(
    "leak.cons-onto-self-result", "consOntoSelfResult", GeneratedFeature.Adt, GeneratedFeature.Match, GeneratedFeature.List)
{
    protected override string Declarations(LeakNames names)
    {
        string type = names.Of("ShapeType");
        string pass = names.Of("ShapePass");
        string grown = names.Of("ShapeGrown");
        string other = names.Of("ShapeOther");
        string walk = names.Of("WalkState");
        string observed = names.Of("observed");
        string count = names.Of("count");
        string merge = names.Of("merge");
        string shapeOf = names.Of("shapeOf");
        string observe = names.Of("observe");
        string initial = names.Of("initial");
        string walkCalls = names.Of("walkCalls");
        string countSome = names.Of("countSome");
        string head = names.Coin("merged-head") ? $"{merge}(current)({shapeOf}(argument))" : $"Some({shapeOf}(argument))";
        int width = names.Between(2, 4);
        string seedList = "[" + string.Join(", ", Enumerable.Range(0, width).Select(index => index == 0 ? "seed" : LeakNames.Text(names.Between(0, 5)))) + "]";
        string calls = names.Coin("three-calls") ? $"[{seedList}, {seedList}, {seedList}]" : $"[{seedList}, {seedList}]";
        string run = $$"""
                match {{walkCalls}}({{calls}})({{walk}}({{observed}} = {{initial}}({{LeakNames.Text(width)}}), {{count}} = 0)) with
                        | {{walk}} { {{observed}} = shapes, {{count}} = walked } -> {{countSome}}(shapes) + walked
                """;
        return $$"""
            type {{type}} =
                | {{pass}}
                | {{grown}}
                | {{other}}
                deriving {Eq}

            type {{walk}} =
                | {{observed}}: List(Maybe({{type}}))
                | {{count}}: Int

            let {{merge}} (seen: Maybe({{type}})) (local: {{type}}) =
                match seen with
                    | None -> Some(local)
                    | Some(previous) ->
                        if previous == local
                        then Some(previous)
                        else Some({{other}})

            let {{shapeOf}} (argument: Int) =
                if argument > 1
                then {{grown}}
                else {{pass}}

            let recursive {{observe}} (arguments: List(Int)) (shapes: List(Maybe({{type}}))) =
                match (arguments, shapes) with
                    | (argument :: restArguments, current :: restShapes) -> {{head}} :: {{observe}}(restArguments)(restShapes)
                    | _ -> shapes

            let recursive {{initial}} (remaining: Int) =
                if remaining == 0
                then []
                else None :: {{initial}}(remaining - 1)

            let recursive {{walkCalls}} (calls: List(List(Int))) (state: {{walk}}) =
                match calls with
                    | [] -> state
                    | arguments :: rest -> {{walkCalls}}(rest)({{walk}}({{observed}} = {{observe}}(arguments)(state.{{observed}}), {{count}} = state.{{count}} + 1))

            let recursive {{countSome}} (shapes: List(Maybe({{type}}))) =
                match shapes with
                    | [] -> 0
                    | Some(_shape) :: rest -> 1 + {{countSome}}(rest)
                    | None :: rest -> {{countSome}}(rest)

            let {{names.Entry}} (seed: Int) =
                {{run}}
            """;
    }
}
