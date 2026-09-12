module Deep =
    let size xs = Ashes.Collection.List.length(xs)

let describe xs = Ashes.Text.fromInt(Deep.size(xs))
