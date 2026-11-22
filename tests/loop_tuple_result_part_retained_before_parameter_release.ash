// expect: 3 Eq,Eq,Eq, 3
// Regression: a loop returning a tuple of its accumulators, one grown by appending a field read out
// of a map lookup. The tuple's parts are handed to the caller before the loop's exit releases the
// accumulator parameter they may be; releasing the parameter first freed the list the caller got.
type Ty =
    | TyVar(Int)
    | TyList(Ty)

type C =
    | name: Str
    | args: List(Ty)

type Summary =
    | constraints: List(C)
    | stages: List(Int)

let recursive appendC left right =
    match left with
        | [] -> right
        | head :: tail -> head :: appendC(tail)(right)

let recursive sites flows summaries requirements demands =
    match flows with
        | [] -> (requirements, demands)
        | (node, name) :: rest ->
            match Ashes.Collection.Map.getStr(name)(summaries) with
                | Some(Summary { constraints = required }) ->
                    sites(rest)(summaries)(appendC(required)(requirements))(node :: demands)
                | None -> sites(rest)(summaries)(requirements)(demands)

let eq = C(name = "Eq", args = [TyVar(7)])

let flows = [(1025, "same"), (1027, "same")]

let summaries = Ashes.Collection.Map.setStr("same")(Summary(constraints = [eq], stages = [1]))(Ashes.Collection.Map.empty)

let recursive names cs =
    match cs with
        | [] -> ""
        | C { name = name } :: rest -> name + "," + names(rest)

match sites(flows)(summaries)([eq])([1000]) with
    | (requirements, demands) ->
        Ashes.Text.fromInt(Ashes.Collection.List.length(requirements)) + " " + names(requirements) + " " + Ashes.Text.fromInt(Ashes.Collection.List.length(demands)) |> Ashes.IO.print
