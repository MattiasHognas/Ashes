// Unifies semantic types into a pure substitution or a structured error.
//
// Invariants:
// - The occurs check rejects infinite types before extending a substitution.
// - Capability rows are order-independent and open tails absorb only unmatched capabilities.
// - Two open rows share one fresh tail so neither unmatched side is assigned twice.

import AshesCompiler.Semantics.Types
import Ashes.Collection.List.reverse
export (
    type UnificationError(..),
    type UnificationResult(..),
    value unify,
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
                then unificationFailure(substitution)(InfiniteType(variableId)(resolvedType))
                else unificationSuccess((variableId, resolvedType) :: substitution))

let recursive unifyTypeLists left right substitution =
    match (left, right) with
        | ([], []) -> unificationSuccess(substitution)
        | (leftHead :: leftTail, rightHead :: rightTail) ->
            match unifyWith(substitution)(leftHead)(rightHead) with
                | UnificationResult { substitution = nextSubstitution, error = None } ->
                    unifyTypeLists(
                        leftTail,
                        rightTail,
                        nextSubstitution
                    )
                | failure -> failure
        | _ -> unificationFailure(substitution)(TypeArityMismatch(typeListLength(left))(typeListLength(right)))
and unifyCommonCapabilities left right substitution =
    match left with
        | [] -> unificationSuccess(substitution)
        | head :: tail ->
            match capabilityName(head) with
                | None -> unificationFailure(substitution)(TypeMismatch(head)(SemRow(right)(None)))
                | Some(name) ->
                    match findCapability(name)(right) with
                        | None -> unifyCommonCapabilities(tail)(right)(substitution)
                        | Some(matching) ->
                            match unifyWith(substitution)(head)(matching) with
                                | UnificationResult { substitution = nextSubstitution, error = None } ->
                                    unifyCommonCapabilities(
                                        tail,
                                        right,
                                        nextSubstitution
                                    )
                                | failure -> failure
and unifyOptionalRows left right substitution =
    match (left, right) with
        | (None, None) -> unificationSuccess(substitution)
        | (Some(leftRow), Some(rightRow)) -> unifyWith(substitution)(leftRow)(rightRow)
        | (None, Some(rightRow)) -> unifyWith(substitution)(SemRow([])(None))(rightRow)
        | (Some(leftRow), None) -> unifyWith(substitution)(leftRow)(SemRow([])(None))
// When both rows are open, one shared fresh tail prevents either unmatched capability set from
// being assigned independently to both row variables.
and unifyRows leftCapabilities leftTail rightCapabilities rightTail substitution leftRow rightRow =
    match unifyCommonCapabilities(leftCapabilities)(rightCapabilities)(substitution) with
        | UnificationResult { substitution = afterCommon, error = None } ->
            let leftOnly = capabilitiesMissingFrom(leftCapabilities)(rightCapabilities)([])
            in
                let rightOnly = capabilitiesMissingFrom(rightCapabilities)(leftCapabilities)([])
                in
                    match (leftOnly, rightOnly) with
                        | ([], []) -> unifyOptionalRows(leftTail)(rightTail)(afterCommon)
                        | ([], _) ->
                            match leftTail with
                                | None -> unificationFailure(afterCommon)(TypeMismatch(leftRow)(rightRow))
                                | Some(leftTailType) ->
                                    unifyWith(
                                        afterCommon,
                                        leftTailType,
                                        SemRow(rightOnly)(rightTail)
                                    )
                        | (_, []) ->
                            match rightTail with
                                | None -> unificationFailure(afterCommon)(TypeMismatch(leftRow)(rightRow))
                                | Some(rightTailType) ->
                                    unifyWith(
                                        afterCommon,
                                        rightTailType,
                                        SemRow(leftOnly)(leftTail)
                                    )
                        | (_, _) ->
                            match (leftTail, rightTail) with
                                | (Some(leftTailType), Some(rightTailType)) ->
                                    let freshTail = SemVariable(-(1000000 + substitutionLength(afterCommon)))
                                    in
                                        match unifyWith(
                                            afterCommon,
                                            leftTailType,
                                            SemRow(rightOnly)(Some(freshTail))
                                        ) with
                                            | UnificationResult { substitution = afterLeftTail, error = None } ->
                                                unifyWith(
                                                    afterLeftTail,
                                                    rightTailType,
                                                    SemRow(leftOnly)(Some(freshTail))
                                                )
                                            | failure -> failure
                                | _ -> unificationFailure(afterCommon)(TypeMismatch(leftRow)(rightRow))
        | failure -> failure
