using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A function called inside a handle returns a tuple holding a perform's result, which the caller
/// passes straight to another function. Without general reference counting the tuple was placed on
/// the reference-counted heap to own the perform's string, but the caller takes the handle's value
/// as an arena value and releases nothing: the tuple and the string outlived every reader.
/// </summary>
internal sealed class ArenaTupleFreshElementTemplate() : LeakCombinationTemplate(
    "leak.arena-tuple-fresh-element", "arenaTupleFresh", GeneratedFeature.Capability, GeneratedFeature.Handler, GeneratedFeature.Tuple)
{
    protected override string Declarations(LeakNames names)
    {
        string capability = names.Of("LeakTakeTuple");
        string make = names.Of("make");
        string size = names.Of("size");
        string probe = names.Of("probe");
        string loop = names.Of("loop");
        string tuple = names.Coin("valueFirst") ? "(perform {0}.get(Unit), i)" : "(i, perform {0}.get(Unit))";
        string pattern = tuple.StartsWith("(perform", StringComparison.Ordinal) ? "(s, a)" : "(a, s)";
        string type = tuple.StartsWith("(perform", StringComparison.Ordinal) ? "(Str, Int)" : "(Int, Str)";
        string arm = names.Coin("returnArm")
            ? $"| {capability}.get(u) -> resume({names.Word()})\n        | return(r) -> r"
            : $"| {capability}.get(u) -> resume(Ashes.Text.fromInt(n))";
        return $$"""
            capability {{capability}} =
                | get : Unit -> Str

            let {{make}} (i: Int) = {{string.Format(System.Globalization.CultureInfo.InvariantCulture, tuple, capability)}}

            let {{size}} (p: {{type}}) =
                match p with
                    | {{pattern}} -> Ashes.Text.byteLength(s) + a

            let {{probe}} (n: Int) =
                {{size}}(handle {{make}}(n) with
                    {{arm}})

            let recursive {{loop}} (n: Int) (acc: Int) =
                if n == 0
                then acc
                else {{loop}}(n - 1)(acc + {{probe}}(n))

            let {{names.Entry}} (seed: Int) = {{loop}}({{names.Between(3, 9)}})(seed)
            """;
    }
}
