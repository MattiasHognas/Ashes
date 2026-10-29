// expect: 121000
import Ashes.Collection.List.reverse as reverseList
type ResolvedPatch =
    | resolvedOffset: Int
    | resolvedBytes: Bytes

let makePatch (index: Int) = ResolvedPatch(resolvedOffset = index, resolvedBytes = Ashes.Byte.allocate(4))

let recursive literalPatches (index: Int) (count: Int) (acc: List(ResolvedPatch)) =
    if index >= count
    then reverseList(acc)
    else literalPatches(index + 1)(count)(ResolvedPatch(resolvedOffset = index * 10 + 3, resolvedBytes = Ashes.Byte.allocate(4)) :: acc)

let recursive helperPatches (index: Int) (count: Int) (acc: List(ResolvedPatch)) =
    if index >= count
    then acc
    else
        let patch = makePatch(index)
        in helperPatches(index + 1)(count)(patch :: acc)

let recursive totalOffsets (patches: List(ResolvedPatch)) (total: Int) =
    match patches with
        | [] -> total
        | ResolvedPatch { resolvedOffset = offset, resolvedBytes = replacement } :: rest -> totalOffsets(rest)(total + offset + Ashes.Byte.length(replacement))

let recursive repeat (left: Int) (total: Int) =
    if left <= 0
    then total
    else
        repeat(left - 1)(total + totalOffsets(literalPatches(0)(10)([]))(0) + totalOffsets(helperPatches(0)(10)([]))(0))

0
|> repeat(200)
|> Ashes.IO.print
