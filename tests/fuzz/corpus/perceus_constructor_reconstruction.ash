type CorpusBox(a) =
    | CorpusBox(a)

let rebuild box =
    match box with
        | CorpusBox(value) -> CorpusBox(value)

match rebuild(CorpusBox("stable")) with
    | CorpusBox(value) -> value
