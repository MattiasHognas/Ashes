using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A function whose result joins a reference-counted tuple holding a split of a let-bound string
/// with a nested match or a retained binding (<c>let slashed = rebuild(path) in if flag then ("",
/// true, split(slashed)) else match split(slashed) with ...</c>). The scope releasing the string
/// copies the join into the arena first, since the join may view it; the join itself, which owned
/// its reference, was never released.
/// </summary>
internal sealed class ViewCopiedScopeJoinTemplate() : LeakCombinationTemplate(
    "leak.view-copied-scope-join", "viewCopiedScopeJoin", GeneratedFeature.Let, GeneratedFeature.Match, GeneratedFeature.Tuple)
{
    protected override string Declarations(LeakNames names)
    {
        string rebuild = names.Of("rebuild");
        string parts = names.Of("parts");
        string separator = names.Pick("\"/\"", "\",\"");
        string sep = separator.Trim('"');
        string firstArm = names.Coin("freshHead") ? "(server + \"x\", true, [])" : "(\"\", true, rest)";
        string otherArm = names.Coin("staticOther") ? "(\"\", false, [])" : "(\"\" + rebuild(\"x\"), false, [])";
        otherArm = otherArm.Replace("rebuild", rebuild, StringComparison.Ordinal);
        string elseArm = names.Variant("elseShape", 3) switch
        {
            0 => $$"""
                match Ashes.Text.split(slashed)({{separator}}) with
                                | server :: rest -> {{firstArm}}
                                | _ -> {{otherArm}}
                """,
            1 => $$"""
                match Ashes.Text.length(slashed) with
                                | {{LeakNames.Text(names.Between(1, 3))}} -> ("", true, [])
                                | _ -> {{otherArm}}
                """,
            _ => "(\"\", false, Ashes.Text.split(other)(" + separator + "))",
        };
        string path = $"\"{sep}a{sep}b{sep}{sep}c\"";
        return $$"""
            let recursive {{rebuild}} (path: Str) =
                match Ashes.Text.unconsText(path) with
                    | None -> ""
                    | Some((head, tail)) -> head + {{rebuild}}(tail)

            let {{parts}} (flag: Bool) (path: Str) (other: Str) =
                (let slashed = {{rebuild}}(path)
                in
                    if flag
                    then ("", true, Ashes.Text.split(slashed)({{separator}}))
                    else
                        {{elseArm}})

            let {{names.Entry}} (seed: Int) =
                match {{parts}}(seed > 0)({{path}} + Ashes.Text.fromInt(seed))("x{{sep}}y" + Ashes.Text.fromInt(seed)) with
                    | (prefix, _absolute, found) -> Ashes.Text.length(prefix) + Ashes.Collection.List.length(found)
            """;
    }
}

/// <summary>
/// A recursive producer consing a string literal onto its own recursive call
/// (<c>_head :: tail -&gt; ".." :: parents(tail)</c>). The literal could not be counted, so the cell
/// stayed in the arena and the reference-counted copy of each level's result it held was never
/// released.
/// </summary>
internal sealed class ProducerStringLiteralHeadTemplate() : LeakCombinationTemplate(
    "leak.producer-string-literal-head", "producerStringLiteralHead", GeneratedFeature.RecursiveFunction, GeneratedFeature.List)
{
    protected override string Declarations(LeakNames names)
    {
        string parents = names.Of("parents");
        string append = names.Of("appendAll");
        string literal = names.Word();
        string baseCase = names.Coin("literalBase") ? $"[{names.Word()}]" : "[]";
        bool appended = names.Coin("appended");
        string body = appended
            ? $"Ashes.Collection.List.length({append}({parents}(parts))([\"x\" + Ashes.Text.fromInt(seed)]))"
            : $"Ashes.Collection.List.length({parents}(parts))";
        return $$"""
            let recursive {{parents}} parts =
                match parts with
                    | [] -> {{baseCase}}
                    | _head :: tail -> {{literal}} :: {{parents}}(tail)

            let recursive {{append}} left right =
                match left with
                    | [] -> right
                    | head :: tail -> head :: {{append}}(tail)(right)

            let {{names.Entry}} (seed: Int) =
                (let parts = [{{names.Word()}}, "y" + Ashes.Text.fromInt(seed){{(names.Coin("third") ? ", \"z\"" : string.Empty)}}]
                in {{body}})
            """;
    }
}

