// expect: 9
// A loop over capture sites keeps its agreed label in the arena beside a list of records holding a
// recursive type, and rebuilds it as Some(label) from a label bound out of a call result the
// iteration releases. The label the Some took for the next iteration is copied into the arena
// and its own reference released, so no label survives the loop.
type IrInstructionKind =
    | LoadLocal(Int, Int)
    | LoadEnv(Int, Int)
    | MakeClosure(Int, Str, Int, Int, Bool, Bool, Bool, Bool)
    | MakeClosureStack(Int, Str, Int, Int, Bool, Bool, Bool)
    | CallKnown(Int, Str, Int, Int, Int, Bool)
    | Borrow(Int, Int)
    | Other(Int)
    | Switch(Int, List(Int), Str)
    | Phi(List((Str, Int)))
    | Nested(IrInstructionKind)

type ClosureDefinitionFacts =
    | defCounts: List((Int, Int))
    | useCounts: List((Int, Int))
    | singleDefs: List((Int, IrInstructionKind))
    | storeCountBySlot: List((Int, Int))
    | singleStoreSourceBySlot: List((Int, Int))
    | closureTempsWithDropper: List(Int)

type CaptureSite =
    | targetLabel: Str
    | index: Int
    | creatorLabel: Str
    | creator: ClosureDefinitionFacts
    | sourceTemp: Int

type CaptureResolution =
    | CaptureKnown(Str)
    | CapturePending
    | CaptureUnknown

let recursive lookupAssociation key entries =
    match entries with
        | [] -> None
        | (k, v) :: tail ->
            if k == key
            then Some(v)
            else lookupAssociation(key)(tail)

let recursive lookupSlotEntry (label: Str) (index: Int) entries =
    match entries with
        | [] -> None
        | ((candidateLabel, candidateIndex), value) :: tail ->
            if candidateLabel == label
            then
                if candidateIndex == index
                then Some(value)
                else lookupSlotEntry(label)(index)(tail)
            else lookupSlotEntry(label)(index)(tail)

let recursive setSlotEntry (label: Str) (index: Int) value entries =
    match entries with
        | [] -> [((label, index), value)]
        | ((candidateLabel, candidateIndex), existing) :: tail ->
            if candidateLabel == label
            then
                if candidateIndex == index
                then ((label, index), value) :: tail
                else ((candidateLabel, candidateIndex), existing) :: setSlotEntry(label)(index)(value)(tail)
            else ((candidateLabel, candidateIndex), existing) :: setSlotEntry(label)(index)(value)(tail)

let recursive containsSlot (label: Str) (index: Int) (slots: List((Str, Int))) =
    match slots with
        | [] -> false
        | (candidateLabel, candidateIndex) :: tail ->
            if candidateLabel == label
            then
                if candidateIndex == index
                then true
                else containsSlot(label)(index)(tail)
            else containsSlot(label)(index)(tail)

// The sites grouped by the (label, word) pair they fill.
let recursive groupCaptureSites (sites: List(CaptureSite)) (groups: List(((Str, Int), List(CaptureSite)))) =
    match sites with
        | [] -> groups
        | site :: tail ->
            let members =
                match lookupSlotEntry(site.targetLabel)(site.index)(groups) with
                    | Some(existing) -> site :: existing
                    | None -> [site]
            in
                groups
                |> setSlotEntry(site.targetLabel)(site.index)(members)
                |> groupCaptureSites(tail)

let recursive resolveCaptureLabel (creatorLabel: Str) (facts: ClosureDefinitionFacts) (temp: Int) known knownReturned (depth: Int) =
    if depth > 16
    then CaptureUnknown
    else
        match lookupAssociation(temp)(facts.singleDefs) with
            | Some(MakeClosure(_, label, _, _, _, _, _, _)) -> CaptureKnown(label)
            | Some(MakeClosureStack(_, label, _, _, _, _, _)) -> CaptureKnown(label)
            | Some(Borrow(_, source)) -> resolveCaptureLabel(creatorLabel)(facts)(source)(known)(knownReturned)(depth + 1)
            | Some(LoadLocal(_, slot)) ->
                if lookupAssociation(slot)(facts.storeCountBySlot) == Some(1)
                then
                    match lookupAssociation(slot)(facts.singleStoreSourceBySlot) with
                        | Some(source) -> resolveCaptureLabel(creatorLabel)(facts)(source)(known)(knownReturned)(depth + 1)
                        | None -> CaptureUnknown
                else CaptureUnknown
            | Some(LoadEnv(_, index)) ->
                match lookupSlotEntry(creatorLabel)(index)(known) with
                    | Some(label) -> CaptureKnown(label)
                    | None -> CapturePending
            | Some(CallKnown(_, callee, _, _, _, _)) ->
                match lookupAssociation(callee)(knownReturned) with
                    | Some(label) -> CaptureKnown(label)
                    | None -> CaptureUnknown
            | _ -> CaptureUnknown

