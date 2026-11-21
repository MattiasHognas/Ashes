// The first source application stage at which each inferred dictionary is used. A negative
// stage denotes a requirement that has no operation at this use site.
import AshesCompiler.Frontend.Syntax
import AshesCompiler.Semantics.Types
import Ashes.Collection.Map.MapTree
export (
    value demandStagesForValue,
    value zipEvidenceStages,
    value functionArgumentCount,
)

let recursive functionArgumentCount semanticType =
    match semanticType with
        | SemFunction(_argument, result, _row) -> 1 + functionArgumentCount(result)
        | _ -> 0

let recursive zipEvidenceStages (requirements: List(TraitConstraint)) stages fallback =
    match requirements with
        | [] -> []
        | requirement :: rest ->
            match stages with
                | first :: tail -> (requirement, first) :: zipEvidenceStages(rest)(tail)(fallback)
                | [] -> (requirement, fallback) :: zipEvidenceStages(rest)([])(fallback)

let recursive projectSites sites applied depth escapes found =
    match sites with
        | [] -> found
        | (requirement, stage) :: rest ->
            if stage < 0 || stage > applied && escapes == false
            then projectSites(rest)(applied)(depth)(escapes)(found)
            else
                let needed =
                    if stage > applied
                    then depth + stage - applied
                    else depth
                in projectSites(rest)(applied)(depth)(escapes)((requirement, needed) :: found)

// A callee consumes an argument stage. Evaluating the argument, condition or discarded
// binding only demands its immediate operations. A branch can return a still-latent value.
let recursive walk (expression: Expr) (applied: Int) (depth: Int) (escapes: Bool) (sites: MapTree(Int, List((TraitConstraint, Int)))) found =
    match expression with
        | ExprAt(_span, inner) -> walk(inner)(applied)(depth)(escapes)(sites)(found)
        | ExprNode(id, inner) ->
            let withDirect =
                match Ashes.Collection.Map.getInt(id)(sites) with
                    | Some(demands) -> projectSites(demands)(applied)(depth)(escapes)(found)
                    | None -> found
            in walk(inner)(applied)(depth)(escapes)(sites)(withDirect)
        | ExprLambda(_name, body, _annotation) ->
            if applied > 0
            then walk(body)(applied - 1)(depth)(escapes)(sites)(found)
            else
                if escapes
                then walk(body)(0)(depth + 1)(true)(sites)(found)
                else found
        | ExprCall(callee, arg, _whitespace, _layout) ->
            found
            |> walk(arg)(0)(depth)(false)(sites)
            |> walk(callee)(applied + 1)(depth)(escapes)(sites)
        | ExprLet(_name, value, body, _parameters, _annotation, _requirements) ->
            found
            |> walk(value)(0)(depth)(false)(sites)
            |> walk(body)(applied)(depth)(escapes)(sites)
        | ExprLetRecursive(_name, value, body, _parameters, _annotation, _requirements) ->
            found
            |> walk(value)(0)(depth)(false)(sites)
            |> walk(body)(applied)(depth)(escapes)(sites)
        | ExprLetResult(_name, value, body) ->
            found
            |> walk(value)(0)(depth)(false)(sites)
            |> walk(body)(applied)(depth)(escapes)(sites)
        | ExprIf(condition, yes, no) ->
            found
            |> walk(condition)(0)(depth)(false)(sites)
            |> walk(yes)(applied)(depth)(escapes)(sites)
            |> walk(no)(applied)(depth)(escapes)(sites)
        | ExprMatch(value, arms, _position) ->
            found
            |> walk(value)(0)(depth)(false)(sites)
            |> walkArms(arms)(applied)(depth)(escapes)(sites)
        | ExprHandle(value, arms) ->
            found
            |> walk(value)(applied)(depth)(escapes)(sites)
            |> walkHandlers(arms)(applied)(depth)(escapes)(sites)
        | ExprPerform(value) -> walk(value)(0)(depth)(false)(sites)(found)
        | ExprAwait(value) -> walk(value)(0)(depth)(false)(sites)(found)
        | ExprResultPipe(value, function) ->
            found
            |> walk(value)(0)(depth)(false)(sites)
            |> walk(function)(applied + 1)(depth)(escapes)(sites)
        | ExprResultMapErrorPipe(value, function) ->
            found
            |> walk(value)(0)(depth)(false)(sites)
            |> walk(function)(applied + 1)(depth)(escapes)(sites)
        | ExprTuple(values) -> walkValues(values)(depth)(sites)(found)
        | ExprList(values, _layout) -> walkValues(values)(depth)(sites)(found)
        | ExprRecord(_name, fields, _layout) -> walkFields(fields)(depth)(sites)(found)
        | ExprRecordUpdate(value, fields) ->
            found
            |> walk(value)(0)(depth)(false)(sites)
            |> walkFields(fields)(depth)(sites)
        | ExprAdd(left, right) -> walkValues([left, right])(depth)(sites)(found)
        | ExprSubtract(left, right) -> walkValues([left, right])(depth)(sites)(found)
        | ExprMultiply(left, right) -> walkValues([left, right])(depth)(sites)(found)
        | ExprDivide(left, right) -> walkValues([left, right])(depth)(sites)(found)
        | ExprModulo(left, right) -> walkValues([left, right])(depth)(sites)(found)
        | ExprBitwiseAnd(left, right) -> walkValues([left, right])(depth)(sites)(found)
        | ExprBitwiseOr(left, right) -> walkValues([left, right])(depth)(sites)(found)
        | ExprBitwiseXor(left, right) -> walkValues([left, right])(depth)(sites)(found)
        | ExprShiftLeft(left, right) -> walkValues([left, right])(depth)(sites)(found)
        | ExprShiftRight(left, right) -> walkValues([left, right])(depth)(sites)(found)
        | ExprBitwiseNot(operand) -> walk(operand)(0)(depth)(false)(sites)(found)
        | ExprLogicalNot(operand) -> walk(operand)(0)(depth)(false)(sites)(found)
        | ExprLogicalAnd(left, right) -> walkValues([left, right])(depth)(sites)(found)
        | ExprLogicalOr(left, right) -> walkValues([left, right])(depth)(sites)(found)
        | ExprGreaterThan(left, right) -> walkValues([left, right])(depth)(sites)(found)
        | ExprLessThan(left, right) -> walkValues([left, right])(depth)(sites)(found)
        | ExprGreaterOrEqual(left, right) -> walkValues([left, right])(depth)(sites)(found)
        | ExprLessOrEqual(left, right) -> walkValues([left, right])(depth)(sites)(found)
        | ExprEqual(left, right) -> walkValues([left, right])(depth)(sites)(found)
        | ExprNotEqual(left, right) -> walkValues([left, right])(depth)(sites)(found)
        | ExprCons(head, tail) -> walkValues([head, tail])(depth)(sites)(found)
        | _ -> found
