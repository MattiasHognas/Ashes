// Synthesizes the to-space deep copiers stage 0 places behind a label
// (`SynthesizeListToSpaceCopier` and `TrySynthesizeAdtToSpaceCopier`), the analogue of
// `StructuralCopiers.ash`'s arena copiers for a value that must survive not one arena reset but
// every one of them: a reuse specialization's rebuilt cell keeps its fields for as long as the
// accumulator lives, while the loop's back edge rewinds the arena on every iteration.
//
// There is no primitive that allocates an arbitrary cell directly in to-space — `AllocAdtToSpace`
// is reserved for in-place reuse's own fixed-shape node arena, and interleaving unrelated cells
// into it would corrupt that mechanism — so every copier here follows stage 0's shape: build the
// cell as ordinary arena scratch with its fields already relocated, then flat-copy the finished
// cell into the persistent blob with `CopyOutArenaToSpace`'s fixed-size branch. The scratch cell's
// own later reclaim is harmless, it has been copied by then.
//
// Invariants:
// - A copier is synthesized once per pretty-printed type, under a key namespaced away from the
//   arena copier of the same type; the label is registered before the body is emitted, so a
//   recursive or mutually recursive type finds its own label instead of recursing forever.
// - A copier's default arm returns the value itself, an unreachable fallback for a tag no
//   constructor carries.
// - Every copier is a complete `IrFunction` with env-and-arg parameters (slot 0 the environment,
//   slot 1 the value), built from its own temp and local counters.
// - A type shape the walk cannot relocate passes through unchanged. `toSpaceCopySafeType` is the
//   predicate that says which those are, so a caller can decline the value rather than keep a
//   dangling field.

import Ashes.Collection.List.length
import AshesCompiler.Semantics.FunctionOrigins
import AshesCompiler.Semantics.HeapLayoutClassification
import AshesCompiler.Semantics.IrInstructions
import AshesCompiler.Semantics.StructuralDroppers
import AshesCompiler.Semantics.TaglessAdtLayout
import AshesCompiler.Semantics.TypeInference
import AshesCompiler.Semantics.Types
export (
    value toSpaceCopySafeType,
    value synthesizeToSpaceCopy,
)

// A to-space copier and an arena copier of the same type are different functions, so the shared
// copier-label cache keys them apart by prefix.
let toSpaceCacheKey (semanticType: SemanticType) = "tospace|" + formatSemanticType(semanticType)

let cachedToSpaceLabel (key: Str) (body: DropperBody) =
    match body with
        | DropperBody { cache = DropperLabelCache { copierLabels = labels } } -> lookupLabel(key)(labels)

let registerToSpaceLabel (prefix: Str) (key: Str) (body: DropperBody) =
    match body with
        | DropperBody { cache = DropperLabelCache { structuralLabels = structural, adtLabels = adt, copierLabels = copiers }, nextLambdaId = nextLambdaId } ->
            let label = prefix + Ashes.Text.fromInt(nextLambdaId)
            in (label, (body with cache = DropperLabelCache(structuralLabels = structural, adtLabels = adt, copierLabels = (key, label) :: copiers), nextLambdaId = nextLambdaId + 1))

let recursive everyElementIsCopyType (elements: List(SemanticType)) =
    match elements with
        | [] -> true
        | element :: rest -> canArenaResetLayout(element) && everyElementIsCopyType(rest)

let recursive containsTypeKey (key: Str) (keys: List(Str)) =
    match keys with
        | [] -> false
        | candidate :: rest -> candidate == key || containsTypeKey(key)(rest)

let namedTypeIsResourceBearing (named: SemanticType) (environment: TypeEnvironment) =
    match classifyHeapLayout(named)(environment) with
        | HeapLayoutFacts { containsResource = containsResource } -> containsResource

