// expect: 42000
import Ashes.Collection.List.reverse as reverseList
type ResolvedPatch =
    | resolvedOffset: Int
    | resolvedBytes: Bytes

let sliceBytes (bytes: Bytes) (start: Int) (length: Int) =
    length
    |> Ashes.Byte.subText(bytes)(start)
    |> Ashes.Byte.fromText

let recursive patchPieces (bytes: Bytes) (patches: List(ResolvedPatch)) (cursor: Int) (acc: List(Bytes)) =
    match patches with
        | [] -> reverseList(sliceBytes(bytes)(cursor)(Ashes.Byte.length(bytes) - cursor) :: acc)
        | ResolvedPatch { resolvedOffset = offset, resolvedBytes = replacement } :: rest ->
            if offset < cursor || offset + Ashes.Byte.length(replacement) > Ashes.Byte.length(bytes)
            then Ashes.IO.panic("overlap")
            else patchPieces(bytes)(rest)(offset + Ashes.Byte.length(replacement))(replacement :: sliceBytes(bytes)(cursor)(offset - cursor) :: acc)

let recursive makePatches (index: Int) (count: Int) (acc: List(ResolvedPatch)) =
    if index >= count
    then reverseList(acc)
    else makePatches(index + 1)(count)(ResolvedPatch(resolvedOffset = index * 10 + 3, resolvedBytes = Ashes.Byte.allocate(4)) :: acc)

let recursive totalLength (pieces: List(Bytes)) (total: Int) =
    match pieces with
        | [] -> total
        | piece :: rest -> totalLength(rest)(total + Ashes.Byte.length(piece))

let bytes = Ashes.Byte.allocate(210)

let patches = makePatches(0)(20)([])

let recursive repeat (left: Int) (total: Int) =
    if left <= 0
    then total
    else
        repeat(left - 1)(total + totalLength(patchPieces(bytes)(patches)(0)([]))(0))

0
|> repeat(200)
|> Ashes.IO.print
