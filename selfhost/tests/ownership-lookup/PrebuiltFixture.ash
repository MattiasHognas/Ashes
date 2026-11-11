import AshesCompiler.Frontend.Syntax
import AshesCompiler.Frontend.Token.TextSpan
export (
    value fixture,
    value matchesFixture,
    value wrappedFixture,
    value matchesWrappedFixture,
)

let call (callee: Expr) (argument: Expr) = ExprCall(callee)(argument)(false)(callArgumentsInline)

let fixture : List((Str, List(Str), Expr)) =
    [
        ("peek", ["h"], call(call(ExprQualifiedVar("Ashes.IO.File")("readChunk"))(ExprVar("h")))(ExprInt(2))),
        ("mid", ["h"], call(ExprVar("peek"))(ExprVar("h"))),
        ("outer", ["h"], call(ExprVar("mid"))(ExprVar("h")))
    ]

let recursive unspan (body: Expr) =
    match body with
        | ExprAt(_span, inner) -> unspan(inner)
        | other -> other

let recursive sameBody (left: Expr) (right: Expr) =
    match (unspan(left), unspan(right)) with
        | (ExprVar(a), ExprVar(b)) -> a == b
        | (ExprQualifiedVar(am, an), ExprQualifiedVar(bm, bn)) -> am == bm && an == bn
        | (ExprInt(a), ExprInt(b)) -> a == b
        | (ExprCall(af, aa, at, al), ExprCall(bf, ba, bt, bl)) -> at == bt && al == bl && sameBody(af)(bf) && sameBody(aa)(ba)
        | _ -> false

let recursive sameFunctions (left: List((Str, List(Str), Expr))) (right: List((Str, List(Str), Expr))) =
    match (left, right) with
        | ([], []) -> true
        | ((an, ap, ab) :: ar, (bn, bp, bb) :: br) -> an == bn && ap == bp && sameBody(ab)(bb) && sameFunctions(ar)(br)
        | _ -> false

let matchesFixture (functions: List((Str, List(Str), Expr))) = sameFunctions(functions)(fixture)

let at (start: Int) (end: Int) (body: Expr) = ExprAt(TextSpan(start = start, end = end))(body)

// Exact stage-0 parser output for the three-function source in Main.ash.
let wrappedFixture : List((Str, List(Str), Expr)) =
    [
        ("peek", ["h"], ExprInt(2)
        |> at(40)(41)
        |> call(ExprVar("h")
        |> at(37)(38)
        |> call("readChunk"
        |> ExprQualifiedVar("Ashes.IO.File")
        |> at(13)(36))
        |> at(13)(39))),
        ("mid", ["h"], ExprVar("h")
        |> at(60)(61)
        |> call(at(55)(59)(ExprVar("peek")))),
        ("outer", ["h"], ExprVar("h")
        |> at(81)(82)
        |> call(at(77)(80)(ExprVar("mid"))))
    ]

let recursive sameWrappedBody (left: Expr) (right: Expr) =
    match (left, right) with
        | (ExprAt(a, ai), ExprAt(b, bi)) -> a.start == b.start && a.end == b.end && sameWrappedBody(ai)(bi)
        | (ExprVar(a), ExprVar(b)) -> a == b
        | (ExprQualifiedVar(am, an), ExprQualifiedVar(bm, bn)) -> am == bm && an == bn
        | (ExprInt(a), ExprInt(b)) -> a == b
        | (ExprCall(af, aa, at, al), ExprCall(bf, ba, bt, bl)) -> at == bt && al == bl && sameWrappedBody(af)(bf) && sameWrappedBody(aa)(ba)
        | _ -> false

let recursive sameWrappedFunctions (left: List((Str, List(Str), Expr))) (right: List((Str, List(Str), Expr))) =
    match (left, right) with
        | ([], []) -> true
        | ((an, ap, ab) :: ar, (bn, bp, bb) :: br) -> an == bn && ap == bp && sameWrappedBody(ab)(bb) && sameWrappedFunctions(ar)(br)
        | _ -> false

let matchesWrappedFixture (functions: List((Str, List(Str), Expr))) = sameWrappedFunctions(functions)(wrappedFixture)
