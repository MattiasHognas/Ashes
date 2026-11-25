// expect: 60 A11,B2,C3,
// A loop replacing a matched entry returns `(entry) :: tail` over the tail it matched, while the
// caller keeps the list: a record holding a closure keeps the loop parameter in the arena, so the
// tail duplicate stays a marker and its owned slot must stay empty, and each round keeps the
// caller's entries intact.
type Region =
    | moduleName: Str
    | packageId: Str
    | step: Int -> Int

let recursive setFirst (name: Str) (pairs: List((Region, Int))) =
    match pairs with
        | [] -> []
        | (region, n) :: tail ->
            if region.moduleName == name
            then
                (Region(moduleName = name, packageId = "q", step = given (x: Int) -> x + 1), n + 1) :: tail
            else (region, n) :: setFirst(name)(tail)

let recursive names (pairs: List((Region, Int))) (acc: Str) =
    match pairs with
        | [] -> acc
        | (region, n) :: rest -> names(rest)(acc + region.moduleName + Ashes.Text.fromInt(n) + ",")

let region (name: Str) =
    Region(moduleName = name, packageId = "p" + name, step = given (x: Int) -> x)

let recursive rounds (pairs: List((Region, Int))) (n: Int) (acc: Int) =
    if n == 0
    then acc
    else
        rounds(pairs)(n - 1)(acc + Ashes.Text.length(names(setFirst("B" + "")(pairs))("")))

let pairs = [(region("A" + "1"), 1), (region("B" + ""), 2), (region("C" + ""), 3)]

Ashes.IO.print(Ashes.Text.fromInt(rounds(pairs)(6)(0)) + " " + names(pairs)(""))
