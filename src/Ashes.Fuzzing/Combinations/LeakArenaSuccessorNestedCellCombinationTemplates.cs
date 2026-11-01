using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A loop over strings whose successor is a tree node built in the arena around the loop's own
/// tree parameter and a fresh constructor cell holding the matched head (<c>Node(seen, Item(name))</c>).
/// The back edge copies the node with a normalizer, which takes its own reference to the head
/// through the nested cell: the retain the nested cell was built with is released after that copy.
/// </summary>
internal sealed class NestedCellInArenaSuccessorTemplate() : LeakCombinationTemplate(
    "leak.nested-cell-in-arena-successor", "nestedCellSuccessor", GeneratedFeature.Adt, GeneratedFeature.Match, GeneratedFeature.List)
{
    protected override string Declarations(LeakNames names)
    {
        string item = names.Of("NestItem");
        string plain = names.Of("NestPlain");
        string tree = names.Of("NestTree");
        string leaf = names.Of("NestLeaf");
        string node = names.Of("NestNode");
        string loop = names.Of("nestLoop");
        string size = names.Of("nestSize");
        bool leftFirst = names.Coin("tree-first");
        string fields = leftFirst ? $"{tree}, {item}" : $"{item}, {tree}";
        string built = leftFirst ? $"{node}(seen, {plain}(name))" : $"{node}({plain}(name), seen)";
        string pattern = leftFirst ? $"{node}(rest, _item)" : $"{node}(_item, rest)";
        string words = names.Words(names.Between(1, 4));
        string exit = names.Coin("sized-exit") ? $"count + {size}(seen)" : "count";
        return $$"""
            type {{item}} =
                | {{plain}}(Str)

            type {{tree}} =
                | {{leaf}}
                | {{node}}({{fields}})

            let recursive {{size}} (tree: {{tree}}) =
                match tree with
                    | {{leaf}} -> 0
                    | {{pattern}} -> 1 + {{size}}(rest)

            let recursive {{loop}} (items: List(Str)) (seen: {{tree}}) (count: Int) =
                match items with
                    | [] -> {{exit}}
                    | name :: rest -> {{loop}}(rest)({{built}})(count + 1)

            let {{names.Entry}} (seed: Int) =
                {{loop}}({{words}})({{leaf}})(seed)
            """;
    }
}

/// <summary>
/// A loop that inserts each matched name into a generic tree through a call and hands the call's result on
/// as its tree parameter, beside an arm that conses a wrapped item back onto the list. The list
/// arm keeps the loop in the arena; the earlier iteration's tree, no name bound to it, is released
/// at the back edge once the successors are shown not to name it.
/// </summary>
internal sealed class UnnamedPredecessorInArenaLoopTemplate() : LeakCombinationTemplate(
    "leak.unnamed-predecessor-in-arena-loop", "unnamedPredecessor", GeneratedFeature.Adt, GeneratedFeature.Match, GeneratedFeature.List)
{
    protected override string Declarations(LeakNames names)
    {
        string item = names.Of("PredItem");
        string plain = names.Of("PredPlain");
        string wrapped = names.Of("PredWrapped");
        string tree = names.Of("PredTree");
        string leaf = names.Of("PredLeaf");
        string node = names.Of("PredNode");
        string insert = names.Of("predInsert");
        string loop = names.Of("predLoop");
        string seen = names.Coin("annotated-tree") ? $"(seen: {tree}(Str, {item}))" : "seen";
        string value = names.Coin("item-value") ? $"{plain}(name)" : $"{wrapped}({plain}(name))";
        IReadOnlyList<string> words = names.DistinctWords(names.Between(3, 5));
        string items = "[" + string.Join(", ", words.Select((word, index) => index == 0 && names.Coin("wrapped-first") ? $"{wrapped}({plain}({word}))" : $"{plain}({word})")) + "]";
        return $$"""
            type {{item}} =
                | {{plain}}(Str)
                | {{wrapped}}({{item}})

            type {{tree}}(K, V) =
                | {{leaf}}
                | {{node}}({{tree}}(K, V), K, V, {{tree}}(K, V))

            let recursive {{insert}} (key: Str) (value: {{item}}) (tree: {{tree}}(Str, {{item}})) =
                match tree with
                    | {{leaf}} -> {{node}}({{leaf}}, key, value, {{leaf}})
                    | {{node}}(left, k, v, right) -> {{node}}({{insert}}(key)(value)(left), k, v, right)

            let recursive {{loop}} (items: List({{item}})) {{seen}} (count: Int) =
                match items with
                    | [] -> count
                    | {{wrapped}}(inner) :: rest -> {{loop}}(inner :: rest)(seen)(count)
                    | {{plain}}(name) :: rest -> {{loop}}(rest)({{insert}}(name)({{value}})(seen))(count + 1)

            let {{names.Entry}} (seed: Int) =
                {{loop}}({{items}})({{leaf}})(seed)
            """;
    }
}

/// <summary>
/// A loop that hands a fresh constructor cell holding the matched head to a call (<c>insert(Item(name))(seen)</c>)
/// and takes the call's result as its next tree. The cell is an arena call argument, not a
/// successor: it borrows the head, which its pattern owner releases after the call.
/// </summary>
internal sealed class PatternOwnerInCallArgumentCellTemplate() : LeakCombinationTemplate(
    "leak.pattern-owner-in-call-argument-cell", "callArgumentCell", GeneratedFeature.Adt, GeneratedFeature.Match, GeneratedFeature.List)
{
    protected override string Declarations(LeakNames names)
    {
        string item = names.Of("ArgItem");
        string plain = names.Of("ArgPlain");
        string tree = names.Of("ArgTree");
        string leaf = names.Of("ArgLeaf");
        string node = names.Of("ArgNode");
        string insert = names.Of("argInsert");
        string loop = names.Of("argLoop");
        bool rightLeaning = names.Coin("right-leaning");
        string recurse = rightLeaning
            ? $"{node}(left, v, {insert}(value)(right))"
            : $"{node}({insert}(value)(left), v, right)";
        string words = names.Words(names.Between(1, 4));
        return $$"""
            type {{item}} =
                | {{plain}}(Str)

            type {{tree}} =
                | {{leaf}}
                | {{node}}({{tree}}, {{item}}, {{tree}})

            let recursive {{insert}} (value: {{item}}) (tree: {{tree}}) =
                match tree with
                    | {{leaf}} -> {{node}}({{leaf}}, value, {{leaf}})
                    | {{node}}(left, v, right) -> {{recurse}}

            let recursive {{loop}} (items: List(Str)) (seen: {{tree}}) (count: Int) =
                match items with
                    | [] -> count
                    | name :: rest -> {{loop}}(rest)({{insert}}({{plain}}(name))(seen))(count + 1)

            let {{names.Entry}} (seed: Int) =
                {{loop}}({{words}})({{leaf}})(seed)
            """;
    }
}