// A group is known once every site resolves to the same label; one unresolvable or disagreeing
// site settles it as unknown, and an unresolved captured word of a creator leaves it pending.
let recursive resolveCaptureGroup (sites: List(CaptureSite)) known knownReturned (agreed: Maybe(Str)) (pending: Bool) =
    match sites with
        | [] ->
            if pending
            then CapturePending
            else
                match agreed with
                    | Some(label) -> CaptureKnown(label)
                    | None -> CapturePending
        | site :: tail ->
            match resolveCaptureLabel(site.creatorLabel)(site.creator)(site.sourceTemp)(known)(knownReturned)(0) with
                | CaptureUnknown -> CaptureUnknown
                | CapturePending -> resolveCaptureGroup(tail)(known)(knownReturned)(agreed)(true)
                | CaptureKnown(label) ->
                    match agreed with
                        | None -> resolveCaptureGroup(tail)(known)(knownReturned)(Some(label))(pending)
                        | Some(previous) ->
                            if previous == label
                            then resolveCaptureGroup(tail)(known)(knownReturned)(agreed)(pending)
                            else CaptureUnknown

let recursive resolveCaptureGroups (groups: List(((Str, Int), List(CaptureSite)))) known (conflicting: List((Str, Int))) knownReturned (changed: Bool) =
    match groups with
        | [] -> (known, conflicting, changed)
        | ((label, index), sites) :: tail ->
            if containsSlot(label)(index)(conflicting)
            then resolveCaptureGroups(tail)(known)(conflicting)(knownReturned)(changed)
            else
                match lookupSlotEntry(label)(index)(known) with
                    | Some(_) -> resolveCaptureGroups(tail)(known)(conflicting)(knownReturned)(changed)
                    | None ->
                        match resolveCaptureGroup(sites)(known)(knownReturned)(None)(false) with
                            | CaptureUnknown -> resolveCaptureGroups(tail)(known)((label, index) :: conflicting)(knownReturned)(true)
                            | CaptureKnown(resolved) ->
                                resolveCaptureGroups(tail)(setSlotEntry(label)(index)(resolved)(known))(conflicting)(knownReturned)(true)
                            | CapturePending -> resolveCaptureGroups(tail)(known)(conflicting)(knownReturned)(changed)

// Whole-program fixpoint over the capture graph: each pass settles the groups whose sites resolve
// against the words known so far, so a chain of captured aliases converges while a word that
// depends on an unresolvable one stays pending and is never rewritten.
let recursive computeKnownCapturedClosureLabels groups known conflicting knownReturned =
    match resolveCaptureGroups(groups)(known)(conflicting)(knownReturned)(false) with
        | (nextKnown, nextConflicting, true) -> computeKnownCapturedClosureLabels(groups)(nextKnown)(nextConflicting)(knownReturned)
        | (nextKnown, _, false) -> nextKnown

let probe (n: Int) =
    (let label = "lambda_" + Ashes.Text.fromInt(n % 10)
    in
        let facts = ClosureDefinitionFacts(defCounts = [], useCounts = [], singleDefs = [(1, MakeClosure(1)(label)(0)(0)(false)(false)(false)(false)), (2, Borrow(2)(1)), (3, LoadEnv(3)(0))], storeCountBySlot = [], singleStoreSourceBySlot = [], closureTempsWithDropper = [])
        in
            let sites = [CaptureSite(targetLabel = "f1", index = 0, creatorLabel = "main", creator = facts, sourceTemp = 2), CaptureSite(targetLabel = "f2", index = 0, creatorLabel = "f1", creator = facts, sourceTemp = 3), CaptureSite(targetLabel = "f3", index = 1, creatorLabel = "main", creator = facts, sourceTemp = 1)]
            in
                []
                |> computeKnownCapturedClosureLabels(groupCaptureSites(sites)([]))([])([])
                |> Ashes.Collection.List.length)

let recursive loop (n: Int) (acc: Int) =
    if n == 0
    then acc
    else loop(n - 1)(acc + probe(n))
in
    0
    |> loop(3)
    |> Ashes.Text.fromInt
    |> Ashes.IO.print
