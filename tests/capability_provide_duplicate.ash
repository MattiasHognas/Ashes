// expect-compile-error: ASH026
capability Clock =
    | now : Unit -> Int

provide Clock =
    | now =
        given (_) -> 1

provide Clock =
    | now =
        given (_) -> 2

Ashes.IO.print(0)
