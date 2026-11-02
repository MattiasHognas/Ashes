using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A function returns a cell consed onto a list a match bound from a call's result
/// (<c>match stage(state) with | applied -> label :: applied</c>). The cell is an arena cell that
/// escapes as the result, so it retains the bound list before the match releases it, and an arena
/// cell releases nothing: the retain, and with it the list and its strings, outlived every reader.
/// </summary>
internal sealed class EscapingCellRetainedTailTemplate() : LeakCombinationTemplate(
    "leak.escaping-cell-retained-tail", "cellRetainedTail", GeneratedFeature.Match, GeneratedFeature.Record, GeneratedFeature.List)
{
    protected override string Declarations(LeakNames names)
    {
        string context = names.Of("TailContext");
        string contextOf = names.Of("contextOf");
        string stage = names.Of("stage");
        string lower = names.Of("lowerIn");
        string probe = names.Of("probe");
        string loop = names.Of("loop");
        string bound = names.Pick("applied", "staged", "built");
        string head = names.Coin("countHead") ? "Ashes.Text.fromInt(c.count)" : names.Word();
        return $$"""
            type {{context}} =
                | name: Str
                | count: Int

            let {{contextOf}} (n: Int) = {{context}}(name = Ashes.Text.fromInt(n) + {{names.Word()}}, count = n)

            let {{stage}} (c: {{context}}) (state: List(Str)) = c.name :: state

            let {{lower}} (n: Int) (state: List(Str)) =
                (let c = {{contextOf}}(n)
                in
                    match {{stage}}(c)(state) with
                        | {{bound}} -> {{head}} :: {{bound}})

            let {{probe}} (n: Int) = Ashes.Collection.List.length({{lower}}(n)([{{names.Word()}}]))

            let recursive {{loop}} (n: Int) (acc: Int) =
                if n == 0
                then acc
                else {{loop}}(n - 1)(acc + {{probe}}(n))

            let {{names.Entry}} (seed: Int) = {{loop}}({{names.Between(3, 9)}})(seed)
            """;
    }
}
