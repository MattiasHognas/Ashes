// expect: Some(leaf) Some(leaf) Some(leaf) None
import Ashes.IO
let recursive lookupAssociation key entries =
    match entries with
        | [] -> None
        | (k, v) :: tail ->
            if k == key
            then Some(v)
            else lookupAssociation(key)(tail)

let lookupThenConcreteStr sourceTemp singleDefs knownLabels =
    match lookupAssociation(sourceTemp)(singleDefs) with
        | Some(_) -> lookupAssociation("bui" + "ld")(knownLabels)
        | None -> None

let lookupThenStrField sourceTemp singleDefs knownLabels =
    match lookupAssociation(sourceTemp)(singleDefs) with
        | Some((_, fnLabel)) -> lookupAssociation(fnLabel)(knownLabels)
        | None -> None

let concreteIntThenPolymorphic (sourceTemp: Int) singleDefs knownLabels =
    match lookupAssociation(sourceTemp)(singleDefs) with
        | Some(fnLabel) -> lookupAssociation(fnLabel)(knownLabels)
        | None -> None

let describe result =
    match result with
        | Some(label) -> "Some(" + label + ")"
        | None -> "None"

let a = lookupThenConcreteStr(2)([(1, 5), (2, 7)])([("x", "y"), ("build", "leaf")])

let b = lookupThenStrField(2)([(1, (0, "other")), (2, (0, "bui" + "ld"))])([("x", "y"), ("build", "leaf")])

let c = concreteIntThenPolymorphic(2)([(1, "x"), (2, "bui" + "ld")])([("x", "y"), ("build", "leaf")])

let d = concreteIntThenPolymorphic(3)([(1, "x"), (2, "bui" + "ld")])([("x", "y"), ("build", "leaf")])

print(describe(a) + " " + describe(b) + " " + describe(c) + " " + describe(d))
