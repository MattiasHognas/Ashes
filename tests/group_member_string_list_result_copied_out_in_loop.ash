// expect: root|  a|    a1|  b
import Ashes.Text
import Ashes.IO
import Ashes.Collection.List
// A recursive group rendering a tree into lines: each member returns a list of strings, which has a
// fixed copy-out, and the calls between members read no returns flag and ask for no arena result.
// The result is copied out of the call's arena window before the window is reset, as stage 0 does,
// instead of keeping the window open while the walk goes on.
type Node =
    | Node(Str, List(Node))

let recursive renderNode prefix node =
    match node with
        | Node(name, children) -> prefix + name :: renderChildren(prefix + "  ")(children)
and renderChildren prefix children =
    match children with
        | [] -> []
        | child :: [] -> renderNode(prefix)(child)
        | child :: rest ->
            rest
            |> renderChildren(prefix)
            |> Ashes.Collection.List.append(renderNode(prefix)(child))

let tree = Node("root")([Node("a")([Node("a1")([])]), Node("b")([])])
in
    tree
    |> renderNode("")
    |> Ashes.Text.join("|")
    |> Ashes.IO.print
