// Unifies semantic types into a pure substitution or a structured error.
//
// Invariants:
// - The occurs check rejects infinite types before extending a substitution.
// - Capability rows are order-independent and open tails absorb only unmatched capabilities.
// - Two open rows share one fresh tail so neither unmatched side is assigned twice.
// - Never is the bottom type: it unifies with any type, at any depth, without extending the substitution.

import AshesCompiler.Semantics.Types
import Ashes.Collection.List.reverse
export (
    type UnificationError(..),
    type UnificationResult(..),
    value unify,
    value unifyFlatRows,
)

type UnificationError =
    | InfiniteType(Int, SemanticType)
    | TypeMismatch(SemanticType, SemanticType)
    | TypeArityMismatch(Int, Int)
    deriving {Eq, Show}

type UnificationResult =
    | substitution: List((Int, SemanticType))
    | error: Maybe(UnificationError)
    deriving {Eq, Show}

let unificationSuccess substitution = UnificationResult(substitution = substitution, error = None)

let unificationFailure substitution error = UnificationResult(substitution = substitution, error = Some(error))

let recursive typeListLength : List(SemanticType) -> Int =
    given (values) ->
        match values with
            | [] -> 0
            | _ :: tail -> 1 + typeListLength(tail)

let recursive substitutionLength : List((Int, SemanticType)) -> Int =
    given (substitution) ->
        match substitution with
            | [] -> 0
            | _ :: tail -> 1 + substitutionLength(tail)

let capabilityName : SemanticType -> Maybe(Str) =
    given (semanticType) ->
        match semanticType with
            | SemCapability(name, _arguments) -> Some(name)
            | _ -> None

let recursive findCapability : Str -> List(SemanticType) -> Maybe(SemanticType) =
    given (name) ->
        given (capabilities) ->
            match capabilities with
                | [] -> None
                | head :: tail ->
                    match capabilityName(head) with
                        | Some(candidateName) ->
                            if name == candidateName
                            then Some(head)
                            else findCapability(name)(tail)
                        | None -> findCapability(name)(tail)

let recursive capabilitiesMissingFrom candidates reference reversed =
    match candidates with
        | [] -> reverse(reversed)
        | head :: tail ->
            match capabilityName(head) with
                | Some(name) ->
                    match findCapability(name)(reference) with
                        | None -> capabilitiesMissingFrom(tail)(reference)(head :: reversed)
                        | Some(_) -> capabilitiesMissingFrom(tail)(reference)(reversed)
                | None -> capabilitiesMissingFrom(tail)(reference)(head :: reversed)

let recursive appendCapabilities : List(SemanticType) -> List(SemanticType) -> List(SemanticType) =
    given (left) ->
        given (right) ->
            match left with
                | [] -> right
                | head :: tail -> head :: appendCapabilities(tail)(right)

let recursive flatRow (flat: Bool) (capabilities: List(SemanticType)) (tail: Maybe(SemanticType)) =
    if flat
    then
        match tail with
            | Some(SemRow(innerCapabilities, innerTail)) ->
                flatRow(flat)(appendCapabilities(capabilities)(innerCapabilities))(innerTail)
            | _ -> SemRow(capabilities)(tail)
    else SemRow(capabilities)(tail)

let bindVariable variableId semanticType substitution =
    (let resolvedType = applySubstitution(substitution)(semanticType)
    in
        match resolvedType with
            | SemVariable(otherId) ->
                if variableId == otherId
                then unificationSuccess(substitution)
                else unificationSuccess((variableId, resolvedType) :: substitution)
            | _ ->
                if occursInType(variableId)(resolvedType)
                then
                    resolvedType
                    |> InfiniteType(variableId)
                    |> unificationFailure(substitution)
                else unificationSuccess((variableId, resolvedType) :: substitution))

let recursive unifyTypeLists (flat: Bool) left right substitution =
    match (left, right) with
        | ([], []) -> unificationSuccess(substitution)
        | (leftHead :: leftTail, rightHead :: rightTail) ->
            match unifyWith(flat)(substitution)(leftHead)(rightHead) with
                | UnificationResult { substitution = nextSubstitution, error = None } ->
                    unifyTypeLists(flat)(
                        leftTail,
                        rightTail,
                        nextSubstitution
                    )
                | failure -> failure
        | _ ->
            right
            |> typeListLength
            |> TypeArityMismatch(typeListLength(left))
            |> unificationFailure(substitution)
