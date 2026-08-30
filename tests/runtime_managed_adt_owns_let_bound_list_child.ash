// expect: 10,42,30, 3000
import Ashes.Text
type Step =
    | Done
    | Hit(List(Int))

let recursive setAt i v xs =
    match xs with
        | [] -> []
        | h :: t ->
            if i == 0
            then v :: t
            else h :: setAt(i - 1)(v)(t)

let recursive show xs =
    match xs with
        | [] -> ""
        | h :: t -> Ashes.Text.fromInt(h) + "," + show(t)

let recursive count xs =
    match xs with
        | [] -> 0
        | _ :: rest -> 1 + count(rest)

let recursive churn n acc =
    if n == 0
    then acc
    else churn(n - 1)(Ashes.Text.fromInt(1000 + n) :: acc)

let build _ =
    (let updated = setAt(1)(42)([10, 20, 30])
    in Hit(updated))

let step = build(0)

let noise = churn(3000)([])

let rendered =
    match step with
        | Done -> "done"
        | Hit(values) -> show(values)

Ashes.IO.print(rendered + " " + Ashes.Text.fromInt(count(noise)))
