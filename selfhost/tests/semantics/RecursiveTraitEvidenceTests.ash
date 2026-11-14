import AshesCompiler.Frontend.Syntax
import AshesCompiler.Frontend.Token.TextSpan
import AshesCompiler.Semantics.RecursiveTraitEvidence
export (
    value runRecursiveTraitEvidenceTests,
)

let assert condition message =
    if condition
    then Unit
    else Ashes.IO.panic(message)

let self = ExprVar("loop")

let evidence = ["dictionary0", "dictionary1"]

let rewrite expression =
    match rewriteRecursiveTraitEvidence("loop")(evidence)(expression) with
        | Some(rewritten) -> rewritten
        | None -> Ashes.IO.panic("valid recursive evidence rewrite failed")

let forwarded expression =
    match expression with
        | ExprCall(ExprCall(ExprVar("loop"), ExprVar("dictionary0"), false, firstLayout), ExprVar("dictionary1"), false, secondLayout) -> firstLayout == callArgumentsInline && secondLayout == callArgumentsInline
        | _ -> false

let ordinarySelf expression =
    match expression with
        | ExprVar("loop") -> true
        | _ -> false

let checkCall unit =
    match callArgumentsPipe
    |> ExprCall(self)(ExprAdd(ExprVar("x"))(ExprInt(1)))(true)
    |> ExprAt(TextSpan(start = 12, end = 38))
    |> rewrite with
        | ExprAt(TextSpan { start = 12, end = 38 }, ExprCall(root, ExprAdd(ExprVar("x"), ExprInt(1)), true, layout)) -> assert(forwarded(root) && layout == callArgumentsPipe)("dictionary order, recursive root, computed argument or span changed")
        | _ -> Ashes.IO.panic("self call metadata changed")

let checkAliases unit =
    match []
    |> ExprLet("alias")(self)(ExprLambda("x")(ExprTuple([ExprCall(self)(ExprVar("x"))(false)(callArgumentsInline), self, ExprVar("alias")]))(None))([])(None)
    |> rewrite with
        | ExprLet("alias", aliasValue, ExprLambda("x", ExprTuple(ExprCall(root, ExprVar("x"), false, _layout) :: returned :: ExprVar("alias") :: []), None), [], None, []) -> assert(forwarded(aliasValue) && forwarded(root) && forwarded(returned))("alias, partial call or escaped self reference missed evidence")
        | _ -> Ashes.IO.panic("alias expression changed")

let checkLambdaShadow unit =
    match Some(TypeNamed("Int"))
    |> ExprLambda("loop")(self)
    |> rewrite with
        | ExprLambda("loop", ExprVar("loop"), Some(TypeNamed("Int"))) -> Unit
        | _ -> Ashes.IO.panic("lambda self shadow rewritten")

let checkLetShadows unit =
    Unit
    |> (given (_) ->
        match []
        |> ExprLet("loop")(self)(self)([])(None)
        |> rewrite with
            | ExprLet("loop", outer, ExprVar("loop"), [], None, []) ->
                assert(forwarded(outer))("nonrecursive initializer did not see outer self")
            | _ -> Ashes.IO.panic("nonrecursive let shadow changed"))
    |> (given (_) ->
        match []
        |> ExprLetRecursive("loop")(self)(self)([])(None)
        |> rewrite with
            | ExprLetRecursive("loop", ExprVar("loop"), ExprVar("loop"), [], None, []) -> Unit
            | _ -> Ashes.IO.panic("recursive let shadow changed"))
    |> (given (_) ->
        match self
        |> ExprLetResult("loop")(self)
        |> rewrite with
            | ExprLetResult("loop", outer, ExprVar("loop")) ->
                assert(forwarded(outer))("result initializer did not see outer self")
            | _ -> Ashes.IO.panic("result let shadow changed"))
    |> (given (_) ->
        match []
        |> ExprLet("local")(self)(self)(["loop"])(None)
        |> rewrite with
            | ExprLet("local", ExprVar("loop"), outer, "loop" :: [], None, []) ->
                assert(forwarded(outer))("sugar parameter leaked into continuation")
            | _ -> Ashes.IO.panic("sugar parameter scope changed"))

