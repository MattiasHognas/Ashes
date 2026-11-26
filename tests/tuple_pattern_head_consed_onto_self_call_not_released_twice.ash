// expect: 102 A1,Ashes.Trait,C,
// A recursive function consing the first component of a matched pair onto its own call, over
// pairs whose records the caller keeps: the cell's duplicate of that head stayed a marker, so
// releasing it behind the join's copy would free a record the caller still owns. Each round
// keeps the regions intact.
type Region =
    | moduleName: Str
    | packageId: Str
    | sourcePath: Str
    | itemStart: Int
    | itemEnd: Int
    | isEntry: Bool
    deriving {Eq, Show}

type Expr =
    | Leaf(Str)
    | Node(Expr, Expr)
    | Fn(Int -> Int)

type Unit2 =
    | items: List(Expr)
    | body: Maybe(Expr)

let recursive regionUnits (regions: List(Region)) (units: List(Unit2)) =
    match (regions, units) with
        | (region :: restRegions, unit :: restUnits) -> (region, unit) :: regionUnits(restRegions)(restUnits)
        | _ -> []

let isTrait (region: Region) = region.moduleName == "Ashes.Trait"

let recursive regionsOf (pairs: List((Region, Unit2))) =
    match pairs with
        | [] -> []
        | (region, _unit) :: rest -> region :: regionsOf(rest)

let recursive names (regions: List(Region)) (acc: Str) =
    match regions with
        | [] -> acc
        | region :: rest -> names(rest)(acc + region.moduleName + ",")

let region (name: Str) (entry: Bool) = Region(moduleName = name, packageId = "p" + name, sourcePath = name + ".ash", itemStart = 0, itemEnd = 1, isEntry = entry)

let unit (n: Int) =
    Unit2(items = [Leaf("x" + Ashes.Text.fromInt(n)), Node(Leaf("a"))(Fn(given (x: Int) -> x + n))], body = Some(Leaf("b")))

let recursive rounds (regions: List(Region)) (units: List(Unit2)) (n: Int) (acc: Int) =
    if n == 0
    then acc
    else
        rounds(regions)(units)(n - 1)(acc + Ashes.Text.length(names(units
        |> regionUnits(regions)
        |> regionsOf)("")))

let regions = [region("A" + "1")(false), region("Ashes." + "Trait")(false), region("C" + "")(true)]

let units = [unit(1), unit(2), unit(3)]

Ashes.IO.print(Ashes.Text.fromInt(rounds(regions)(units)(6)(0)) + " " + names(regions)(""))
