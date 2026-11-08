using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// The rewrite collection of stage 1's currying-stage inlining: a loop retargets the next call of each
/// stage chain (a fresh record holding a <c>List(Int)</c> by its type) and threads it into an
/// association list, beside a <c>List(Int)</c> parameter grown by <c>position :: removed</c>. That
/// successor could hold a <c>List(Int)</c> part of the record by its type alone, so the back edge kept
/// the retargeted record's slot, and every iteration overwrote it: one record per rewritten call. Its
/// new cells hold only scalars over the parameter's older value, so they hold no part of it.
/// </summary>
internal sealed class InlineConsOverParameterTemplate() : LeakCombinationTemplate(
    "leak.inline-cons-over-parameter", "inlineConsOverParameter", GeneratedFeature.Match, GeneratedFeature.List)
{
    protected override string Declarations(LeakNames names)
    {
        string loc = names.Of("Loc");
        string kind = names.Of("Kind");
        string inst = names.Of("IrInstruction");
        string lookup = names.Of("lookupAssociation");
        string set = names.Of("setAssociation");
        string retarget = names.Of("retarget");
        string collect = names.Of("collect");
        string rebuild = names.Of("rebuild");
        string index = names.Of("indexInstructions");
        string once = names.Of("once");
        string weigh = names.Of("weigh");
        string callKnown = names.Of("CallKnown");
        string loadEnv = names.Of("LoadEnv");
        string other = names.Of("Other");
        string @switch = names.Of("Switch");
        int extra = names.Between(3, 5);
        string calls = string.Join(", ", Enumerable.Range(0, extra).Select(i =>
            $"{inst}(instruction = {callKnown}({(20 + i).ToString(System.Globalization.CultureInfo.InvariantCulture)}, {names.Word()}, 1, 2, 3, false), location = Some({loc}(filePath = {names.Word()} + Ashes.Text.fromInt(seed), line = seed)))"));
        return $$"""
            type {{loc}} =
                | filePath: Str
                | line: Int

            type {{kind}} =
                | {{callKnown}}(Int, Str, Int, Int, Int, Bool)
                | {{loadEnv}}(Int, Int)
                | {{other}}(Int)
                | {{@switch}}(Int, List(Int))

            type {{inst}} =
                | instruction: {{kind}}
                | location: Maybe({{loc}})

            let recursive {{lookup}} key entries =
                match entries with
                    | [] -> None
                    | (k, v) :: tail ->
                        if k == key
                        then Some(v)
                        else {{lookup}}(key)(tail)

            let recursive {{set}} key value entries =
                match entries with
                    | [] -> [(key, value)]
                    | (k, v) :: tail ->
                        if k == key
                        then (key, value) :: tail
                        else (k, v) :: {{set}}(key)(value)(tail)

            let {{retarget}} (stackEnv: Int) (stackAllocated: Bool) irInst =
                match irInst with
                    | {{inst}} { instruction = {{callKnown}}(dest, label, _, argTemp, flagTemp, _), location = loc } -> {{inst}}(instruction = {{callKnown}}(dest, label, stackEnv, argTemp, flagTemp, stackAllocated), location = loc)
                    | other -> other

            let recursive {{collect}} indexed remaining (nextTemp: Int) expansions (removed: List(Int)) rewrites =
                match remaining with
                    | [] -> (expansions, removed, rewrites, nextTemp)
                    | (position, irInst) :: tail ->
                        match irInst with
                            | {{inst}} { instruction = {{callKnown}}(resultTemp, stageLabel, envTemp, argTemp, _, _), location = loc } ->
                                match {{lookup}}(position)(rewrites) with
                                    | Some(_) -> {{collect}}(indexed)(tail)(nextTemp)(expansions)(removed)(rewrites)
                                    | None ->
                                        let callPosition = position + 1
                                        in
                                            let replacement = [{{inst}}(instruction = {{other}}(envTemp), location = loc)]
                                            in
                                                let retargeted =
                                                    match {{lookup}}(callPosition)(indexed) with
                                                        | Some(nextCall) -> {{retarget}}(nextTemp)(resultTemp > 0)(nextCall)
                                                        | None -> irInst
                                                in {{collect}}(indexed)(tail)(nextTemp + 1)({{set}}(position)(replacement)(expansions))(position :: removed)({{set}}(callPosition)(retargeted)(rewrites))
                            | _ -> {{collect}}(indexed)(tail)(nextTemp)(expansions)(removed)(rewrites)

            let recursive {{rebuild}} indexed expansions (removed: List(Int)) rewrites acc =
                match indexed with
                    | [] -> Ashes.Collection.List.reverse(acc)
                    | (position, irInst) :: tail ->
                        match {{lookup}}(position)(expansions) with
                            | Some(replacement) -> {{rebuild}}(tail)(expansions)(removed)(rewrites)(Ashes.Collection.List.append(acc)(replacement))
                            | None ->
                                match {{lookup}}(position)(rewrites) with
                                    | Some(rewritten) -> {{rebuild}}(tail)(expansions)(removed)(rewrites)(rewritten :: acc)
                                    | None -> {{rebuild}}(tail)(expansions)(removed)(rewrites)(irInst :: acc)

            let recursive {{index}} (insts: List({{inst}})) (n: Int) acc =
                match insts with
                    | [] -> Ashes.Collection.List.reverse(acc)
                    | i :: rest -> {{index}}(rest)(n + 1)((n, i) :: acc)

            let {{once}} (insts: List({{inst}})) (tempCount: Int) =
                (let indexed = {{index}}(insts)(0)([])
                in
                    match {{collect}}(indexed)(indexed)(tempCount)([])([])([]) with
                        | ([], _, _, _) -> (insts, false)
                        | (expansions, removed, rewrites, nextTemp) -> ({{rebuild}}(indexed)(expansions)(removed)(rewrites)([]), nextTemp > 0))

            let recursive {{weigh}} (insts: List({{inst}})) =
                match insts with
                    | [] -> 0
                    | {{inst}} { instruction = {{callKnown}}(_, label, _, _, _, _) } :: rest -> Ashes.Text.byteLength(label) + {{weigh}}(rest)
                    | _ :: rest -> 1 + {{weigh}}(rest)

            let {{names.Entry}} (seed: Int) =
                (let insts = [{{inst}}(instruction = {{callKnown}}(1, "s" + Ashes.Text.fromInt(seed), 2, 3, 4, false), location = Some({{loc}}(filePath = "a", line = seed))), {{calls}}, {{inst}}(instruction = {{callKnown}}(11, "w", 1, 2, 3, false), location = None), {{inst}}(instruction = {{other}}(seed), location = None)]
                in
                    match {{once}}(insts)(10) with
                        | (rewritten, _) -> {{weigh}}(rewritten))
            """;
    }
}
