// expect: 9|-1|20|2|1|-5|1|2|7|5|16|4|-6|true|true|true|true|true|true
import Ashes.Trait
type Box =
    | Box(Int)

let value boxed =
    match boxed with
        | Box(inner) -> inner

implement Add(Box) =
    | add =
        given (left) ->
            given (right) -> Box(value(left) + value(right))

implement Subtract(Box) =
    | subtract =
        given (left) ->
            given (right) -> Box(value(left) - value(right))

implement Multiply(Box) =
    | multiply =
        given (left) ->
            given (right) -> Box(value(left) * value(right))

implement Divide(Box) =
    | divide =
        given (left) ->
            given (right) -> Box(value(left) / value(right))

implement Remainder(Box) =
    | remainder =
        given (left) ->
            given (right) -> Box(value(left) % value(right))

implement Negate(Box) =
    | negate =
        given (boxed) -> Box(-value(boxed))

implement Not(Box) =
    | not =
        given (boxed) -> Box(value(boxed) ^ 1)

implement BitAnd(Box) =
    | bitAnd =
        given (left) ->
            given (right) -> Box(value(left) & value(right))

implement BitOr(Box) =
    | bitOr =
        given (left) ->
            given (right) -> Box(value(left) | value(right))

implement BitXor(Box) =
    | bitXor =
        given (left) ->
            given (right) -> Box(value(left) ^ value(right))

implement ShiftLeft(Box) =
    | shiftLeft =
        given (left) ->
            given (right) -> Box(value(left) << value(right))

implement ShiftRight(Box) =
    | shiftRight =
        given (left) ->
            given (right) -> Box(value(left) >> value(right))

implement BitwiseNot(Box) =
    | bitwiseNot =
        given (boxed) -> Box(~value(boxed))

implement Eq(Box) =
    | equal =
        given (left) ->
            given (right) -> value(left) == value(right)

implement Ord(Box) =
    | compare =
        given (left) ->
            given (right) ->
                if value(left) < value(right)
                then Less
                else
                    if value(left) > value(right)
                    then Greater
                    else Equal

let showBox boxed = Ashes.Text.fromInt(value(boxed))

let showBool boolean =
    if boolean
    then "true"
    else "false"

let join left right = left + "|" + right

let output = join(showBox(Box(7) + Box(2)))(join(showBox(Box(7) - Box(8)))(join(showBox(Box(5) * Box(4)))(join(showBox(Box(8) / Box(4)))(join(showBox(Box(7) % Box(3)))(join(showBox(-Box(5)))(join(showBox(!Box(0)))(join(showBox(Box(3) & Box(6)))(join(showBox(Box(3) | Box(6)))(join(showBox(Box(3) ^ Box(6)))(join(showBox(Box(1) << Box(4)))(join(showBox(Box(8) >> Box(1)))(join(showBox(~Box(5)))(join(showBool(Box(3) == Box(3)))(join(showBool(Box(3) != Box(4)))(join(showBool(Box(3) < Box(4)))(join(showBool(Box(3) <= Box(3)))(join(showBool(Box(4) > Box(3)))(showBool(Box(4) >= Box(4))))))))))))))))))))
in Ashes.IO.print(output)
