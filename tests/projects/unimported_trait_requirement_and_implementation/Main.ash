// expect: 42
import Rendering
implement Label(Int) =
    | text =
        given (value) -> Ashes.Text.fromInt(value)

let describe : a -> Str requires {Label(a)} =
    given (value) -> Rendering.render(value)

42
|> describe
|> Ashes.IO.print
