// expect: 144
// A loop rewrites calls through a table of known labels: a label matched out of the lookup's owned
// result goes into a constructor written as the head of the cell the loop conses onto its
// accumulator. The back edge copies that cell onto the reference-counted heap with references of
// its own, so the constructor borrows the label and the retain it took is released after the copy.
// The labels are read again after the loop, so a released one would show.
type Kind =
    | Call(Str)
    | Known(Str)
    | Other(Int)

let recursive lookupLabel (key: Str) (entries: List((Str, Str))) =
    match entries with
        | [] -> None
        | (k, v) :: tail ->
            if k == key
            then Some(v)
            else lookupLabel(key)(tail)

let recursive labelBytes (kinds: List(Kind)) (total: Int) =
    match kinds with
        | [] -> total
        | Known(label) :: rest -> labelBytes(rest)(total + Ashes.Text.byteLength(label))
        | _ :: rest -> labelBytes(rest)(total)

let recursive rewrite insts knownLabels acc =
    match insts with
        | [] -> labelBytes(acc)(0)
        | Call(calleeLabel) :: tail ->
            match lookupLabel(calleeLabel)(knownLabels) with
                | Some(label) -> rewrite(tail)(knownLabels)(Known(label) :: acc)
                | None -> rewrite(tail)(knownLabels)(Other(0) :: acc)
        | _ :: tail -> rewrite(tail)(knownLabels)(Other(0) :: acc)

let name (prefix: Str) (n: Int) = prefix + Ashes.Text.fromInt(n)

let recursive program (n: Int) acc =
    if n == 0
    then acc
    else
        program(n - 1)(Call(name("f")(n)) :: acc)

let recursive labels (n: Int) acc =
    if n == 0
    then acc
    else labels(n - 1)((name("f")(n), name("lambda_")(n)) :: acc)

let recursive loop (n: Int) (acc: Int) =
    if n == 0
    then acc
    else
        loop(n - 1)(acc + rewrite(program(6)([]))(labels(6)([]))([]))

0
|> loop(3)
|> Ashes.Text.fromInt
|> Ashes.IO.print