let recursive checkPatterns patterns =
    match patterns with
        | [] -> Unit
        | pattern :: rest ->
            match Some(7)
            |> ExprMatch(self)([(pattern, self, Some(self)), (PatternWildcard, self, Some(self))])
            |> rewrite with
                | ExprMatch(scrutinee, (_pattern, ExprVar("loop"), Some(ExprVar("loop"))) :: (PatternWildcard, body, Some(guard)) :: [], Some(7)) ->
                    "match scope leaked across arms"
                    |> assert(forwarded(scrutinee) && forwarded(body) && forwarded(guard))
                    |> (given (_) -> checkPatterns(rest))
                | _ -> Ashes.IO.panic("pattern shadow did not cover guard and body")

let checkHandler unit =
    match [(None, "return", [PatternVar("loop")], self), (Some("loop"), "read", [PatternWildcard], self)]
    |> ExprHandle(ExprPerform(self))
    |> rewrite with
        | ExprHandle(ExprPerform(effect), (None, "return", PatternVar("loop") :: [], ExprVar("loop")) :: (Some("loop"), "read", PatternWildcard :: [], body) :: []) -> assert(forwarded(effect) && forwarded(body))("handler pattern shadow leaked")
        | _ -> Ashes.IO.panic("handler metadata or scope changed")

let checkAggregate unit =
    match [("value", ExprList([self, ExprQualifiedVar("Other")("loop")])(true))]
    |> ExprRecordUpdate(ExprRecord("Holder")([("value", ExprAwait(self))])(true))
    |> rewrite with
        | ExprRecordUpdate(ExprRecord("Holder", ("value", ExprAwait(awaited)) :: [], true), ("value", ExprList(item :: ExprQualifiedVar("Other", "loop") :: [], true)) :: []) -> assert(forwarded(awaited) && forwarded(item))("aggregate or await self reference missed evidence")
        | _ -> Ashes.IO.panic("aggregate metadata or qualified variable changed")

let rejected expression =
    match rewriteRecursiveTraitEvidence("loop")(evidence)(expression) with
        | None -> true
        | Some(_rewritten) -> false

let checkHygiene unit =
    Unit
    |> (given (_) ->
        assert(None
        |> ExprLambda("dictionary0")(self)
        |> rejected)("shadowed dictionary captured"))
    |> (given (_) ->
        assert([]
        |> ExprLet("dictionary1")(ExprInt(1))(self)([])(None)
        |> rejected)("let dictionary shadow captured"))
    |> (given (_) ->
        assert(None
        |> ExprMatch(ExprInt(1))([(PatternVar("dictionary0"), self, None)])
        |> rejected)("pattern dictionary shadow captured"))
    |> (given (_) ->
        match []
        |> ExprLet("dictionary0")(self)(ExprInt(1))([])(None)
        |> rewrite with
            | ExprLet("dictionary0", outer, ExprInt(1), [], None, []) ->
                assert(forwarded(outer))("nonrecursive dictionary shadow affected initializer")
            | _ -> Ashes.IO.panic("dictionary initializer scope changed"))
    |> (given (_) ->
        match None
        |> ExprLambda("dictionary0")(ExprLambda("loop")(self)(None))
        |> rewrite with
            | ExprLambda("dictionary0", ExprLambda("loop", ExprVar("loop"), None), None) -> Unit
            | _ -> Ashes.IO.panic("shadowed self unnecessarily rewrote dictionary"))
    |> (given (_) ->
        match rewriteRecursiveTraitEvidence("loop")([])(self) with
            | Some(ExprVar("loop")) -> Unit
            | _ -> Ashes.IO.panic("empty evidence changed source"))

let runRecursiveTraitEvidenceTests unit =
    Unit
    |> checkCall
    |> checkAliases
    |> checkLambdaShadow
    |> checkLetShadows
    |> (given (_) -> checkPatterns([PatternVar("loop"), PatternAt(TextSpan(start = 1, end = 3))(PatternVar("loop")), PatternCons(PatternWildcard)(PatternVar("loop")), PatternTuple([PatternVar("loop")]), PatternConstructor("Some")([PatternVar("loop")]), PatternRecord("Holder")([("value", PatternVar("loop"))]), PatternAs(PatternWildcard)("loop"), PatternOr([PatternVar("loop"), PatternVar("loop")])]))
    |> checkHandler
    |> checkAggregate
    |> checkHygiene
