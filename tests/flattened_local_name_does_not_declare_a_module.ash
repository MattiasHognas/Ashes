// expect-compile-error: Unknown module 'Names'.
let Names_collect x = x + 1

1
|> Names.collect
|> Ashes.IO.print
