let show flag = if flag then "T" else "F"

Ashes.IO.print(show("a" < "b") + show("a" <= "b") + show("a" > "b") + show("a" >= "b") + "|" + show("b" < "a") + show("b" <= "a") + show("b" > "a") + show("b" >= "a") + "|" + show("a" < "a") + show("a" <= "a") + show("a" > "a") + show("a" >= "a"))
