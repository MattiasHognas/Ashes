// expect: tag:tag:int|tag:tag:bool
type Tag =
    | Tag

trait Render(a) =
    | render : a -> Str

implement Render(Tag) =
    | render =
        given (_value) -> "tag"

implement Render(Int) =
    | render =
        given (_value) -> "int"

implement Render(Bool) =
    | render =
        given (_value) -> "bool"

let describe : a -> Str requires {Render(a)} =
    given (value) ->
        let fixed : Tag -> Str = Render.render
        in fixed(Tag) + ":" + Render.render(Tag) + ":" + Render.render(value)

Ashes.IO.print(describe(42) + "|" + describe(true))
