using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A loop over top-level items that hands each step's escape body, a value read out of its analysis
/// record and bound to no name, to one argument of a curried call whose result it matches. The
/// successors include a map of seen names and a state that could hold the value's parts, so the
/// value an earlier iteration left in its slot is released only once the curried chain's result is
/// known to be owned.
/// </summary>
internal sealed class CurriedChainPredecessorTemplate() : LeakCombinationTemplate(
    "leak.curried-chain-predecessor", "curriedChainPredecessor", GeneratedFeature.Adt, GeneratedFeature.Record, GeneratedFeature.RecordPattern, GeneratedFeature.TailCall, GeneratedFeature.Match, GeneratedFeature.List)
{
    protected override IReadOnlyList<string> RequiredImports => ["Ashes.Collection.Map"];

    protected override string Declarations(LeakNames names)
    {
        string expr = names.Of("Syntax");
        string at = names.Of("SyntaxAt");
        string name = names.Of("SyntaxName");
        string bind = names.Of("SyntaxLet");
        string call = names.Of("SyntaxCall");
        string analysis = names.Of("Analysis");
        string item = names.Of("Item");
        string itemAt = names.Of("ItemAt");
        string itemExpr = names.Of("ItemExpr");
        string itemFn = names.Of("ItemFn");
        string state = names.Of("LowerState");
        string opened = names.Of("Opened");
        string check = names.Of("NameCheck");
        string peel = names.Of("peel");
        string peelAnalysis = names.Of("peelAnalysis");
        string size = names.Of("size");
        string escapeSize = names.Of("escapeSize");
        string count = names.Of("count");
        string escapeBody = names.Of("escapeBody");
        string itemSize = names.Of("itemSize");
        string checkNames = names.Of("checkNames");
        string open = names.Of("openChain");
        string lower = names.Of("lowerItems");
        string build = names.Of("build");
        int depth = names.Between(2, 4);
        bool chainEscape = names.Coin("chain-escape");
        string escapeField = chainEscape ? "escapeOfChain" : "escapeOfTrailing";
        return $$"""
            type {{expr}} =
                | {{at}}(Int, {{expr}})
                | {{name}}(Str)
                | {{bind}}(Str, {{expr}}, {{expr}})
                | {{call}}({{expr}}, {{expr}})

            type {{analysis}} =
                | remainingBody: {{expr}}
                | escapeOfChain: Maybe({{expr}})
                | escapeOfTrailing: Maybe({{expr}})

            type {{item}} =
                | {{itemAt}}(Int, {{item}})
                | {{itemExpr}}({{expr}})
                | {{itemFn}}(Int -> Int)

            type {{state}} =
                | emitted: List(Str)
                | counter: Int
                | request: Maybe({{expr}})

            type {{opened}} =
                | chainState: {{state}}
                | chainPending: List(Str)
                | chainError: Maybe(Str)

            type {{check}} =
                | seenNames: MapTree(Str, Bool)
                | duplicate: Maybe(Str)

            let recursive {{peel}} (count: Int) (body: {{expr}}) =
                if count <= 0
                then body
                else
                    match body with
                        | {{bind}}(_label, _value, next) -> {{peel}}(count - 1)(next)
                        | _ -> body

            let {{peelAnalysis}} (count: Int) (analysis: {{analysis}}) = analysis with remainingBody = {{peel}}(count)(analysis.remainingBody)

            let recursive {{size}} (expression: {{expr}}) =
                match expression with
                    | {{at}}(_span, inner) -> 1 + {{size}}(inner)
                    | {{bind}}(_label, value, body) -> {{size}}(value) + {{size}}(body)
                    | {{call}}(callee, argument) -> {{size}}(callee) + {{size}}(argument)
                    | _ -> 1

            let {{escapeSize}} (escape: Maybe({{expr}})) =
                match escape with
                    | Some(found) -> {{size}}(found)
                    | None -> 0

            let recursive {{count}} (labels: List(Str)) =
                match labels with
                    | [] -> 0
                    | _label :: rest -> 1 + {{count}}(rest)

            let {{escapeBody}} (analysis: {{analysis}}) =
                match analysis.remainingBody with
                    | {{bind}}(_label, _value, _nested) -> analysis.{{escapeField}}
                    | _ -> analysis.escapeOfTrailing

            let {{itemSize}} (entry: {{item}}) =
                match entry with
                    | {{itemExpr}}(expression) -> {{size}}(expression)
                    | {{itemFn}}(f) -> f(1)
                    | {{itemAt}}(_span, _inner) -> 1

            let recursive {{checkNames}} (labels: List(Str)) (seen: MapTree(Str, Bool)) =
                match labels with
                    | [] -> {{check}}(seenNames = seen, duplicate = None)
                    | label :: rest ->
                        match Ashes.Collection.Map.getStr(label)(seen) with
                            | Some(_declared) -> {{check}}(seenNames = seen, duplicate = Some(label))
                            | None ->
                                seen
                                |> Ashes.Collection.Map.setStr(label)(true)
                                |> {{checkNames}}(rest)

            let {{open}} label value remaining (escape: Maybe({{expr}})) (forwarded: Bool) environment (pending: List(Str)) (state: {{state}}) =
                if forwarded
                then {{opened}}(chainState = state, chainPending = pending, chainError = Some(label))
                else
                    let requested = state with counter = state.counter + {{escapeSize}}(escape) + {{size}}(value) + {{size}}(remaining) + environment
                    in {{opened}}(chainState = (requested with emitted = label :: requested.emitted), chainPending = label :: pending, chainError = None)

            let recursive {{lower}} (items: List({{item}})) trailing (seen: MapTree(Str, Bool)) environment (analysis: {{analysis}}) (pending: List(Str)) (state: {{state}}) =
                match items with
                    | [] -> state.counter + {{count}}(state.emitted) + {{size}}(analysis.remainingBody)
                    | {{itemAt}}(_span, inner) :: rest -> {{lower}}(inner :: rest)(trailing)(seen)(environment)(analysis)(pending)(state)
                    | entry :: rest ->
                        match {{checkNames}}([Ashes.Text.fromInt({{count}}(pending))])(seen) with
                            | {{check}} { duplicate = Some(_label) } -> 0
                            | {{check}} { seenNames = nextSeen, duplicate = None } ->
                                let nextAnalysis = {{peelAnalysis}}(1)(analysis)
                                in
                                    match {{open}}({{names.Word()}})(trailing)(nextAnalysis.remainingBody)({{escapeBody}}(nextAnalysis))({{itemSize}}(entry) > 50)(environment)(pending)(state) with
                                        | {{opened}} { chainState = failed, chainError = Some(_error) } -> failed.counter
                                        | {{opened}} { chainState = next, chainPending = nextPending, chainError = None } -> {{lower}}(rest)(trailing)(nextSeen)(environment)(nextAnalysis)(nextPending)(next)

            let recursive {{build}} (k: Int) (seed: Int) =
                if k == 0
                then {{call}}({{at}}(seed, {{name}}({{names.Word()}})), {{name}}({{names.Word()}}))
                else {{bind}}({{names.Word()}}, {{name}}({{names.Word()}}), {{build}}(k - 1)(seed))

            let {{names.Entry}} (seed: Int) =
                let trailing = {{at}}(seed, {{call}}({{name}}({{names.Word()}}), {{at}}(1, {{name}}({{names.Word()}}))))
                in
                    let analysis = {{analysis}}(remainingBody = {{build}}({{LeakNames.Text(depth)}})(seed), escapeOfChain = Some(trailing), escapeOfTrailing = Some(trailing))
                    in {{lower}}([{{itemExpr}}({{name}}({{names.Word()}})), {{itemAt}}(1, {{itemFn}}(given (x) -> x + seed)), {{itemExpr}}({{name}}({{names.Word()}}))])(trailing)(Ashes.Collection.Map.empty)(seed)(analysis)([])({{state}}(emitted = [], counter = 0, request = None))
            """;
    }
}
