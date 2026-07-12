let assertEqual expected actual =
    if expected == actual
    then Unit
    else Ashes.IO.panic("Assertion failed")

let fail msg = Ashes.IO.panic(msg)
