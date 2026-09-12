// expect: 3|ab|2
// An inline module reaches another namespace only by qualified access: it may not carry an
// import (ASH021) and does not inherit the file's. The lifted submodules used to be stitched
// ahead of the standard library, making every such reference a forward reference.
module Counting =
    let size xs = Ashes.Collection.List.length(xs)

module Rendering =
    let squeeze text = Ashes.Text.trim(text)
    let label n = Ashes.Text.fromInt(n)

module Nested =
    module Inner =
        let pairs entries = Ashes.Collection.Map.size(Ashes.Collection.Map.fromList(entries))

Ashes.IO.print(Rendering.label(Counting.size([1, 2, 3])) + "|" + Rendering.squeeze("  ab  ") + "|" + Rendering.label(Nested.Inner.pairs([("a", 1), ("b", 2)])))