/// <summary>
/// A deep copy of a call's owned result (<c>Ashes.Internal.deepCopy(Ashes.Text.drop(text)(2))</c>).
/// The copy stood in for the result, and the result, which nothing else held, was never released.
/// </summary>
internal sealed class DeepCopiedCallResultTemplate() : LeakCombinationTemplate(
    "leak.deep-copied-call-result", "deepCopiedCallResult", GeneratedFeature.Call, GeneratedFeature.Let)
{
    protected override string Declarations(LeakNames names)
    {
        string copy = names.Of("copied");
        int count = names.Between(1, 3);
        string call = names.Variant("producer", 3) switch
        {
            0 => $"Ashes.Text.drop(text)({LeakNames.Text(count)})",
            1 => $"Ashes.Text.take(text)({LeakNames.Text(count)})",
            _ => "Ashes.Text.trim(text)",
        };
        string use = names.Coin("listCopy")
            ? $"Ashes.Collection.List.length(Ashes.Internal.deepCopy(Ashes.Text.split({call})(\"/\")))"
            : $"Ashes.Text.length(Ashes.Internal.deepCopy({call}))";
        return $$"""
            let {{copy}} (text: Str) = {{use}}

            let {{names.Entry}} (seed: Int) = {{copy}}(" C:/a/b" + Ashes.Text.fromInt(seed) + " ")
            """;
    }
}

/// <summary>
/// A generic map whose closure returns a tuple of scalars (<c>List.map(given (p: (Int, Int)) -&gt;
/// (a + 1, c))(box.pairs)</c>). The closure hands its reference-counted tuple over owned; the
/// producer's cell retained it again, so every element kept one reference too many.
/// </summary>
internal sealed class MappedTupleClosureResultTemplate() : LeakCombinationTemplate(
    "leak.mapped-tuple-closure-result", "mappedTupleClosureResult", GeneratedFeature.Lambda, GeneratedFeature.Tuple, GeneratedFeature.Record)
{
    protected override string Declarations(LeakNames names)
    {
        string box = names.Of("LeakBox");
        string shift = names.Of("shift");
        string bump = names.Of("bump");
        string lambda = names.Coin("identity") ? "given (p: (Int, Int)) -> p" : $"given (p: (Int, Int)) -> {bump}(p)";
        string bumped = names.Coin("swap") ? "(c, a)" : $"(a + {LeakNames.Text(names.Between(1, 5))}, c)";
        string source = names.Coin("recordField") ? "box.pairs" : $"[(seed, 1), (2, seed)]";
        return $$"""
            type {{box}} =
                | pairs: List((Int, Int))
                | count: Int

            let {{bump}} (p: (Int, Int)) =
                match p with
                    | (a, c) -> {{bumped}}

            let {{shift}} (box: {{box}}) (seed: Int) = Ashes.Collection.List.map({{lambda}})({{source}})

            let {{names.Entry}} (seed: Int) =
                Ashes.Collection.List.length({{shift}}({{box}}(pairs = [(seed, {{LeakNames.Text(names.Between(1, 9))}}), (3, seed)], count = seed))(seed))
            """;
    }
}

/// <summary>
/// A loop over borrowed strings that keeps a stack of them, pushing a head onto the stack and
/// popping it by its consumed tail (<c>".." -&gt; match stack with | _top :: rest -&gt; keep(tail)(rest)</c>),
/// as a path normalizer does. Neither a choice accumulator nor a walked list, the stack stayed in
/// the arena, and the reference each push took of its head was never released.
/// </summary>
internal sealed class WalkedStackOfStringsTemplate() : LeakCombinationTemplate(
    "leak.walked-stack-of-strings", "walkedStackOfStrings", GeneratedFeature.RecursiveFunction, GeneratedFeature.Match, GeneratedFeature.Tuple)
{
    protected override string Declarations(LeakNames names)
    {
        string keep = names.Of("keep");
        string reverse = names.Of("reverseAll");
        string make = names.Of("make");
        string emptyPop = names.Coin("literalReset") ? $"{keep}(tail)([\"..\"])" : $"{keep}(tail)([])";
        string exit = names.Coin("reversed") ? $"{reverse}(stack)([])" : "stack";
        string doubled = names.Coin("doubledPop")
            ? $$"""
                if top == ".."
                                        then {{keep}}(tail)(".." :: stack)
                                        else {{keep}}(tail)(rest)
                """
            : $"{keep}(tail)(rest)";
        string path = names.Pick("\"/a/b/../c\"", "\"a/../b/c/d\"", "\"/x/y/z/../..\"");
        return $$"""
            let recursive {{reverse}} (values: List(Str)) (result: List(Str)) =
                match values with
                    | [] -> result
                    | head :: tail -> {{reverse}}(tail)(head :: result)

            let recursive {{keep}} (parts: List(Str)) (stack: List(Str)) =
                match parts with
                    | [] -> {{exit}}
                    | head :: tail ->
                        if head == ".."
                        then
                            match stack with
                                | [] -> {{emptyPop}}
                                | top :: rest ->
                                    {{doubled}}
                        else {{keep}}(tail)(head :: stack)

            let {{make}} (s: Str) = ("", Ashes.Text.split({{path}} + s)("/"))

            let {{names.Entry}} (seed: Int) =
                match {{make}}(Ashes.Text.fromInt(seed)) with
                    | (_prefix, parts) -> Ashes.Collection.List.length({{keep}}(parts)([]))
            """;
    }
}