// Stage 0's `IsToSpaceCopySafeType`: whether the walk below relocates every heap pointer inside a
// value of this type. Copy types are inline and need nothing; a string or bytes value copies by its
// length; a tuple of copy types is a fixed-size shallow copy; a list of strings goes through the
// synthesized list copier; and a non-resource ADT whose every field is itself safe goes through
// the synthesized ADT copier. Every other shape — a list over a heap element, a closure, a
// resource handle — is declined, so the caller keeps the value where it is rather than relocating
// half of it. `path` guards the recursion: a type already being validated further up answers safe,
// the same assumption its own synthesis makes.
let recursive toSpaceCopySafeIn (path: List(Str)) (semanticType: SemanticType) (environment: TypeEnvironment) =
    if canArenaResetLayout(semanticType)
    then true
    else
        match semanticType with
            | SemString -> true
            | SemBytes -> true
            | SemTuple(elements) -> everyElementIsCopyType(elements)
            | SemList(SemString) -> true
            | SemNamed(_symbolId, typeName, _arguments) -> !isBuiltinResourceTypeName(typeName) && !namedTypeIsResourceBearing(semanticType)(environment) && adtToSpaceCopySafeIn(path)(semanticType)(environment)
            | _ -> false
and adtToSpaceCopySafeIn (path: List(Str)) (named: SemanticType) (environment: TypeEnvironment) =
    match heapNamedTypeConstructors(named)(environment) with
        | [] -> false
        | constructors ->
            let key = formatSemanticType(named)
            in containsTypeKey(key)(path) || everyConstructorToSpaceSafeIn(key :: path)(constructors)(environment)
and everyConstructorToSpaceSafeIn (path: List(Str)) (constructors: List((Str, List(SemanticType)))) (environment: TypeEnvironment) =
    match constructors with
        | [] -> true
        | (_constructorName, fieldTypes) :: rest -> everyFieldToSpaceSafeIn(path)(fieldTypes)(environment) && everyConstructorToSpaceSafeIn(path)(rest)(environment)
and everyFieldToSpaceSafeIn (path: List(Str)) (fieldTypes: List(SemanticType)) (environment: TypeEnvironment) =
    match fieldTypes with
        | [] -> true
        | fieldType :: rest -> toSpaceCopySafeIn(path)(fieldType)(environment) && everyFieldToSpaceSafeIn(path)(rest)(environment)

let toSpaceCopySafeType (semanticType: SemanticType) (environment: TypeEnvironment) = toSpaceCopySafeIn([])(semanticType)(environment)

// Calls a synthesized copier on `valueTemp` through a fresh closure whose environment holds the
// closure itself, so the copier can recurse through `LoadEnv 0`; answers the clone's temp. A real
// recursive function is the only way to walk an unbounded shape, so every caller pays this fixed
// closure construction per value — negligible next to the walk itself.
let emitToSpaceCopierCall (label: Str) (valueTemp: Int) (body: DropperBody) =
    match freshDropperTemp(body) with
        | (environmentTemp, environmentBody) ->
            match freshDropperTemp(environmentBody) with
                | (copierTemp, copierBody) ->
                    match freshDropperTemp(copierBody) with
                        | (resultTemp, resultBody) ->
                            resultBody
                            |> emitDropper(Alloc(environmentTemp)(8)(false))
                            |> emitDropper(MakeClosure(copierTemp)(label)(environmentTemp)(8)(false)(false)(false))
                            |> emitDropper(StoreMemOffset(environmentTemp)(0)(copierTemp))
                            |> emitDropper(CallClosure(resultTemp)(copierTemp)(valueTemp)(-1))
                            |> (given (called: DropperBody) -> (resultTemp, called))

let recursive constructorArmLabels (label: Str) (count: Int) (index: Int) =
    if index >= count
    then []
    else label + "_c" + Ashes.Text.fromInt(index) :: constructorArmLabels(label)(count)(index + 1)

let recursive toSpaceSwitchCases (labels: List(Str)) (tag: Int) =
    match labels with
        | [] -> []
        | label :: rest -> IrSwitchCase(tag = tag, label = label) :: toSpaceSwitchCases(rest)(tag + 1)

