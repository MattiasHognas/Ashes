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
            | TypeVariable(otherId) ->
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
        | (None, Some(rightRow)) -> unifyWith(substitution)(TypeRow([])(None))(rightRow)
        | (Some(leftRow), None) -> unifyWith(substitution)(leftRow)(TypeRow([])(None))
and unifyWith substitution left right =
    (let resolvedLeft = applySubstitution(substitution)(left)
    in
        let resolvedRight = applySubstitution(substitution)(right)
        in
            if resolvedLeft == resolvedRight
            then unificationSuccess(substitution)
            else
                match (resolvedLeft, resolvedRight) with
                    | (TypeVariable(variableId), semanticType) -> bindVariable(variableId)(semanticType)(substitution)
                    | (semanticType, TypeVariable(variableId)) -> bindVariable(variableId)(semanticType)(substitution)
                    | (TypeUInt(leftBits), TypeUInt(rightBits)) ->
                        if leftBits == rightBits
                        then unificationSuccess(substitution)
                        else unificationFailure(substitution)(TypeMismatch(resolvedLeft)(resolvedRight))
                    | (TypeList(leftElement), TypeList(rightElement)) -> unifyWith(substitution)(leftElement)(rightElement)
                    | (TypeTuple(leftElements), TypeTuple(rightElements)) -> unifyTypeLists(leftElements)(rightElements)(substitution)
                    | (TypeFunction(leftArgument, leftResult, leftRow), TypeFunction(rightArgument, rightResult, rightRow)) ->
                        match unifyWith(substitution)(leftArgument)(rightArgument) with
                            | UnificationResult { substitution = afterArgument, error = None } ->
                                match unifyWith(afterArgument)(leftResult)(rightResult) with
                                    | UnificationResult { substitution = afterResult, error = None } -> unifyOptionalRows(leftRow)(rightRow)(afterResult)
                                    | failure -> failure
                            | failure -> failure
                    | (TypeCapability(leftName, leftArguments), TypeCapability(rightName, rightArguments)) ->
                        if leftName == rightName
                        then unifyTypeLists(leftArguments)(rightArguments)(substitution)
                        else unificationFailure(substitution)(TypeMismatch(resolvedLeft)(resolvedRight))
                    | (TypeRow(leftCapabilities, leftTail), TypeRow(rightCapabilities, rightTail)) ->
                        match unifyTypeLists(leftCapabilities)(rightCapabilities)(substitution) with
                            | UnificationResult { substitution = afterCapabilities, error = None } -> unifyOptionalRows(leftTail)(rightTail)(afterCapabilities)
                            | failure -> failure
                    | (TypeNamed(leftId, _leftName, leftArguments), TypeNamed(rightId, _rightName, rightArguments)) ->
                        if leftId == rightId
                        then unifyTypeLists(leftArguments)(rightArguments)(substitution)
                        else unificationFailure(substitution)(TypeMismatch(resolvedLeft)(resolvedRight))
                    | (TypeParameter(leftId, _leftName), TypeParameter(rightId, _rightName)) ->
                        if leftId == rightId
                        then unificationSuccess(substitution)
                        else unificationFailure(substitution)(TypeMismatch(resolvedLeft)(resolvedRight))
                    | (TypeOpaque(leftName), TypeOpaque(rightName)) ->
                        if leftName == rightName
                        then unificationSuccess(substitution)
                        else unificationFailure(substitution)(TypeMismatch(resolvedLeft)(resolvedRight))
                    | (TypePointer(leftPointee), TypePointer(rightPointee)) -> unifyWith(substitution)(leftPointee)(rightPointee)
                    | _ -> unificationFailure(substitution)(TypeMismatch(resolvedLeft)(resolvedRight)))

let unify left right = unifyWith([])(left)(right)
