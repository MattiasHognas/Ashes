external strlen(Str) -> Int
external type Handle
external handleOf(Str) -> Handle = "strlen"

let length = strlen("Ashes")

let h = handleOf("x")

Ashes.IO.print(length)
