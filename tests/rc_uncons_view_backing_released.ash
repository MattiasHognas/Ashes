// expect: head: 1 head: x
// `Ashes.Text.unconsText` splits a string without copying: the head and tail it yields are views
// naming the backing string's own bytes. When the backing string is a let-bound call result -
// owned here, released here - its release must wait for the last use of a piece, not land right
// after the split. It landed there, and the concatenation below then allocated over the bytes it
// was reading, printing `head: h` twice: the first byte of its own left operand.
let idOf (text: Str) = text

let classify (text: Str) =
    (let trimmed = idOf(text)
    in
        match Ashes.Text.unconsText(trimmed) with
            | None -> "end"
            | Some((head, _rest)) -> "head: " + head)

Ashes.IO.print(classify("1") + " " + classify("xy"))
