// Ashes.Regex — regular expressions backed by PCRE2.
//
// A pattern is compiled once into an opaque Regex value and then matched against subject strings.
// Pattern and subject are treated as UTF-8 with Unicode property support (\d, \w, \p{...}). Offsets
// returned by find/findAll are byte offsets into the subject.
//
// The low-level PCRE2 primitives — compileRaw, compileError, findFrom, capturesFrom, substituteAll —
// are native members of this module; the ergonomic API below is written on top of them.

type Regex =
    | Regex(Int)

let compile pattern =
    (let code = Ashes.Regex.compileRaw(pattern)
    in
        if code == 0
        then Error(Ashes.Regex.compileError(pattern))
        else Ok(Regex(code)))

let isMatch regex text =
    match regex with
        | Regex(code) ->
            match Ashes.Regex.findFrom(code)(text)(0) with
                | Some(_span) -> true
                | None -> false

let find regex text =
    match regex with
        | Regex(code) -> Ashes.Regex.findFrom(code)(text)(0)

let captures regex text =
    match regex with
        | Regex(code) -> Ashes.Regex.capturesFrom(code)(text)(0)

let findAll regex text =
    match regex with
        | Regex(code) ->
            let recursive go start =
                match Ashes.Regex.findFrom(code)(text)(start) with
                    | None -> []
                    | Some((s, e)) ->
                        (s, e) :: go(if e > s
                        then e
                        else e + 1)
            in go(0)

let replace regex text replacement =
    match regex with
        | Regex(code) -> Ashes.Regex.substituteAll(code)(text)(replacement)
