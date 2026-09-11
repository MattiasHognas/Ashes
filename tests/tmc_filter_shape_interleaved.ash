// A filter-shaped producer has TWO ways back into its loop: `then keep(tail)` is an ordinary tail
// self-call and `else head :: keep(tail)` is the tail-modulo-constructor cons. The ordinary one is
// lowered first, so it must already agree that the loop keeps its spine above the per-iteration arena
// watermark — otherwise every dropped element reclaims the cells the kept elements built, and the
// output silently loses earlier entries (this is the shape that regressed
// tests/path_pure_operations.ash, where "/a//b/../c/." normalized to "/c").
//
// The elements must be POINTER-BEARING for that to bite: a list of scalars gets a reference-counted
// spine, which the arena reset cannot touch, while a list of strings gets an arena spine that it can.
// Dropped and kept elements also have to interleave, so this drops every empty string from a list
// long enough to span several reclaims.
// expect: a,b,c,d,e,f,|6
import Ashes.IO as io
import Ashes.Text as text
let recursive dropEmpty parts =
    match parts with
        | [] -> []
        | head :: tail ->
            if head == ""
            then dropEmpty(tail)
            else head :: dropEmpty(tail)

let recursive render parts =
    match parts with
        | [] -> ""
        | head :: tail -> head + "," + render(tail)

let recursive count parts acc =
    match parts with
        | [] -> acc
        | _ :: tail -> count(tail)(acc + 1)

let kept = dropEmpty("" :: "a" :: "" :: "b" :: "" :: "" :: "c" :: "d" :: "" :: "e" :: "" :: "f" :: "" :: [])

io.print(render(kept) + "|" + text.fromInt(count(kept)(0)))
