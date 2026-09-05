// Synthesizes the arena deep copiers stage 0 places behind a label (`__deepcopy_N`, an
// `AdtDeepCopier`): a per-type function taking (env, value) that reads the constructor tag,
// allocates a fresh cell of the same constructor in the arena, and deep-copies every field, a
// field of the type itself through the self-closure in env[0] and any other heap field through the
// inline clone below, so a fixed-shape ADT accumulator crosses a fixed-watermark arena reset as a
// self-contained clone. The inline clone (stage 0's `EmitDeepCopy`) copies a string or bytes
// value by its length, rebuilds a tuple element by element, copies a list over scalar, string or
// scalar-list heads through the cons-chain copy, and calls a named type's copier through a
// closure whose environment holds the closure itself; a scalar passes through, and a value whose
// clone is not supported stays a shared reference.
//
// Invariants:
// - A copier is synthesized once per pretty-printed type; the cache maps the type to its label,
//   and a recursive ADT copier finds its own label in the cache before its body is emitted.
// - A copier's default arm returns the value itself, an unreachable fallback for a tag no
//   constructor carries.
// - Every copier is a complete `IrFunction` with env-and-arg parameters (slot 0 the environment,
//   slot 1 the value), built from its own temp and local counters.

import Ashes.Collection.List.length
import AshesCompiler.Semantics.FunctionOrigins
import AshesCompiler.Semantics.HeapLayoutClassification
import AshesCompiler.Semantics.IrInstructions
import AshesCompiler.Semantics.StructuralDroppers
import AshesCompiler.Semantics.TaglessAdtLayout
import AshesCompiler.Semantics.TypeInference
import AshesCompiler.Semantics.Types
export (
    value synthesizeDeepCopy,
)

let cachedCopierLabel (key: Str) (body: DropperBody) =
    match body with
        | DropperBody { cache = DropperLabelCache { copierLabels = labels } } -> lookupLabel(key)(labels)

let registerCopierLabel (prefix: Str) (key: Str) (body: DropperBody) =
    match body with
        | DropperBody { cache = DropperLabelCache { structuralLabels = structural, adtLabels = adt, copierLabels = copiers }, nextLambdaId = nextLambdaId } ->
            let label = prefix + Ashes.Text.fromInt(nextLambdaId)
            in (label, (body with cache = DropperLabelCache(structuralLabels = structural, adtLabels = adt, copierLabels = (key, label) :: copiers), nextLambdaId = nextLambdaId + 1))

// Whether the arena copier can clone a value of this type completely (stage 0's
// `IsDeepCopyOutSafeType`).
let elementDeepCopySupported (element: SemanticType) (body: DropperBody) =
    match body
    |> bodyEnvironment
    |> classifyHeapLayout(element) with
        | HeapLayoutFacts { arenaDeepCopySupported = supported } -> supported

// Calls a synthesized copier on `valueTemp` through a fresh closure whose environment holds the
// closure itself, so the copier can recurse through `LoadEnv 0`; answers the clone's temp.
let emitCopierCall (label: Str) (valueTemp: Int) (body: DropperBody) =
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

// A named type whose value is, or holds, a resource handle is never cloned: duplicating a handle
// is wrong, so the reference stays shared.
let namedTypeHoldsResource (named: SemanticType) (body: DropperBody) =
    match body
    |> bodyEnvironment
    |> classifyHeapLayout(named) with
        | HeapLayoutFacts { containsResource = containsResource } -> containsResource

let recursive constructorLabels (label: Str) (count: Int) (index: Int) =
    if index >= count
    then []
    else label + "_c" + Ashes.Text.fromInt(index) :: constructorLabels(label)(count)(index + 1)

let recursive copierSwitchCases (labels: List(Str)) (tag: Int) =
    match labels with
        | [] -> []
        | label :: rest -> IrSwitchCase(tag = tag, label = label) :: copierSwitchCases(rest)(tag + 1)

// The clone of one value by its type (stage 0's `EmitDeepCopy`), answering the temp holding it.
let recursive emitDeepCopyInto (valueTemp: Int) (semanticType: SemanticType) (body: DropperBody) =
    match semanticType with
        | SemString -> emitLeafClone(valueTemp)(body)
        | SemBytes -> emitLeafClone(valueTemp)(body)
        | SemTuple(elements) -> emitTupleClone(valueTemp)(elements)(body)
        | SemList(element) -> emitListClone(valueTemp)(element)(body)
        | SemNamed(_symbolId, _name, _arguments) ->
            if namedTypeHoldsResource(semanticType)(body)
            then (valueTemp, body)
            else emitAdtClone(valueTemp)(semanticType)(body)
        | _ -> (valueTemp, body)
