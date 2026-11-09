using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A list another module's function returns, passed fresh to a function that returns either that
/// list or a new one built over its elements. The module's export is declared the way project
/// stitching emits it (<c>Module_name</c>) and reached through its qualified name or through the
/// alias a selector import stitches in; the caller releases its argument after the call only when it
/// knows the callee's compiled result is reference-counted.
/// </summary>
internal sealed class QualifiedCalleeResultArgumentTemplate() : LeakCombinationTemplate(
    "leak.qualified-callee-result-argument", "qualifiedCalleeResult", GeneratedFeature.Match, GeneratedFeature.List)
{
    protected override string Declarations(LeakNames names)
    {
        string module = names.Of("Names");
        string collect = names.Of("collect");
        string exported = $"{module}_{collect}";
        string overlay = names.Of("overlay");
        string pick = names.Of("pick");
        string count = names.Of("count");
        string alias = names.Of("collected");
        bool viaAlias = names.Coin("alias");
        string callee = viaAlias ? alias : $"{module}.{collect}";
        string aliasDeclaration = viaAlias ? $"let {alias} = {module}.{collect}\n\n" : "";
        int width = names.Between(1, 4);
        string items = "[" + string.Join(", ", Enumerable.Range(0, width).Select(index => index == 0 ? "Ashes.Text.fromInt(seed)" : "\"" + names.Pick("ash", "ember", "coal", "flint") + "\"")) + "]";
        string weight = names.Between(1, 3).ToString(System.Globalization.CultureInfo.InvariantCulture);
        string flag = names.Coin("overlay-branch") ? "seed >= 0" : "seed < 0";
        return $$"""
            let recursive {{exported}} (items: List(Str)) (acc: List((Str, Int))) =
                match items with
                    | [] -> Ashes.Collection.List.reverse(acc)
                    | item :: rest -> {{exported}}(rest)((item, {{weight}}) :: acc)

            {{aliasDeclaration}}let recursive {{overlay}} (proven: Int) (pairs: List((Str, Int))) =
                match pairs with
                    | (name, _n) :: rest ->
                        if proven > 0
                        then (name, 0) :: {{overlay}}(proven - 1)(rest)
                        else pairs
                    | [] -> []

            let {{pick}} (flag: Bool) (pairs: List((Str, Int))) =
                if flag
                then {{overlay}}(1)(pairs)
                else pairs

            let recursive {{count}} (pairs: List((Str, Int))) =
                match pairs with
                    | [] -> 0
                    | (_name, n) :: rest -> n + {{count}}(rest)

            let {{names.Entry}} (seed: Int) = {{count}}({{pick}}({{flag}})({{callee}}({{items}})([])))
            """;
    }
}
