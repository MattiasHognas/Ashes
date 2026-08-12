// Ashes.Text.Regex — regular expressions backed by PCRE2.
//
// A pattern is compiled once into an opaque Regex value and then matched against subject strings.
// Pattern and subject are treated as UTF-8 with Unicode property support (\d, \w, \p{...}). Offsets
// returned by find/findAll are byte offsets into the subject.
//
// The low-level PCRE2 primitives live in the compiler-reserved Ashes.Internal.Regex module; the
// ergonomic API below is the complete public surface.

export (
    value compile,
    value isMatch,
    value find,
    value captures,
    value findAll,
    value replace,
    type Regex,
)

type Regex =
    | CompiledRegex(Int)

let compile pattern =
    (let code = Ashes.Internal.Regex.compileRaw(pattern)
    in
        if code == 0
        then Error(Ashes.Internal.Regex.compileError(pattern))
        else Ok(CompiledRegex(code)))

let isMatch regex text =
    match regex with
        | CompiledRegex(code) ->
            match Ashes.Internal.Regex.findFrom(code)(text)(0) with
                | Some(_span) -> true
                | None -> false

let find regex text =
    match regex with
        | CompiledRegex(code) -> Ashes.Internal.Regex.findFrom(code)(text)(0)

let captures regex text =
    match regex with
        | CompiledRegex(code) -> Ashes.Internal.Regex.capturesFrom(code)(text)(0)

let findAll regex text =
    match regex with
        | CompiledRegex(code) ->
            let recursive go start =
                match Ashes.Internal.Regex.findFrom(code)(text)(start) with
                    | None -> []
                    | Some((s, e)) ->
                        (s, e) :: go(if e > s
                        then e
                        else e + 1)
            in go(0)

let replace regex text replacement =
    match regex with
        | CompiledRegex(code) -> Ashes.Internal.Regex.substituteAll(code)(text)(replacement)
