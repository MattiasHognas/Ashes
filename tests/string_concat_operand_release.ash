// A concatenation copies its operands' bytes into a fresh allocation, so an operand nothing else
// owns is dead once the copy is done. Reference-counted operands used to be abandoned there instead
// of released, leaking one allocation per operand per evaluation -- unbounded in a loop, and
// invisible to any test that only checks output. The counts below are what a leak would grow.
// expect: 14 60 132
import Ashes.IO
import Ashes.Text
let build n = Ashes.Text.fromInt(n) + "-tail"

// One reference-counted operand: the call result. The literal owns nothing.
let recursive oneOperand n total =
    if n <= 0
    then total
    else oneOperand(n - 1)(total + Ashes.Text.byteLength(build(n) + "!"))

// Two, and bound rather than consumed on the spot, so a scope owns the result.
let recursive twoOperands n total =
    if n <= 0
    then total
    else
        let text = build(n) + build(n)
        in twoOperands(n - 1)(total + Ashes.Text.byteLength(text))

// The operand is a live binding the enclosing scope still owns and reads afterwards -- the shape the
// newly-produced requirement exists to protect. It is carried here as a worked example rather than a
// trip-wire: releasing a borrowed operand does not corrupt this program, because a freed string's
// length word survives at its own offset while the free-list link is written below it, so a read
// that only measures length cannot see the difference.
let recursive borrowedOperand n total =
    if n <= 0
    then total
    else
        let held = build(n)
        in
            let joined = held + "!"
            in borrowedOperand(n - 1)(total + Ashes.Text.byteLength(joined) + Ashes.Text.byteLength(held))

Ashes.IO.print(Ashes.Text.fromInt(oneOperand(2)(0)) + " " + Ashes.Text.fromInt(twoOperands(5)(0)) + " " + Ashes.Text.fromInt(borrowedOperand(10)(0)))