// The relocation of one value by its type (stage 0's `EmitDeepCopyToSpace`), answering the temp
// holding it. A shape `toSpaceCopySafeType` declines passes through unchanged.
let recursive emitToSpaceCopyInto (valueTemp: Int) (semanticType: SemanticType) (body: DropperBody) =
    if canArenaResetLayout(semanticType)
    then (valueTemp, body)
    else
        match semanticType with
            | SemString -> emitToSpaceLeafCopy(valueTemp)(-1)(body)
            | SemBytes -> emitToSpaceLeafCopy(valueTemp)(-1)(body)
            | SemTuple(elements) ->
                if everyElementIsCopyType(elements)
                then emitToSpaceLeafCopy(valueTemp)(8 * length(elements))(body)
                else (valueTemp, body)
            | SemList(SemString) -> emitListToSpaceCopy(valueTemp)(SemString)(body)
            | SemNamed(_symbolId, typeName, _arguments) ->
                if isBuiltinResourceTypeName(typeName) || namedTypeIsResourceBearing(semanticType)(bodyEnvironment(body))
                then (valueTemp, body)
                else emitAdtToSpaceCopy(valueTemp)(semanticType)(body)
            | _ -> (valueTemp, body)
and emitToSpaceLeafCopy (valueTemp: Int) (sizeBytes: Int) (body: DropperBody) =
    match freshDropperTemp(body) with
        | (destinationTemp, destinationBody) ->
            (destinationTemp, emitDropper(CopyOutArenaToSpace(destinationTemp)(valueTemp)(sizeBytes))(destinationBody))
and emitListToSpaceCopy (valueTemp: Int) (element: SemanticType) (body: DropperBody) =
    match synthesizeListToSpaceCopierIn(element)(body) with
        | (label, synthesized) -> emitToSpaceCopierCall(label)(valueTemp)(synthesized)
and synthesizeListToSpaceCopierIn (element: SemanticType) (body: DropperBody) =
    match cachedToSpaceLabel(toSpaceCacheKey(SemList(element)))(body) with
        | Some(label) -> (label, body)
        | None ->
            match registerToSpaceLabel("__tospacecopy_list_")(toSpaceCacheKey(SemList(element)))(body) with
                | (label, registered) ->
                    registered
                    |> beginSynthesizedBody
                    |> emitListToSpaceCopierBody(element)(label)
                    |> finishSynthesizedBody(label)(createDeepCopierOrigin(label)(formatSemanticType(SemList(element)))(true))(registered)
                    |> (given (outer: DropperBody) -> (label, outer))
