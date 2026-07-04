// expect: 42/yes
capability Show(a) =
    | show : a -> Str

provide Show(Int) =
    | show = 
        given (n) -> Ashes.Text.fromInt(n)

provide Show(Bool) =
    | show = 
        given (b) -> 
            if b
            then "yes"
            else "no"

let display = 
    given (x) -> Show.show(x)

Ashes.IO.print(display(42) + "/" + display(true))
