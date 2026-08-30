// expect: 20 3020 93 /lib/Ashes
import Ashes.Text
let recursive joined root names =
    match names with
        | [] -> []
        | name :: rest ->
            if Ashes.Text.length(name) == 0
            then joined(root)(rest)
            else
                let path = root + "/" + name
                in path + ":" + Ashes.Text.fromInt(Ashes.Text.length(path)) :: joined(root)(rest)

let recursive count xs =
    match xs with
        | [] -> 0
        | _ :: rest -> 1 + count(rest)

let recursive numbered n acc =
    if n == 0
    then acc
    else numbered(n - 1)("file" + Ashes.Text.fromInt(n) + ".ash" :: "" :: acc)

let recursive churn n acc =
    if n == 0
    then acc
    else churn(n - 1)(Ashes.Text.substring("/home/user/source/project/.worktrees/generic-reverse-audit/lib/Ashes/Collection/List/Sorted")(0)(88) + Ashes.Text.fromInt(1000 + n) :: acc)

let root = Ashes.Text.substring("/home/user/source/project/.worktrees/generic-reverse-audit/lib/Ashes/Collection/List/Sorted/x")(0)(95)

let paths = joined(root)(numbered(20)([]))

let noise = churn(3000)([])

Ashes.IO.print(Ashes.Text.fromInt(count(paths)) + " " + Ashes.Text.fromInt(count(noise) + count(paths)) + " " + Ashes.Text.fromInt(Ashes.Text.length(root)) + " " + Ashes.Text.substring(root)(58)(10))