// Stage 0's `EmitListToSpaceCopierBody`: the empty list passes through; otherwise the head is
// relocated, the tail recursed through the self-closure, a fresh cell built around them in arena
// scratch, and that whole cell flat-copied into the blob.
and emitListToSpaceCopierBody (element: SemanticType) (label: Str) (body: DropperBody) =
    match openSynthesizedValue(body) with
        | (valueTemp, valueBody) ->
            match freshDropperTemp(valueBody) with
                | (selfTemp, selfBody) ->
                    match freshDropperTemp(selfBody) with
                        | (zeroTemp, zeroBody) ->
                            match freshDropperTemp(zeroBody) with
                                | (isNilTemp, nilBody) ->
                                    match freshDropperTemp(nilBody) with
                                        | (nilResultTemp, resultBody) ->
                                            match freshDropperTemp(resultBody) with
                                                | (headTemp, headBody) ->
                                                    match freshDropperTemp(headBody) with
                                                        | (tailTemp, tailBody) ->
                                                            match tailBody
                                                            |> emitDropper(LoadEnv(selfTemp)(0))
                                                            |> emitDropper(LoadConstInt(zeroTemp)(0))
                                                            |> emitDropper(CmpIntEq(isNilTemp)(valueTemp)(zeroTemp))
                                                            |> emitDropper(JumpIfFalse(isNilTemp)(label + "_copy"))
                                                            |> emitDropper(LoadConstInt(nilResultTemp)(0))
                                                            |> emitDropper(Return(nilResultTemp))
                                                            |> emitDropper(Label(label + "_copy"))
                                                            |> emitDropper(LoadMemOffset(headTemp)(valueTemp)(0))
                                                            |> emitDropper(LoadMemOffset(tailTemp)(valueTemp)(8))
                                                            |> emitToSpaceCopyInto(headTemp)(element) with
                                                                | (copiedHeadTemp, copiedBody) ->
                                                                    match freshDropperTemp(copiedBody) with
                                                                        | (copiedTailTemp, copiedTailBody) ->
                                                                            match freshDropperTemp(copiedTailBody) with
                                                                                | (scratchTemp, scratchBody) ->
                                                                                    match freshDropperTemp(scratchBody) with
                                                                                        | (cellTemp, cellBody) ->
                                                                                            cellBody
                                                                                            |> emitDropper(CallClosure(copiedTailTemp)(selfTemp)(tailTemp)(-1))
                                                                                            |> emitDropper(Alloc(scratchTemp)(16)(false))
                                                                                            |> emitDropper(StoreMemOffset(scratchTemp)(0)(copiedHeadTemp))
                                                                                            |> emitDropper(StoreMemOffset(scratchTemp)(8)(copiedTailTemp))
                                                                                            |> emitDropper(CopyOutArenaToSpace(cellTemp)(scratchTemp)(16))
                                                                                            |> emitDropper(Return(cellTemp))
and emitAdtToSpaceCopy (valueTemp: Int) (named: SemanticType) (body: DropperBody) =
    match synthesizeAdtToSpaceCopierIn(named)(body) with
        | (None, unsupported) -> (valueTemp, unsupported)
        | (Some(label), synthesized) -> emitToSpaceCopierCall(label)(valueTemp)(synthesized)
and synthesizeAdtToSpaceCopierIn (named: SemanticType) (body: DropperBody) =
    match cachedToSpaceLabel(toSpaceCacheKey(named))(body) with
        | Some(label) -> (Some(label), body)
        | None ->
            match body
            |> bodyEnvironment
            |> heapNamedTypeConstructors(named) with
                | [] -> (None, body)
                | constructors ->
                    match registerToSpaceLabel("__tospacecopy_adt_")(toSpaceCacheKey(named))(body) with
                        | (label, registered) ->
                            registered
                            |> beginSynthesizedBody
                            |> emitAdtToSpaceCopierBody(named)(label)(constructors)
                            |> finishSynthesizedBody(label)(createDeepCopierOrigin(label)(formatSemanticType(named))(false))(registered)
                            |> (given (outer: DropperBody) -> (Some(label), outer))
and emitAdtToSpaceCopierBody (named: SemanticType) (label: Str) (constructors: List((Str, List(SemanticType)))) (body: DropperBody) =
    match openSynthesizedValue(body) with
        | (valueTemp, valueBody) ->
            match freshDropperTemp(valueBody) with
                | (selfTemp, selfBody) ->
                    match freshDropperTemp(selfBody) with
                        | (tagTemp, tagBody) ->
                            let labels =
                                constructorArmLabels(label)(length(constructors))(0)
                            in
                                tagBody
                                |> emitDropper(LoadEnv(selfTemp)(0))
                                |> emitConstructorTagRead(tagTemp)(valueTemp)(named)
                                |> emitDropper(SwitchTag(tagTemp)(toSpaceSwitchCases(labels)(0))(label + "_default"))
                                |> emitAdtToSpaceCopierArms(valueTemp)(selfTemp)(named)(typeIsTagless(named)(body))(0)(labels)(constructors)
                                |> emitDropper(Label(label + "_default"))
                                |> emitDropper(Return(valueTemp))
