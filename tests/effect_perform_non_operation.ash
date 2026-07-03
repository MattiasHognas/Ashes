// expect-compile-error: 'perform' must be applied to an effect operation call

let double = 
    fun (x) -> x * 2

let y = perform double(21)

Ashes.IO.print(y)
