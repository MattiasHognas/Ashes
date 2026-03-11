// expect-compile-error: Unknown member 'args' in module Ashes.
match Ashes.args with
    | [] -> Ashes.IO.print("[]")
    | _ -> Ashes.IO.print("bad")
