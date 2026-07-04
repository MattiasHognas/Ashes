// expect-compile-error: 
module M =
    let a = later
    let later = 5

Ashes.IO.print(0)
