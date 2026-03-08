// expect: 10
let add x y = x + y
in 
    let r = add(3) 7
    in Ashes.IO.print r
