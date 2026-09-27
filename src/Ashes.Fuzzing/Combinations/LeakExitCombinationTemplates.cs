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

/// <summary>
/// A generic walk whose exit arm returns a string bound under a nested pattern
/// (<c>| last :: [] -&gt; last</c>, <c>first :: _ :: []</c>, <c>(name, _) :: []</c>,
/// <c>(_ :: last :: []) :: []</c>) while the <c>[] -&gt; ""</c> arm fixes the type only later; the
/// caller concatenates the returned string, so a freed one is read.
/// </summary>
internal sealed class LoopExitHeadUnderNestedPatternTemplate() : LeakCombinationTemplate(
    "leak.loop-exit-head-under-nested-pattern", "loopExitHeadUnderNestedPattern", GeneratedFeature.TailCall, GeneratedFeature.Match, GeneratedFeature.List, GeneratedFeature.Let)
{
    protected override string Declarations(LeakNames names)
    {
        string walk = names.Of("headOf");
        string word = names.Word();
        (string arm, string list) = names.Variant("pattern", 4) switch
        {
            0 => ("| last :: [] -> last", $"[{word}, {names.Word()} + suffix]"),
            1 => ("| first :: _ :: [] -> first", $"[{word}, {names.Word()} + suffix, {names.Word()}]"),
            2 => ("| (name, _) :: [] -> name", $"[({word}, 1), ({names.Word()} + suffix, 2)]"),
            _ => ("| (_ :: last :: []) :: [] -> last", $"[[{word}], [{names.Word()}, {names.Word()} + suffix]]"),
        };
        string repeat = names.Coin("walk-twice") ? $" + {walk}({list})" : "";
        return $$"""
            let recursive {{walk}} list =
                match list with
                    {{arm}}
                    | _ :: rest -> {{walk}}(rest)
                    | [] -> ""

            let {{names.Entry}} (seed: Int) =
                let suffix = Ashes.Text.fromInt(seed)
                in
                    let picked = {{walk}}({{list}}){{repeat}}
                    in Ashes.Text.byteLength({{names.Word()}} + picked)
            """;
    }
}

/// <summary>
/// A loop over a list shared by every round whose record literal successor holds <c>Some(span)</c>
/// with <c>span</c> matched out of that list: the literal only borrows it, so the back edge must
/// leave the span to the list that owns it. A release of the borrowed child frees the shared list's
/// cells, which the next round reads.
/// </summary>
internal sealed class BorrowedChildOfLiteralSuccessorTemplate() : LeakCombinationTemplate(
    "leak.borrowed-child-of-literal-successor", "borrowedChildOfLiteralSuccessor", GeneratedFeature.Record, GeneratedFeature.Adt, GeneratedFeature.UniqueConstructorUpdate, GeneratedFeature.TailCall, GeneratedFeature.Match, GeneratedFeature.List)
{
    protected override string Declarations(LeakNames names)
    {
        string span = names.Of("LeakSpan");
        string item = names.Of("LeakItem");
        string group = names.Of("LeakGroup");
        string state = names.Of("LeakState");
        string lower = names.Of("lowerItems");
        string items = names.Of("items");
        string program = names.Of("program");
        string count = names.Of("countInts");
        int length = names.Between(8, 40);
        bool counts = names.Coin("count-instructions");
        string exit = counts ? $"state.counter + {count}(state.instructions)(0)" : "state.counter";
        return $$"""
            type {{span}} =
                | start: Int
                | length: Int

            type {{item}} =
                | At({{span}}, {{item}})
                | Decl(Int)
                | Let(Int)

            type {{group}} =
                | declarationSpan: Maybe({{span}})
                | names: List(Str)

            type {{state}} =
                | instructions: List(Int)
                | currentSpan: Maybe({{span}})
                | functionGroup: {{group}}
                | counter: Int

            let recursive {{count}} (values: List(Int)) (total: Int) =
                match values with
                    | [] -> total
                    | _ :: tail -> {{count}}(tail)(total + 1)

            let recursive {{lower}} (items: List({{item}})) (body: List(Str)) (seen: List(Str)) (state: {{state}}) =
                match items with
                    | [] -> {{exit}}
                    | At(span, inner) :: rest ->
                        {{state}}(instructions = state.instructions, currentSpan = Some(span), functionGroup = state.functionGroup, counter = state.counter)
                        |> {{lower}}(inner :: rest)(body)(seen)
                    | Decl(_value) :: rest -> {{lower}}(rest)(body)(seen)(state)
                    | Let(value) :: rest -> {{lower}}(rest)(body)(seen)((state with instructions = value :: state.instructions, counter = state.counter + 1))

            let recursive {{items}} (n: Int) (built: List({{item}})) =
                if n == 0
                then built
                else {{items}}(n - 1)(At({{span}}(start = n, length = 1))(Decl(n)) :: At({{span}}(start = n, length = 2))(Let(n)) :: built)

            let {{program}} = {{items}}({{LeakNames.Text(length)}})([])

            let {{names.Entry}} (seed: Int) = {{lower}}({{program}})([{{names.Word()}}])([{{names.Word()}}])({{state}}(instructions = [], currentSpan = None, functionGroup = {{group}}(declarationSpan = None, names = [{{names.Word()}}]), counter = seed))
            """;
    }
}

