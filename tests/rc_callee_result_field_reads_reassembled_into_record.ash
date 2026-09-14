// expect: tok2 tok1 tok0 | warn2 warn1 warn0 | 3 2000
type LexerResult =
    | tokens: List(Str)
    | diagnostics: List(Str)

type Program =
    | items: List(Str)
    | diagnostics: List(Str)

let recursive rev (remaining: List(Str)) (result: List(Str)) =
    match remaining with
        | [] -> result
        | head :: tail -> rev(tail)(head :: result)

let recursive lexerScan (n: Int) (tokens: List(Str)) (diagnostics: List(Str)) =
    (let next = n - 1
    in
        if next < 0
        then LexerResult(tokens = rev(tokens)([]), diagnostics = rev(diagnostics)([]))
        else
            lexerScan(next)("tok" + Ashes.Text.fromInt(next) :: tokens)(
                "warn" + Ashes.Text.fromInt(next) :: diagnostics
            ))

let parseProgram (n: Int) =
    (let lexed = lexerScan(n)([])([])
    in Program(items = lexed.tokens, diagnostics = lexed.diagnostics))

let recursive render (values: List(Str)) (acc: Str) =
    match values with
        | [] -> acc
        | value :: [] -> acc + value
        | value :: rest -> render(rest)(acc + value + " ")

let recursive churn (k: Int) (acc: List(Str)) =
    if k == 0
    then acc
    else churn(k - 1)("garbage" + Ashes.Text.fromInt(k) :: acc)

let program = parseProgram(3)

let noise = churn(2000)([])

Ashes.IO.print(
    render(program.items)("") + " | " + render(program.diagnostics)("") + " | " + Ashes.Text.fromInt(Ashes.Collection.List.length(program.diagnostics)) + " " + Ashes.Text.fromInt(Ashes.Collection.List.length(noise))
)
