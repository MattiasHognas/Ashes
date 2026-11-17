// A call to a later member of a mutually recursive group whose result the caller asks for in the
// arena gets the callee's arena copy back, so the caller copies it out of the call's window even
// though the callee's compiled body returns a reference-counted list.
// expect: [x]

type Pattern =
    | PatternAt(Int, Pattern)
    | PatternVar(Str)
    | PatternTuple(List(Pattern))
    | PatternWildcard

let recursive patternBinders (pattern: Pattern) (binders: List(Str)) =
    match pattern with
        | PatternAt(_span, inner) -> patternBinders(inner)(binders)
        | PatternVar(name) -> name :: binders
        | PatternTuple(elements) -> patternListBinders(elements)(binders)
        | _ -> binders
and patternListBinders (patterns: List(Pattern)) (binders: List(Str)) =
    match patterns with
        | [] -> binders
        | pattern :: rest ->
            binders
            |> patternBinders(pattern)
            |> patternListBinders(rest)

Ashes.IO.print("[" + Ashes.Text.join(",")(patternBinders(PatternAt(7)(PatternVar("x")))([])) + "]")
