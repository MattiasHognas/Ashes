// expect: a!,b!,c!:6:a,b,c
import Ashes.Text
let recursive length values total =
    match values with
        | [] -> total
        | _ :: tail -> length(tail)(total + 1)

let recursive decorate (values: List(Str)) =
    match values with
        | [] -> []
        | head :: tail ->
            let text = head + "!"
            in
                let result = decorate(tail)
                in
                    let alias = result
                    in
                        let prefixed = text :: alias
                        in
                            if length(result)(0) >= 0
                            then prefixed
                            else result

let recursive totalLength values total =
    match values with
        | [] -> total
        | head :: tail -> totalLength(tail)(total + Ashes.Text.byteLength(head))

let input = ["a", "b", "c"]

let output = decorate(input)

Ashes.IO.print(Ashes.Text.join(",")(output) + ":" + Ashes.Text.fromInt(totalLength(output)(0)) + ":" + Ashes.Text.join(",")(input))
