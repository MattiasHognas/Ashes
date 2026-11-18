// A body comparing lists of its parameter needs `Eq(List(a))`, which the list implementation reduces
// to `Eq(a)`: that is the requirement the binding states.
// expect: yes
let pairs : a -> Bool requires {Eq(a)} =
    given (x) -> [x] == [x]

if pairs(1)
then Ashes.IO.print("yes")
else Ashes.IO.print("no")
