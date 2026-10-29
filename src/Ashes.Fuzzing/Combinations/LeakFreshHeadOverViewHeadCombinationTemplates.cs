using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A loop pushing a fresh byte buffer over a view of another (<c>fresh :: fromText(view) :: acc</c>),
/// the view taken of the loop's buffer parameter or of a string literal, and either pushed in the
/// chain or as its innermost cell. The view head is copied onto the reference-counted heap, so its
/// cell and the fresh cell over it are reference-counted and the caller releases the whole list.
/// </summary>
internal sealed class FreshHeadOverViewHeadTemplate() : LeakCombinationTemplate(
    "leak.fresh-head-over-view-head", "freshHeadOverViewHead", GeneratedFeature.TailCall, GeneratedFeature.List)
{
    protected override string Declarations(LeakNames names)
    {
        string views = names.Of("leakViews");
        string measure = names.Of("leakMeasure");
        string size = LeakNames.Text(names.Between(1, 9));
        string view = names.Coin("literal-view")
            ? "Ashes.Byte.fromText(\"ab\")"
            : "Ashes.Byte.fromText(Ashes.Byte.subView(buffer)(index)(1))";
        string push = names.Variant("push", 3) switch
        {
            0 => $"Ashes.Byte.allocate({size}) :: {view} :: acc",
            1 => $"{view} :: acc",
            _ => $"Ashes.Byte.allocate({size}) :: {view} :: Ashes.Byte.allocate(1) :: acc",
        };
        return $$"""
            let recursive {{views}} (buffer: Bytes) (index: Int) (limit: Int) (acc: List(Bytes)) =
                if index >= limit
                then acc
                else {{views}}(buffer)(index + 1)(limit)({{push}})

            let recursive {{measure}} (pieces: List(Bytes)) (sum: Int) =
                match pieces with
                    | [] -> sum
                    | piece :: rest -> {{measure}}(rest)(sum + Ashes.Byte.length(piece))

            let {{names.Entry}} (seed: Int) = {{measure}}({{views}}(Ashes.Byte.allocate(16))(0)(seed % 5 + 3)([]))(0) + seed
            """;
    }
}
