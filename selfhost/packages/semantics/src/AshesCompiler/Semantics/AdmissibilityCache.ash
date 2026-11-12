import AshesCompiler.Semantics.Types
export (
    value concreteAdmissibilityType,
    value lookupAdmissibilityAnswer,
    value rememberAdmissibilityAnswer,
)

// Only complete, resolved value-type roots are cached. Other roots keep the ordinary query.
let recursive concreteAdmissibilityType semanticType =
    match semanticType with
        | SemVariable(_id) -> false
        | SemParameter(_id, _name) -> false
        | SemFunction(_argument, _result, _row) -> false
        | SemCapability(_name, _arguments) -> false
        | SemRow(_capabilities, _tail) -> false
        | SemPointer(_pointee) -> false
        | SemList(element) -> concreteAdmissibilityType(element)
        | SemTuple(elements) -> concreteAdmissibilityTypes(elements)
        | SemNamed(_id, _name, arguments) -> concreteAdmissibilityTypes(arguments)
        | _ -> true
and concreteAdmissibilityTypes types =
    match types with
        | [] -> true
        | head :: rest -> concreteAdmissibilityType(head) && concreteAdmissibilityTypes(rest)

// Exact type identity includes symbol IDs, unlike display strings used by the path-cycle rule.
let recursive lookupAdmissibilityAnswer semanticType (entries: List((SemanticType, Bool))) =
    match entries with
        | [] -> None
        | (previous, answer) :: rest ->
            if previous == semanticType
            then Some(answer)
            else lookupAdmissibilityAnswer(semanticType)(rest)

let recursive cacheHasRoom remaining entries =
    match entries with
        | [] -> remaining > 0
        | _head :: rest -> remaining > 1 && cacheHasRoom(remaining - 1)(rest)

// The caller inserts only complete query answers on misses. A bounded generation avoids
// retaining every historical generic instantiation; entries never capture lowering states.
let rememberAdmissibilityAnswer semanticType answer entries =
    if !concreteAdmissibilityType(semanticType)
    then entries
    else
        if cacheHasRoom(64)(entries)
        then (semanticType, answer) :: entries
        else [(semanticType, answer)]
