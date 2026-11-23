// A tail-modulo-cons loop whose skipping branch hands an accumulator on unchanged while its keeping
// branch grows it: the accumulator is no loop invariant, so the skipping branch's back edge keeps
// the cells the keeping branch allocated.
// expect: p1,p2,p3
let recursive containsPath path paths =
    match paths with
        | [] -> false
        | candidate :: rest ->
            if candidate == path
            then true
            else containsPath(path)(rest)

let recursive uniquePaths paths seen =
    match paths with
        | [] -> []
        | path :: rest ->
            if containsPath(path)(seen)
            then uniquePaths(rest)(seen)
            else path :: uniquePaths(rest)(path :: seen)

let name n = "p" + Ashes.Text.fromInt(n)

let paths = [name(1), name(2), name(1), name(3), name(2)]

[]
|> uniquePaths(paths)
|> Ashes.Text.join(",")
|> Ashes.IO.print
