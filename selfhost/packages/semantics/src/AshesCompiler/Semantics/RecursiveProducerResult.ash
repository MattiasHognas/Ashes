// Lexical producer provenance, independent of uniqueness and physical placement.
import AshesCompiler.Frontend.Syntax
import AshesCompiler.Semantics.ModuleReferenceRewriting.patternNames
export (
    type RecursiveProducerBinding(..),
    value isRecursiveProducerResult,
)

type RecursiveProducerBinding =
    | OtherProducerBinding
    | RecursiveProducerFunction
    | RecursiveProducerValue

let recursive containsName name names =
    match names with
        | [] -> false
        | head :: tail -> head == name || containsName(name)(tail)

let recursive recursiveCallRoot expression resolve =
    match expression with
        | ExprAt(_span, inner) -> recursiveCallRoot(inner)(resolve)
        | ExprCall(function, _argument, _sugar, _layout) -> recursiveCallRoot(function)(resolve)
        | ExprVar(name) ->
            match resolve(name) with
                | RecursiveProducerFunction -> true
                | _ -> false
        | _ -> false

let recursive isRecursiveProducerResult expression resolve =
    match expression with
        | ExprAt(_span, inner) -> isRecursiveProducerResult(inner)(resolve)
        | ExprVar(name) ->
            match resolve(name) with
                | RecursiveProducerValue -> true
                | _ -> false
        | ExprCall(_function, _argument, _sugar, _layout) -> recursiveCallRoot(expression)(resolve)
        | ExprLet(name, value, body, _parameters, _annotation, _traits) ->
            let valueBinding =
                if isRecursiveProducerResult(value)(resolve)
                then RecursiveProducerValue
                else OtherProducerBinding
            in
                isRecursiveProducerResult(body)(given (candidate) ->
                    if candidate == name
                    then valueBinding
                    else resolve(candidate))
        | ExprIf(_condition, thenBody, elseBody) -> isRecursiveProducerResult(thenBody)(resolve) && isRecursiveProducerResult(elseBody)(resolve)
        | ExprMatch(_value, cases, _position) ->
            match cases with
                | [] -> false
                | _ -> recursiveResultArms(cases)(resolve)
        | _ -> false
and recursiveResultArms cases resolve =
    match cases with
        | [] -> true
        | (pattern, body, _guard) :: rest ->
            let names = patternNames(pattern)
            in
                isRecursiveProducerResult(body)(given (name) ->
                    if containsName(name)(names)
                    then OtherProducerBinding
                    else resolve(name)) && recursiveResultArms(rest)(resolve)