and emitLeafClone (valueTemp: Int) (body: DropperBody) =
    match freshDropperTemp(body) with
        | (destTemp, destBody) ->
            (destTemp, emitDropper(CopyOutArena(destTemp)(valueTemp)(-1)(false)(IndependentClone)(None))(destBody))
and emitTupleClone (valueTemp: Int) (elements: List(SemanticType)) (body: DropperBody) =
    match freshDropperTemp(body) with
        | (destTemp, destBody) ->
            destBody
            |> emitDropper(Alloc(destTemp)(8 * length(elements))(false))
            |> emitTupleElementClones(valueTemp)(destTemp)(0)(elements)
            |> (given (cloned: DropperBody) -> (destTemp, cloned))
and emitTupleElementClones (valueTemp: Int) (destTemp: Int) (index: Int) (elements: List(SemanticType)) (body: DropperBody) =
    match elements with
        | [] -> body
        | element :: rest ->
            match freshDropperTemp(body) with
                | (fieldTemp, fieldBody) ->
                    match fieldBody
                    |> emitDropper(LoadMemOffset(fieldTemp)(valueTemp)(index * 8))
                    |> emitDeepCopyInto(fieldTemp)(element) with
                        | (copiedTemp, copiedBody) ->
                            copiedBody
                            |> emitDropper(StoreMemOffset(destTemp)(index * 8)(copiedTemp))
                            |> emitTupleElementClones(valueTemp)(destTemp)(index + 1)(rest)
and emitListClone (valueTemp: Int) (element: SemanticType) (body: DropperBody) =
    match listCloneHeadKind(element) with
        | Some(headKind) ->
            match freshDropperTemp(body) with
                | (destTemp, destBody) ->
                    (destTemp, emitDropper(CopyOutList(destTemp)(valueTemp)(headKind)(false)(IndependentClone))(destBody))
        | None ->
            if elementDeepCopySupported(element)(body)
            then
                match synthesizeListCopierIn(element)(body) with
                    | (label, synthesized) -> emitCopierCall(label)(valueTemp)(synthesized)
            else (valueTemp, body)
and synthesizeListCopierIn (element: SemanticType) (body: DropperBody) =
    match cachedCopierLabel(formatSemanticType(SemList(element)))(body) with
        | Some(label) -> (label, body)
        | None ->
            match registerCopierLabel("__deepcopy_list_")(formatSemanticType(SemList(element)))(body) with
                | (label, registered) ->
                    registered
                    |> beginSynthesizedBody
                    |> emitListCopierBody(element)(label)
                    |> finishSynthesizedBody(label)(createDeepCopierOrigin(label)(formatSemanticType(SemList(element)))(true))(registered)
                    |> (given (outer: DropperBody) -> (label, outer))
// Stage 0's `EmitListDeepCopierBody`: the empty list passes through; otherwise the head is
// cloned, the tail recursed through the self-closure, and a fresh cell rebuilt around them.
and emitListCopierBody (element: SemanticType) (label: Str) (body: DropperBody) =
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
                                                            |> emitDeepCopyInto(headTemp)(element) with
                                                                | (copiedHeadTemp, copiedBody) ->
                                                                    match freshDropperTemp(copiedBody) with
                                                                        | (copiedTailTemp, copiedTailBody) ->
                                                                            match freshDropperTemp(copiedTailBody) with
                                                                                | (cellTemp, cellBody) ->
                                                                                    cellBody
                                                                                    |> emitDropper(CallClosure(copiedTailTemp)(selfTemp)(tailTemp)(-1))
                                                                                    |> emitDropper(Alloc(cellTemp)(16)(false))
                                                                                    |> emitDropper(StoreMemOffset(cellTemp)(0)(copiedHeadTemp))
                                                                                    |> emitDropper(StoreMemOffset(cellTemp)(8)(copiedTailTemp))
                                                                                    |> emitDropper(Return(cellTemp))
and listCloneHeadKind (element: SemanticType) =
    if canArenaResetLayout(element)
    then Some(InlineListHead)
    else
        match element with
            | SemString -> Some(StringListHead)
            | SemList(inner) ->
                if canArenaResetLayout(inner)
                then Some(InnerListHead)
                else None
            | _ -> None
and emitAdtClone (valueTemp: Int) (named: SemanticType) (body: DropperBody) =
    match synthesizeAdtCopierIn(named)(body) with
        | (None, unsupported) -> (valueTemp, unsupported)
        | (Some(label), synthesized) -> emitCopierCall(label)(valueTemp)(synthesized)
and synthesizeAdtCopierIn (named: SemanticType) (body: DropperBody) =
    match cachedCopierLabel(formatSemanticType(named))(body) with
        | Some(label) -> (Some(label), body)
        | None ->
            match body
            |> bodyEnvironment
            |> heapNamedTypeConstructors(named) with
                | [] -> (None, body)
                | constructors ->
                    match registerCopierLabel("__deepcopy_")(formatSemanticType(named))(body) with
                        | (label, registered) ->
                            registered
                            |> beginSynthesizedBody
                            |> emitCopierBody(named)(label)(constructors)
                            |> finishSynthesizedBody(label)(createDeepCopierOrigin(label)(formatSemanticType(named))(false))(registered)
                            |> (given (outer: DropperBody) -> (Some(label), outer))