/// <summary>
/// A worklist over a tree of seen keys whose front queue is walked by its consumed tail and
/// refilled from the reversed back queue, each step pairing the grown back queue with the grown
/// tree: the front must live on the reference-counted heap for each step's earlier pair to be
/// released.
/// </summary>
internal sealed class WorklistFrontWalkedAndRefilledTemplate() : LeakCombinationTemplate(
    "leak.worklist-front-walked-and-refilled", "worklistFrontWalkedAndRefilled", GeneratedFeature.Adt, GeneratedFeature.Tuple, GeneratedFeature.TailCall, GeneratedFeature.Match, GeneratedFeature.List)
{
    protected override string Declarations(LeakNames names)
    {
        string seen = names.Of("LeakSeen");
        string insert = names.Of("insert");
        string countSeen = names.Of("countSeen");
        string reverseAll = names.Of("reverseAll");
        string enqueue = names.Of("enqueue");
        string solve = names.Of("solve");
        string keys = names.Words(names.Between(2, 4));
        int fuel = names.Between(6, 14);
        string refill = names.Coin("library-reverse") ? "Ashes.Collection.List.reverse(back)" : $"{reverseAll}(back)([])";
        return $$"""
            type {{seen}} =
                | Leaf
                | Node({{seen}}, Str, {{seen}})

            let recursive {{insert}} (key: Str) (seen: {{seen}}) =
                match seen with
                    | Leaf -> Node(Leaf)(key)(Leaf)
                    | Node(left, current, right) -> Node({{insert}}(key)(left))(current)(right)

            let recursive {{countSeen}} (seen: {{seen}}) =
                match seen with
                    | Leaf -> 0
                    | Node(left, _current, right) -> {{countSeen}}(left) + 1 + {{countSeen}}(right)

            let recursive {{reverseAll}} (values: List(Str)) (acc: List(Str)) =
                match values with
                    | [] -> acc
                    | value :: rest -> {{reverseAll}}(rest)(value :: acc)

            let {{enqueue}} (key: Str) (back: List(Str)) (seen: {{seen}}) = (key :: back, {{insert}}(key)(seen))

            let recursive {{solve}} (front: List(Str)) (back: List(Str)) (seen: {{seen}}) (fuel: Int) =
                match front with
                    | [] ->
                        match back with
                            | [] -> {{countSeen}}(seen)
                            | _ -> {{solve}}({{refill}})([])(seen)(fuel)
                    | key :: rest ->
                        if fuel <= 0
                        then {{countSeen}}(seen)
                        else
                            match {{enqueue}}(key + {{names.Word()}})(back)(seen) with
                                | (nextBack, nextSeen) -> {{solve}}(rest)(nextBack)(nextSeen)(fuel - 1)

            let {{names.Entry}} (seed: Int) = {{solve}}({{keys}})([])(Leaf)({{LeakNames.Text(fuel)}}) + seed
            """;
    }
}
