// expect: 0
match (1, 2) with
    | (value, _) | (_, value) when value == 2 -> Ashes.IO.print(value)
    | _ -> Ashes.IO.print(0)
