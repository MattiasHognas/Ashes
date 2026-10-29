using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A loop arm that pipes a fresh record through a chain of calls and continues with a part of the
/// last call's result, beside an item list kept in the arena for a generic list sibling that may hold
/// its parts. The intermediate results are never bound to a name and only handed to the next call,
/// so each iteration's are released at the back edge.
/// </summary>
internal sealed class HandedOnPipelineStepTemplate() : LeakCombinationTemplate(
    "leak.handed-on-pipeline-step", "handedOnPipelineStep", GeneratedFeature.Record, GeneratedFeature.Match)
{
    protected override string Declarations(LeakNames names)
    {
        string state = names.Of("LeakState");
        string group = names.Of("LeakGroup");
        string lowered = names.Of("LeakLowered");
        string item = names.Of("LeakItem");
        string prepare = names.Of("prepare");
        string relabel = names.Of("relabel");
        string open = names.Of("openGroup");
        string loop = names.Of("loop");
        string items = names.Of("items");
        string word = names.Word();
        string relabelStep = names.Coin("relabel") ? $"\n                    |> {relabel}(id)" : string.Empty;
        return $$"""
            type {{state}} =
                | names: List(Str)
                | count: Int
                | bindings: List(Str)

            type {{group}} =
                | state: {{state}}
                | members: List(Str)
                | error: Maybe(Str)

            type {{lowered}} =
                | state: {{state}}
                | temp: Int
                | error: Maybe(Str)

            type {{item}} =
                | {{item}}Plain(Str)
                | {{item}}Wrapped({{item}})
                | {{item}}Many(List(Str))

            let recursive {{prepare}} (names: List(Str)) (state: {{state}}) (reversed: List(Str)) =
                match names with
                    | [] -> {{group}}(state = state, members = Ashes.Collection.List.reverse(reversed), error = None)
                    | name :: rest ->
                        match state with
                            | {{state}} { count = count } -> {{prepare}}(rest)((state with names = [name], count = count + 1))(name :: reversed)

            let {{relabel}} (id: Int) (prepared: {{group}}) =
                match prepared with
                    | {{group}} { state = state, members = member :: [], error = None } -> {{group}}(state = state, members = [member + Ashes.Text.fromInt(id)], error = None)
                    | _ -> prepared

            let {{open}} (outer: List(Str)) (prepared: {{group}}) =
                match prepared with
                    | {{group}} { state = failed, error = Some(error) } -> {{lowered}}(state = failed, temp = 0, error = Some(error))
                    | {{group}} { state = state, members = members, error = None } -> {{lowered}}(state = (state with bindings = members), temp = Ashes.Collection.List.length(outer), error = None)

            let recursive {{loop}} (items: List({{item}})) wrap seen (state: {{state}}) =
                match items with
                    | [] -> state.count + Ashes.Collection.List.length(state.names)
                    | {{item}}Wrapped(inner) :: rest -> {{loop}}(inner :: rest)(wrap)(seen)(state)
                    | {{item}}Many(_names) :: rest -> {{loop}}(rest)(wrap)(seen)(state)
                    | {{item}}Plain(name) :: rest ->
                        match state with
                            | {{state}} { bindings = outer, count = id } ->
                                match []
                                |> {{prepare}}([name])(state){{relabelStep}}
                                |> {{open}}(outer) with
                                    | {{lowered}} { state = failed, error = Some(_error) } -> failed.count
                                    | {{lowered}} { state = opened, error = None } -> {{loop}}(rest)(wrap)(wrap(name) :: seen)(opened)

            let recursive {{items}} (n: Int) (acc: List({{item}})) =
                if n <= 0
                then acc
                else {{items}}(n - 1)({{item}}Wrapped({{item}}Plain({{word}})) :: acc)

            let {{names.Entry}} (seed: Int) =
                {{state}}(names = [{{word}}], count = seed, bindings = [])
                |> {{loop}}({{items}}({{names.Between(3, 9)}})([]))(given (s: Str) -> [s])([])
            """;
    }
}