and unifyWith substitution left right =
    (let resolvedLeft = applySubstitution(substitution)(left)
    in
        let resolvedRight = applySubstitution(substitution)(right)
        in
            if resolvedLeft == resolvedRight
            then unificationSuccess(substitution)
            else
                match (resolvedLeft, resolvedRight) with
                    | (SemVariable(variableId), semanticType) -> bindVariable(variableId)(semanticType)(substitution)
                    | (semanticType, SemVariable(variableId)) -> bindVariable(variableId)(semanticType)(substitution)
                    | (SemUInt(leftBits), SemUInt(rightBits)) ->
                        if leftBits == rightBits
                        then unificationSuccess(substitution)
                        else unificationFailure(substitution)(TypeMismatch(resolvedLeft)(resolvedRight))
                    | (SemList(leftElement), SemList(rightElement)) ->
                        unifyWith(
                            substitution,
                            leftElement,
                            rightElement
                        )
                    | (SemTuple(leftElements), SemTuple(rightElements)) ->
                        unifyTypeLists(
                            leftElements,
                            rightElements,
                            substitution
                        )
                    | (SemFunction(leftArgument, leftResult, leftRow), SemFunction(rightArgument, rightResult, rightRow)) ->
                        match unifyWith(substitution)(leftArgument)(rightArgument) with
                            | UnificationResult { substitution = afterArgument, error = None } ->
                                match unifyWith(afterArgument)(leftResult)(rightResult) with
                                    | UnificationResult { substitution = afterResult, error = None } ->
                                        unifyOptionalRows(
                                            leftRow,
                                            rightRow,
                                            afterResult
                                        )
                                    | failure -> failure
                            | failure -> failure
                    | (SemCapability(leftName, leftArguments), SemCapability(rightName, rightArguments)) ->
                        if leftName == rightName
                        then unifyTypeLists(leftArguments)(rightArguments)(substitution)
                        else unificationFailure(substitution)(TypeMismatch(resolvedLeft)(resolvedRight))
                    | (SemRow(leftCapabilities, leftTail), SemRow(rightCapabilities, rightTail)) ->
                        unifyRows(
                            leftCapabilities,
                            leftTail,
                            rightCapabilities,
                            rightTail,
                            substitution,
                            resolvedLeft,
                            resolvedRight
                        )
                    | (SemNamed(leftId, _leftName, leftArguments), SemNamed(rightId, _rightName, rightArguments)) ->
                        if leftId == rightId
                        then unifyTypeLists(leftArguments)(rightArguments)(substitution)
                        else unificationFailure(substitution)(TypeMismatch(resolvedLeft)(resolvedRight))
                    | (SemParameter(leftId, _leftName), SemParameter(rightId, _rightName)) ->
                        if leftId == rightId
                        then unificationSuccess(substitution)
                        else unificationFailure(substitution)(TypeMismatch(resolvedLeft)(resolvedRight))
                    | (SemOpaque(leftName), SemOpaque(rightName)) ->
                        if leftName == rightName
                        then unificationSuccess(substitution)
                        else unificationFailure(substitution)(TypeMismatch(resolvedLeft)(resolvedRight))
                    | (SemPointer(leftPointee), SemPointer(rightPointee)) ->
                        unifyWith(
                            substitution,
                            leftPointee,
                            rightPointee
                        )
                    | _ -> unificationFailure(substitution)(TypeMismatch(resolvedLeft)(resolvedRight)))

let unify left right = unifyWith([])(left)(right)