and walkValues values depth sites found =
    match values with
        | [] -> found
        | head :: rest ->
            found
            |> walk(head)(0)(depth)(false)(sites)
            |> walkValues(rest)(depth)(sites)
and walkFields fields depth sites found =
    match fields with
        | [] -> found
        | (_name, head) :: rest ->
            found
            |> walk(head)(0)(depth)(false)(sites)
            |> walkFields(rest)(depth)(sites)
and walkArms arms applied depth escapes sites found =
    match arms with
        | [] -> found
        | (_pattern, body, guard) :: rest ->
            let guarded =
                match guard with
                    | Some(value) -> walk(value)(0)(depth)(false)(sites)(found)
                    | None -> found
            in
                guarded
                |> walk(body)(applied)(depth)(escapes)(sites)
                |> walkArms(rest)(applied)(depth)(escapes)(sites)
and walkHandlers arms applied depth escapes sites found =
    match arms with
        | [] -> found
        | (_capability, _name, _patterns, body) :: rest ->
            found
            |> walk(body)(applied)(depth)(escapes)(sites)
            |> walkHandlers(rest)(applied)(depth)(escapes)(sites)

let recursive indexSites root sites indexed =
    match sites with
        | [] -> indexed
        | (node, demands) :: rest ->
            if node < root
            then indexed
            else
                match Ashes.Collection.Map.getInt(node)(indexed) with
                    | Some(_younger) -> indexSites(root)(rest)(indexed)
                    | None ->
                        indexed
                        |> Ashes.Collection.Map.setInt(node)(demands)
                        |> indexSites(root)(rest)

let demandStagesForValue root value sites =
    match sites with
        | [] -> []
        | _ ->
            walk(value)(0)(0)(true)(indexSites(root)(sites)(Ashes.Collection.Map.empty))([])
