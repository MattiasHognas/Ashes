// expect: done
// A member of a same-arity recursive group performs a capability. The group's fresh member arrows
// carry an open capability row, so the member's row grows with the perform instead of colliding
// with a closed empty row.
let recursive ping n =
    if n == 0
    then Ashes.IO.print("done")
    else pong(n - 1)
and pong n = ping(n - 1)

ping(4)
