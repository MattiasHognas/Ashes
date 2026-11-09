using System.Text.RegularExpressions;
using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A function builds its result as an arena record whose <c>Maybe</c> field joins a fresh
/// <c>None</c> with a retain of a slot read through its borrowed state, the way stage 1's
/// <c>CoreArgumentHandOff</c> takes <c>pendingRootSlot</c>. The result's copy onto the
/// reference-counted heap retains the field again, so the arena record's own reference moves into
/// the copy; before, it stayed behind once per call.
/// </summary>
internal sealed partial class ArenaRecordOwnedFieldTemplate() : LeakCombinationTemplate(
    "leak.arena-record-owned-field", "arenaRecordOwnedField", GeneratedFeature.Record, GeneratedFeature.Match, GeneratedFeature.TailCall)
{
    private static readonly string[] Identifiers =
    [
        "Frame", "Loop", "Group", "State", "Expr", "ExprVar", "ExprNum", "HandOff",
        "stateTcoLoop", "stateTcoLoopFrame", "slotIn", "patternBindingRootSlot", "argumentRootSlotOf",
        "handOff", "makeState", "score", "loop",
    ];

    protected override string Declarations(LeakNames names) => Render(names, readBeforeReturn: false);

    internal static string Render(LeakNames names, bool readBeforeReturn, bool replaceField = false, string noneArm = "built")
    {
        string someArm = replaceField
            ? "if s > 2 then (built with borrowsOnly = true) else (built with pendingRootSlot = None)"
            : "if s > 1000 then (built with borrowsOnly = true) else built";
        string source = readBeforeReturn
            ? Program
                .Replace("\n            HandOff(\n", "\n            let built = HandOff(\n", StringComparison.Ordinal)
                .Replace(
                    "pendingRootSlot = if runtimeArgument then None else rootSlot\n            )\n",
                    "pendingRootSlot = if runtimeArgument then None else rootSlot\n            )\n            in match built.pendingRootSlot with\n                | Some(s) -> " + someArm + "\n                | None -> " + noneArm + "\n",
                    StringComparison.Ordinal)
            : Program;
        string program = source.Replace("ENTRY", names.Entry, StringComparison.Ordinal)
            .Replace("WIDTH", names.Between(3, 9).ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
        Dictionary<string, string> renamed = new(StringComparer.Ordinal);
        foreach (string identifier in Identifiers)
        {
            renamed[identifier] = names.Of(identifier);
        }

        return IdentifierPattern().Replace(program, match => renamed.TryGetValue(match.Value, out string? name) ? name : match.Value);
    }

    [GeneratedRegex(@"\b[A-Za-z]+\b")]
    private static partial Regex IdentifierPattern();

    private const string Program = """
        type Frame =
            | bodyLabel: Str
            | parameterSlots: List((Str, Int))

        type Loop =
            | name: Str
            | cached: Maybe(Int)

        type Group =
            | tcoLoop: Maybe(Loop)
            | tcoLoopFrame: Maybe(Frame)

        type State =
            | tcoState: Group
            | names: List(Str)

        let stateTcoLoop (state: State) =
            (let group = state.tcoState
            in group.tcoLoop)

        let stateTcoLoopFrame (state: State) =
            (let group = state.tcoState
            in group.tcoLoopFrame)

        type Expr =
            | ExprVar(Str)
            | ExprNum(Int)

        let recursive slotIn (name: Str) (entries: List((Str, Int))) =
            match entries with
                | [] -> None
                | (key, slot) :: rest ->
                    if key == name
                    then Some(slot)
                    else slotIn(name)(rest)

        let patternBindingRootSlot (name: Str) (loop: Loop) = loop.cached

        let argumentRootSlotOf (argument: Expr) (state: State) =
            match (stateTcoLoopFrame(state), stateTcoLoop(state), argument) with
                | (Some(frame), Some(loop), ExprVar(name)) ->
                    match slotIn(name)(frame.parameterSlots) with
                        | Some(slot) -> Some(slot)
                        | None -> patternBindingRootSlot(name)(loop)
                | _ -> None

        type HandOff =
            | borrowsOnly: Bool
            | runtimeArgument: Bool
            | mayReach: Bool
            | borrowedReach: Bool
            | pendingRootSlot: Maybe(Int)

        let handOff (argument: Expr) (state: State) =
            match (Ashes.Collection.List.length(state.names) > 3, argumentRootSlotOf(argument)(state), Ashes.Collection.List.length(state.names) > 5) with
                | (runtimeArgument, rootSlot, fresh) ->
                    HandOff(
                        borrowsOnly = false,
                        runtimeArgument = runtimeArgument,
                        mayReach = rootSlot != None,
                        borrowedReach = rootSlot != None && !fresh,
                        pendingRootSlot = if runtimeArgument then None else rootSlot
                    )

        let makeState (n: Int) =
            State(
                tcoState = Group(tcoLoop = Some(Loop(name = "l" + Ashes.Text.fromInt(n), cached = if n % 2 == 0 then None else Some(n))), tcoLoopFrame = Some(Frame(bodyLabel = "body", parameterSlots = [("a", 0)]))),
                names = ["a" + Ashes.Text.fromInt(n)]
            )

        let score (handOff: HandOff) =
            match handOff.pendingRootSlot with
                | Some(slot) -> slot + 1
                | None ->
                    if handOff.borrowedReach
                    then 2
                    else 3

        let recursive loop (n: Int) (acc: Int) =
            if n == 0
            then acc
            else
                let state = makeState(n)
                in loop(n - 1)(acc + score(handOff(ExprVar("z"))(state)) + score(handOff(ExprVar("a"))(state)))

        let ENTRY (seed: Int) = loop(WIDTH)(seed)
        """;
}

/// <summary>
/// The same function binds the arena record, reads its <c>Maybe</c> field, and returns either the
/// record or an update of it that copies that field without a retain. The field is the same
/// reference on both arms, so it moves into the result's copy whichever arm returned.
/// </summary>
internal sealed class ArenaRecordReadBeforeReturnTemplate() : LeakCombinationTemplate(
    "leak.arena-record-read-before-return", "arenaRecordReadBeforeReturn", GeneratedFeature.Record, GeneratedFeature.Match, GeneratedFeature.TailCall)
{
    protected override string Declarations(LeakNames names) => ArenaRecordOwnedFieldTemplate.Render(names, readBeforeReturn: true);
}

/// <summary>
/// The same function returns either an update that copies the retained field or one that replaces
/// it with <c>None</c>. The record's own reference to the field is released once, from the record,
/// after the result's copy: the copy retains the field where it kept it, and nothing else holds it
/// where it was replaced.
/// </summary>
internal sealed class ArenaRecordUpdateReplacingFieldTemplate() : LeakCombinationTemplate(
    "leak.arena-record-update-replacing-field", "arenaRecordUpdateReplacingField", GeneratedFeature.Record, GeneratedFeature.Match, GeneratedFeature.TailCall)
{
    protected override string Declarations(LeakNames names) => ArenaRecordOwnedFieldTemplate.Render(names, readBeforeReturn: true, replaceField: true);
}

/// <summary>
/// The same function's other arm returns another record of the type: a fresh one, or one assembled
/// from the record's fields with the retained field replaced. No arm returns the let-bound record
/// itself, so its own reference to the field is released from it once after the result's copy.
/// </summary>
internal sealed class ArenaRecordDerivedJoinTemplate() : LeakCombinationTemplate(
    "leak.arena-record-derived-join", "arenaRecordDerivedJoin", GeneratedFeature.Record, GeneratedFeature.Match, GeneratedFeature.TailCall)
{
    protected override string Declarations(LeakNames names)
        => ArenaRecordOwnedFieldTemplate.Render(
            names,
            readBeforeReturn: true,
            replaceField: true,
            noneArm: names.Coin("fresh-arm")
                ? "HandOff(borrowsOnly = true, runtimeArgument = false, mayReach = false, borrowedReach = false, pendingRootSlot = None)"
                : "HandOff(borrowsOnly = built.borrowsOnly, runtimeArgument = built.runtimeArgument, mayReach = built.mayReach, borrowedReach = built.borrowedReach, pendingRootSlot = Some(7))");
}
