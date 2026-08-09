// expect: false|true|true|true
let render value =
    if value
    then "true"
    else "false"

let result = render(!true) + "|" + render(!false) + "|" + render(!!true) + "|" + render(true != false)

Ashes.IO.print(result)