and unifyCommonCapabilities (flat: Bool) left right substitution =
    match left with
        | [] -> unificationSuccess(substitution)
        | head :: tail ->
            match capabilityName(head) with
                | None ->
                    None
                    |> SemRow(right)
                    |> TypeMismatch(head)
                    |> unificationFailure(substitution)
                | Some(name) ->
                    match findCapability(name)(right) with
                        | None -> unifyCommonCapabilities(flat)(tail)(right)(substitution)
                        | Some(matching) ->
                            match unifyWith(flat)(substitution)(head)(matching) with
                                | UnificationResult { substitution = nextSubstitution, error = None } ->
                                    unifyCommonCapabilities(flat)(
                                        tail,
                                        right,
                                        nextSubstitution
                                    )
                                | failure -> failure
and unifyOptionalRows (flat: Bool) left right substitution =
    match (left, right) with
        | (None, None) -> unificationSuccess(substitution)
        | (Some(leftRow), Some(rightRow)) -> unifyWith(flat)(substitution)(leftRow)(rightRow)
        | (None, Some(rightRow)) ->
            unifyWith(flat)(substitution)(SemRow([])(None))(rightRow)
        | (Some(leftRow), None) ->
            None
            |> SemRow([])
            |> unifyWith(flat)(substitution)(leftRow)
// When both rows are open, one shared fresh tail prevents either unmatched capability set from
// being assigned independently to both row variables.
and unifyRows (flat: Bool) leftCapabilities leftTail rightCapabilities rightTail substitution leftRow rightRow =
    match unifyCommonCapabilities(flat)(leftCapabilities)(rightCapabilities)(substitution) with
        | UnificationResult { substitution = afterCommon, error = None } ->
            let leftOnly = capabilitiesMissingFrom(leftCapabilities)(rightCapabilities)([])
            in
                let rightOnly = capabilitiesMissingFrom(rightCapabilities)(leftCapabilities)([])
                in
                    match (leftOnly, rightOnly) with
                        | ([], []) -> unifyOptionalRows(flat)(leftTail)(rightTail)(afterCommon)
                        | ([], _) ->
                            match leftTail with
                                | None ->
                                    rightRow
                                    |> TypeMismatch(leftRow)
                                    |> unificationFailure(afterCommon)
                                | Some(leftTailType) ->
                                    unifyWith(flat)(
                                        afterCommon,
                                        leftTailType,
                                        SemRow(rightOnly)(rightTail)
                                    )
                        | (_, []) ->
                            match rightTail with
                                | None ->
                                    rightRow
                                    |> TypeMismatch(leftRow)
                                    |> unificationFailure(afterCommon)
                                | Some(rightTailType) ->
                                    unifyWith(flat)(
                                        afterCommon,
                                        rightTailType,
                                        SemRow(leftOnly)(leftTail)
                                    )
                        | (_, _) ->
                            match (leftTail, rightTail) with
                                | (Some(leftTailType), Some(rightTailType)) ->
                                    let freshTail = SemVariable(-(1000000 + substitutionLength(afterCommon)))
                                    in
                                        match unifyWith(flat)(
                                            afterCommon,
                                            leftTailType,
                                            SemRow(rightOnly)(Some(freshTail))
                                        ) with
                                            | UnificationResult { substitution = afterLeftTail, error = None } ->
                                                unifyWith(flat)(
                                                    afterLeftTail,
                                                    rightTailType,
                                                    SemRow(leftOnly)(Some(freshTail))
                                                )
                                            | failure -> failure
                                | _ ->
                                    rightRow
                                    |> TypeMismatch(leftRow)
                                    |> unificationFailure(afterCommon)
        | failure -> failure
