// expect-compile-error: ASH010
let recursive invert x = !x

let alias = invert

(alias, 42)
