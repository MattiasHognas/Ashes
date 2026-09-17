// expect: Ashes.M1,Ashes.M2,Ashes.M0,Ashes.M1,Ashes.M2,Ashes.M0 | 9
import Ashes.IO as io
type Reference =
    | referenceModule: Str
    | referenceMember: Str

let recursive containsText (value: Str) (values: List(Str)) =
    match values with
        | [] -> false
        | candidate :: rest ->
            if candidate == value
            then true
            else containsText(value)(rest)

let needsSource (shippedNames: List(Str)) (reference: Reference) =
    match reference with
        | Reference { referenceModule = moduleName, referenceMember = memberName } ->
            if memberName == "print"
            then false
            else containsText(moduleName)(shippedNames)

// `reference.referenceModule` reads a field off the pattern binding: the cons cell keeps the string
// after `reference` itself is released, so the read has to count as a use of the binding.
let recursive sourcesNeeded (shippedNames: List(Str)) (references: List(Reference)) =
    match references with
        | [] -> []
        | reference :: rest ->
            if needsSource(shippedNames)(reference)
            then reference.referenceModule :: sourcesNeeded(shippedNames)(rest)
            else sourcesNeeded(shippedNames)(rest)

let recursive makeReferences (n: Int) (acc: List(Reference)) =
    if n == 0
    then acc
    else makeReferences(n - 1)(Reference(referenceModule = "Ashes.M" + Ashes.Text.fromInt(n - n / 3 * 3), referenceMember = "m" + Ashes.Text.fromInt(n)) :: acc)

// Records with same-sized strings, built after the first references are gone: a string released
// too early is handed out again here and shows up under its new text.
let recursive makeOthers (n: Int) (acc: List(Reference)) =
    if n == 0
    then acc
    else makeOthers(n - 1)(Reference(referenceModule = "Other.X" + Ashes.Text.fromInt(n - n / 3 * 3), referenceMember = "x" + Ashes.Text.fromInt(n)) :: acc)

let needed (shippedNames: List(Str)) (references: List(Reference)) = references |> sourcesNeeded(shippedNames)

let report (names: List(Str)) (others: List(Reference)) =
    Ashes.Text.join(",")(names) + " | " + Ashes.Text.fromInt(Ashes.Collection.List.length(others))

let run unit =
    (let names =
        []
        |> makeReferences(6)
        |> needed(["Ashes.M0", "Ashes.M1", "Ashes.M2"])
    in
        let others = makeOthers(9)([])
        in report(names)(others))

Unit
|> run
|> io.print
