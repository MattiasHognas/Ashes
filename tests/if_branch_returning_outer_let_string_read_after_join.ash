// An if branch returning a reference-counted let binding bound before the if, read again after the
// join: the branch takes a reference of its own and the binding stays live.
// expect: addTimes1/__in_place_41 __in_place_421/__in_place_42
type Cand =
    | name: Str
    | isRecursive: Bool

let keep (name: Str) (tag: Int) = name + Ashes.Text.fromInt(tag)

let run (cand: Cand) (seed: Int) =
    (let callName =
        let next = seed + 1
        in "__in_place_" + Ashes.Text.fromInt(next)
    in
        let groupName =
            if cand.isRecursive
            then cand.name
            else callName
        in keep(groupName)(1) + "/" + callName)

Ashes.IO.print(run(Cand(name = "addTimes", isRecursive = true))(40) + " " + run(Cand(name = "other", isRecursive = false))(41))
