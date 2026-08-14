import AshesCompiler.Semantics.Types
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
                | UnificationResult { substitution = nextSubstitution, error = None } -> unifyTypeLists(leftTail)(rightTail)(nextSubstitution)
                | failure -> failure
        | _ -> unificationFailure(substitution)(TypeArityMismatch(typeListLength(left))(typeListLength(right)))
and unifyOptionalRows left right substitution =
    match (left, right) with
        | (None, None) -> unificationSuccess(substitution)
        | (Some(leftRow), Some(rightRow)) -> unifyWith(substitution)(leftRow)(rightRow)
        | (None, Some(rightRow)) -> unifyWith(substitution)(SemRow([])(None))(rightRow)
        | (Some(leftRow), None) -> unifyWith(substitution)(leftRow)(SemRow([])(None))
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
                    | (SemList(leftElement), SemList(rightElement)) -> unifyWith(substitution)(leftElement)(rightElement)
                    | (SemTuple(leftElements), SemTuple(rightElements)) -> unifyTypeLists(leftElements)(rightElements)(substitution)
                    | (SemFunction(leftArgument, leftResult, leftRow), SemFunction(rightArgument, rightResult, rightRow)) ->
                        match unifyWith(substitution)(leftArgument)(rightArgument) with
                            | UnificationResult { substitution = afterArgument, error = None } ->
                                match unifyWith(afterArgument)(leftResult)(rightResult) with
                                    | UnificationResult { substitution = afterResult, error = None } -> unifyOptionalRows(leftRow)(rightRow)(afterResult)
                                    | failure -> failure
                            | failure -> failure
                    | (SemCapability(leftName, leftArguments), SemCapability(rightName, rightArguments)) ->
                        if leftName == rightName
                        then unifyTypeLists(leftArguments)(rightArguments)(substitution)
                        else unificationFailure(substitution)(TypeMismatch(resolvedLeft)(resolvedRight))
                    | (SemRow(leftCapabilities, leftTail), SemRow(rightCapabilities, rightTail)) ->
                        match unifyTypeLists(leftCapabilities)(rightCapabilities)(substitution) with
                            | UnificationResult { substitution = afterCapabilities, error = None } -> unifyOptionalRows(leftTail)(rightTail)(afterCapabilities)
                            | failure -> failure
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
                    | (SemPointer(leftPointee), SemPointer(rightPointee)) -> unifyWith(substitution)(leftPointee)(rightPointee)
                    | _ -> unificationFailure(substitution)(TypeMismatch(resolvedLeft)(resolvedRight)))

let unify left right = unifyWith([])(left)(right)