and emitCopierBody (named: SemanticType) (label: Str) (constructors: List((Str, List(SemanticType)))) (body: DropperBody) =
    match openSynthesizedValue(body) with
        | (valueTemp, valueBody) ->
            match freshDropperTemp(valueBody) with
                | (selfTemp, selfBody) ->
                    match freshDropperTemp(selfBody) with
                        | (tagTemp, tagBody) ->
                            let labels =
                                constructorLabels(label)(length(constructors))(0)
                            in
                                tagBody
                                |> emitDropper(LoadEnv(selfTemp)(0))
                                |> emitConstructorTagRead(tagTemp)(valueTemp)(named)
                                |> emitDropper(SwitchTag(tagTemp)(copierSwitchCases(labels)(0))(label + "_default"))
                                |> emitCopierArms(valueTemp)(selfTemp)(named)(typeIsTagless(named)(body))(0)(labels)(constructors)
                                |> emitDropper(Label(label + "_default"))
                                |> emitDropper(Return(valueTemp))
and emitCopierArms (valueTemp: Int) (selfTemp: Int) (named: SemanticType) (tagless: Bool) (tag: Int) (labels: List(Str)) (constructors: List((Str, List(SemanticType)))) (body: DropperBody) =
    match (labels, constructors) with
        | (armLabel :: restLabels, (_constructorName, fieldTypes) :: restConstructors) ->
            match body
            |> emitDropper(Label(armLabel))
            |> freshDropperTemp with
                | (cellTemp, cellBody) ->
                    cellBody
                    |> emitDropper(AllocAdt(cellTemp)(tag)(length(fieldTypes))(false)(tagless))
                    |> emitCopierFields(valueTemp)(cellTemp)(selfTemp)(named)(tagless)(0)(fieldTypes)
                    |> emitDropper(Return(cellTemp))
                    |> emitCopierArms(valueTemp)(selfTemp)(named)(tagless)(tag + 1)(restLabels)(restConstructors)
        | _ -> body
and emitCopierFields (valueTemp: Int) (cellTemp: Int) (selfTemp: Int) (named: SemanticType) (tagless: Bool) (index: Int) (fieldTypes: List(SemanticType)) (body: DropperBody) =
    match fieldTypes with
        | [] -> body
        | fieldType :: rest ->
            match freshDropperTemp(body) with
                | (fieldTemp, fieldBody) ->
                    match fieldBody
                    |> emitDropper(index
                    |> adtFieldOffsetBytes(tagless)
                    |> LoadMemOffset(fieldTemp)(valueTemp))
                    |> emitCopierField(fieldTemp)(fieldType)(named)(selfTemp) with
                        | (copiedTemp, copiedBody) ->
                            copiedBody
                            |> emitDropper(StoreMemOffset(cellTemp)(adtFieldOffsetBytes(tagless)(index))(copiedTemp))
                            |> emitCopierFields(valueTemp)(cellTemp)(selfTemp)(named)(tagless)(index + 1)(rest)
and emitCopierField (fieldTemp: Int) (fieldType: SemanticType) (named: SemanticType) (selfTemp: Int) (body: DropperBody) =
    if formatSemanticType(fieldType) == formatSemanticType(named)
    then
        match freshDropperTemp(body) with
            | (copiedTemp, copiedBody) ->
                (copiedTemp, emitDropper(CallClosure(copiedTemp)(selfTemp)(fieldTemp)(-1))(copiedBody))
    else emitDeepCopyInto(fieldTemp)(fieldType)(body)

// The inline clone of `valueTemp`, a value of the caller's resolved `semanticType`, in emission
// order, with the copier functions it synthesized and the counters, cache, and functions it
// advanced; the temp holding the clone comes back beside it. `definitions` are the constructors
// in scope in declaration order (a constructor's tag is its index among its type's constructors).
let synthesizeDeepCopy (valueTemp: Int) (semanticType: SemanticType) (definitions: List(ConstructorInferenceDefinition)) (cache: DropperLabelCache) (nextTemp: Int) (nextLocal: Int) (nextLambdaId: Int) (nextLabelId: Int) =
    match openDropperBody(definitions)(cache)(nextLambdaId)(nextLabelId) with
        | (ids, opened) ->
            match emitDeepCopyInto(valueTemp)(renumberType(ids)(semanticType))((opened with nextTemp = nextTemp, nextLocal = nextLocal)) with
                | (resultTemp, cloned) -> (inlineReleaseResult(cloned), resultTemp)