and unifyWith (flat: Bool) substitution left right =
    (let resolvedLeft = applySubstitution(substitution)(left)
    in
        let resolvedRight = applySubstitution(substitution)(right)
        in
            if resolvedLeft == resolvedRight
            then unificationSuccess(substitution)
            else
                match (resolvedLeft, resolvedRight) with
                    | (SemNever, _) -> unificationSuccess(substitution)
                    | (_, SemNever) -> unificationSuccess(substitution)
                    | (SemVariable(variableId), semanticType) -> bindVariable(variableId)(semanticType)(substitution)
                    | (semanticType, SemVariable(variableId)) -> bindVariable(variableId)(semanticType)(substitution)
                    | (SemUInt(leftBits), SemUInt(rightBits)) ->
                        if leftBits == rightBits
                        then unificationSuccess(substitution)
                        else
                            resolvedRight
                            |> TypeMismatch(resolvedLeft)
                            |> unificationFailure(substitution)
                    | (SemList(leftElement), SemList(rightElement)) ->
                        unifyWith(flat)(
                            substitution,
                            leftElement,
                            rightElement
                        )
                    // The empty tuple a standard trait's method type writes (`Default.default`) and
                    // external typing gives `void` is the language's `Unit`.
                    | (SemTuple([]), SemNamed(_unitId, unitName, [])) ->
                        if unitName == "Unit"
                        then unificationSuccess(substitution)
                        else
                            resolvedRight
                            |> TypeMismatch(resolvedLeft)
                            |> unificationFailure(substitution)
                    | (SemNamed(_unitId, unitName, []), SemTuple([])) ->
                        if unitName == "Unit"
                        then unificationSuccess(substitution)
                        else
                            resolvedRight
                            |> TypeMismatch(resolvedLeft)
                            |> unificationFailure(substitution)
                    | (SemTuple(leftElements), SemTuple(rightElements)) ->
                        if typeListLength(leftElements) == typeListLength(rightElements)
                        then
                            unifyTypeLists(flat)(
                                leftElements,
                                rightElements,
                                substitution
                            )
                        else
                            resolvedRight
                            |> TypeMismatch(resolvedLeft)
                            |> unificationFailure(substitution)
                    | (SemFunction(leftArgument, leftResult, leftRow), SemFunction(rightArgument, rightResult, rightRow)) ->
                        match unifyWith(flat)(substitution)(leftArgument)(rightArgument) with
                            | UnificationResult { substitution = afterArgument, error = None } ->
                                match unifyWith(flat)(afterArgument)(leftResult)(rightResult) with
                                    | UnificationResult { substitution = afterResult, error = None } ->
                                        unifyOptionalRows(flat)(
                                            leftRow,
                                            rightRow,
                                            afterResult
                                        )
                                    | failure -> failure
                            | failure -> failure
                    | (SemCapability(leftName, leftArguments), SemCapability(rightName, rightArguments)) ->
                        if leftName == rightName
                        then unifyTypeLists(flat)(leftArguments)(rightArguments)(substitution)
                        else
                            resolvedRight
                            |> TypeMismatch(resolvedLeft)
                            |> unificationFailure(substitution)
                    | (SemRow(leftCapabilities, leftTail), SemRow(rightCapabilities, rightTail)) ->
                        match (flatRow(flat)(leftCapabilities)(leftTail), flatRow(flat)(rightCapabilities)(rightTail)) with
                            | (SemRow(leftFlat, leftFlatTail), SemRow(rightFlat, rightFlatTail)) ->
                                unifyRows(flat)(
                                    leftFlat,
                                    leftFlatTail,
                                    rightFlat,
                                    rightFlatTail,
                                    substitution,
                                    resolvedLeft,
                                    resolvedRight
                                )
                            | _ -> unificationSuccess(substitution)
                    | (SemNamed(leftId, _leftName, leftArguments), SemNamed(rightId, _rightName, rightArguments)) ->
                        if leftId == rightId
                        then unifyTypeLists(flat)(leftArguments)(rightArguments)(substitution)
                        else
                            resolvedRight
                            |> TypeMismatch(resolvedLeft)
                            |> unificationFailure(substitution)
                    | (SemParameter(leftId, _leftName), SemParameter(rightId, _rightName)) ->
                        if leftId == rightId
                        then unificationSuccess(substitution)
                        else
                            resolvedRight
                            |> TypeMismatch(resolvedLeft)
                            |> unificationFailure(substitution)
                    | (SemOpaque(leftName), SemOpaque(rightName)) ->
                        if leftName == rightName
                        then unificationSuccess(substitution)
                        else
                            resolvedRight
                            |> TypeMismatch(resolvedLeft)
                            |> unificationFailure(substitution)
                    | (SemPointer(leftPointee), SemPointer(rightPointee)) ->
                        unifyWith(flat)(
                            substitution,
                            leftPointee,
                            rightPointee
                        )
                    | _ ->
                        resolvedRight
                        |> TypeMismatch(resolvedLeft)
                        |> unificationFailure(substitution))

let unify left right = unifyWith(false)([])(left)(right)

// Unifies with every row read as one list of capabilities and one tail, however the
// substitution chained it. Read as chained, two rows that differ at two depths are given the same
// shared tail twice.
let unifyFlatRows left right = unifyWith(true)([])(left)(right)
