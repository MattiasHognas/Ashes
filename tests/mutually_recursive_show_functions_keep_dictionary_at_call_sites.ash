// expect: 42 true
let recursive display value depth =
    if depth == 0
    then Ashes.Trait.Show.show(value)
    else bounce(value)(depth - 1)
and bounce value depth =
    if depth == 0
    then Ashes.Trait.Show.show(value)
    else display(value)(depth - 1)

Ashes.IO.print(display(42)(2) + " " + bounce(true)(2))
