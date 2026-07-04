// expect-compile-error: 'perform' must be applied to an effect operation call

let double = 
    given (x) -> x * 2

let y = perform double(21)

Ashes.IO.print(y)
