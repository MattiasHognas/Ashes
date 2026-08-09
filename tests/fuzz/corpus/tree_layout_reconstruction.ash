type CorpusTree(a) =
    | CorpusEmpty
    | CorpusLeaf(a)
    | CorpusBranch(CorpusTree(a), CorpusTree(a))

let rebuild tree =
    match tree with
        | CorpusEmpty -> CorpusEmpty
        | CorpusLeaf(value) -> CorpusBranch(CorpusLeaf(value))(CorpusEmpty)
        | CorpusBranch(left, right) -> CorpusBranch(left)(right)

rebuild(CorpusLeaf("payload"))
