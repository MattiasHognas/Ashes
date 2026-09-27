using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A pass over a list of instruction records, given a fresh list built in the program's trailing
/// pipe (<c>0 |&gt; pass(build(N)([]))</c>): the entry holds the built list as an owned call
/// result and must release it at its end. The census runs the pipe as the trailing expression,
/// with the rounds count as the built list's length.
/// </summary>
internal sealed class EntryTrailingPipeFreshArgumentTemplate() : LeakCombinationTemplate(
    "leak.entry-trailing-pipe-fresh-argument", "entryTrailingPipeFreshArgument", GeneratedFeature.Record, GeneratedFeature.Tuple, GeneratedFeature.TailCall, GeneratedFeature.Match, GeneratedFeature.List)
{
    protected override string Declarations(LeakNames names)
    {
        string where = names.Of("LeakWhere");
        string instr = names.Of("LeakInstr");
        string step = names.Of("step");
        string pass = names.Of("pass");
        string build = names.Of("build");
        string kind = names.Pick("kind", "opcode", "tag");
        bool file = names.Coin("file-field");
        bool pair = names.Coin("step-returns-pair");
        string fileField = file ? "\n    | file: Str" : "";
        string fileInit = file ? $", file = {names.Word()}" : "";
        string stepResult = pair
            ? $"({kind}, {instr}({kind} = {kind} + 1, location = location))"
            : $"{instr}({kind} = {kind} + {LeakNames.Text(names.Between(1, 3))}, location = location)";
        string stepMatch = pair
            ? $"match {step}({kind})(location) with\n                | (_n, rewritten) -> {pass}(tail)(total + rewritten.{kind})"
            : $"let rewritten = {step}({kind})(location)\n                in {pass}(tail)(total + rewritten.{kind})";
        return $$"""
            type {{where}} =
                | line: Int
                | column: Int{{fileField}}

            type {{instr}} =
                | {{kind}}: Int
                | location: {{where}}

            let {{step}} ({{kind}}: Int) (location: {{where}}) = {{stepResult}}

            let recursive {{pass}} (instructions: List({{instr}})) (total: Int) =
                match instructions with
                    | [] -> total
                    | {{instr}} { {{kind}} = {{kind}}, location = location } :: tail ->
                        {{stepMatch}}

            let recursive {{build}} (n: Int) (acc: List({{instr}})) =
                if n == 0
                then acc
                else {{build}}(n - 1)({{instr}}({{kind}} = n, location = {{where}}(line = n, column = 1{{fileInit}})) :: acc)

            let {{names.Entry}} (seed: Int) = seed |> {{pass}}({{build}}(seed)([]))
            """;
    }

    protected override string? TrailingWorkload(LeakNames names) =>
        $"0\n|> {names.Of("pass")}({names.Of("build")}(ROUNDS)([]))\n|> Ashes.IO.print";
}