and emitAdtToSpaceCopierArms (valueTemp: Int) (selfTemp: Int) (named: SemanticType) (tagless: Bool) (tag: Int) (labels: List(Str)) (constructors: List((Str, List(SemanticType)))) (body: DropperBody) =
    match (labels, constructors) with
        | (armLabel :: restLabels, (_constructorName, fieldTypes) :: restConstructors) ->
            match body
            |> emitDropper(Label(armLabel))
            |> freshDropperTemp with
                | (scratchTemp, scratchBody) ->
                    match freshDropperTemp(scratchBody) with
                        | (cellTemp, cellBody) ->
                            cellBody
                            |> emitDropper(AllocAdt(scratchTemp)(tag)(length(fieldTypes))(false)(tagless))
                            |> emitAdtToSpaceCopierFields(valueTemp)(scratchTemp)(selfTemp)(named)(tagless)(0)(fieldTypes)
                            |> emitDropper(fieldTypes
                            |> length
                            |> adtAllocationSizeBytes(tagless)
                            |> CopyOutArenaToSpace(cellTemp)(scratchTemp))
                            |> emitDropper(Return(cellTemp))
                            |> emitAdtToSpaceCopierArms(valueTemp)(selfTemp)(named)(tagless)(tag + 1)(restLabels)(restConstructors)
        | _ -> body
and emitAdtToSpaceCopierFields (valueTemp: Int) (cellTemp: Int) (selfTemp: Int) (named: SemanticType) (tagless: Bool) (index: Int) (fieldTypes: List(SemanticType)) (body: DropperBody) =
    match fieldTypes with
        | [] -> body
        | fieldType :: rest ->
            match freshDropperTemp(body) with
                | (fieldTemp, fieldBody) ->
                    match fieldBody
                    |> emitDropper(index
                    |> adtFieldOffsetBytes(tagless)
                    |> LoadMemOffset(fieldTemp)(valueTemp))
                    |> emitAdtToSpaceCopierField(fieldTemp)(fieldType)(named)(selfTemp) with
                        | (copiedTemp, copiedBody) ->
                            copiedBody
                            |> emitDropper(StoreMemOffset(cellTemp)(adtFieldOffsetBytes(tagless)(index))(copiedTemp))
                            |> emitAdtToSpaceCopierFields(valueTemp)(cellTemp)(selfTemp)(named)(tagless)(index + 1)(rest)
// Stage 0's `CopyFieldToSpaceInsideCopier`: a field of the copier's own type recurses through the
// self-closure; any other field type goes through the walk above.
and emitAdtToSpaceCopierField (fieldTemp: Int) (fieldType: SemanticType) (named: SemanticType) (selfTemp: Int) (body: DropperBody) =
    if formatSemanticType(fieldType) == formatSemanticType(named)
    then
        match freshDropperTemp(body) with
            | (copiedTemp, copiedBody) ->
                (copiedTemp, emitDropper(CallClosure(copiedTemp)(selfTemp)(fieldTemp)(-1))(copiedBody))
    else emitToSpaceCopyInto(fieldTemp)(fieldType)(body)

// The relocation of `valueTemp`, a value of the caller's resolved `semanticType`, into to-space, in
// emission order, with the copier functions it synthesized and the counters, cache, and functions it
// advanced; the temp holding the relocated value comes back beside it. `definitions` are the
// constructors in scope in declaration order (a constructor's tag is its index among its type's
// constructors).
let synthesizeToSpaceCopy (valueTemp: Int) (semanticType: SemanticType) (definitions: List(ConstructorInferenceDefinition)) (cache: DropperLabelCache) (nextTemp: Int) (nextLocal: Int) (nextLambdaId: Int) (nextLabelId: Int) =
    match openDropperBody(definitions)(cache)(nextLambdaId)(nextLabelId) with
        | (ids, opened) ->
            match emitToSpaceCopyInto(valueTemp)(renumberType(ids)(semanticType))((opened with nextTemp = nextTemp, nextLocal = nextLocal)) with
                | (resultTemp, copied) -> (inlineReleaseResult(copied), resultTemp)
