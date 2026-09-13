import Ashes.Test as test
import AshesCompiler.Semantics.QualifiedShippedReferences
export (
    value runQualifiedShippedReferencesTests,
)

let known = ["Ashes.IO", "Ashes.Text", "Ashes.IO.Path", "Ashes.Collection.List"]

let recursive referenceSummary (references: List(QualifiedShippedReference)) =
    match references with
        | [] -> []
        | QualifiedShippedReference { referenceModule = moduleName, referenceMember = memberName } :: rest -> moduleName + "." + memberName :: referenceSummary(rest)

// Every maximal `Ashes.A.B.c` path yields the longest known module prefix and the member right
// after it, wherever it occurs: an expression, a type annotation, a constructor in a pattern, a
// nested argument; a path with no known prefix (`Ashes.Unknown.x`) yields nothing, and a bare
// `Ashes` or a member-less module path yields the module with an empty member.
let testCollectsLongestKnownModulePrefixes unit =
    "let f (s: Ashes.IO.Path.Style) = Ashes.IO.print(Ashes.Text.join(\",\")(Ashes.IO.Path.join(s)(a)(b)))\nmatch x with\n    | Ashes.IO.Path.Unix -> Ashes.Unknown.x\n    | _ -> Ashes.Collection.List"
    |> collectQualifiedShippedReferences(known)
    |> referenceSummary
    |> test.assertEqual([
        "Ashes.IO.Path.Style",
        "Ashes.IO.print",
        "Ashes.Text.join",
        "Ashes.IO.Path.join",
        "Ashes.IO.Path.Unix",
        "Ashes.Collection.List."
    ])

// A member the builtin table lowers itself (`Ashes.Text.fromInt`, `Ashes.IO.print`) asks for no
// source; a shipped member (`Ashes.Text.join`, `Ashes.IO.Path.join`) asks for its module, once,
// in first-reference order; a module with no shipped text is never asked for.
let testModulesNeedingSourceSkipIntrinsicMembers unit =
    "Ashes.IO.print(Ashes.Text.fromInt(Ashes.IO.Path.join(u)(a)(b)) + Ashes.Text.join(s)(xs) + Ashes.Text.join(t)(ys) + Ashes.Number.Math.sqrt(x))"
    |> qualifiedShippedModulesNeedingSource(["Ashes.Text", "Ashes.IO.Path", "Ashes.Collection.List"])
    |> test.assertEqual(["Ashes.IO.Path", "Ashes.Text"])

// Without shipped texts nothing can be loaded, whatever the source references.
let testNoShippedTextsNeedNothing unit =
    "Ashes.Text.join(s)(xs)"
    |> qualifiedShippedModulesNeedingSource([])
    |> test.assertEqual([])

let runQualifiedShippedReferencesTests unit =
    Unit
    |> testCollectsLongestKnownModulePrefixes
    |> testModulesNeedingSourceSkipIntrinsicMembers
    |> testNoShippedTextsNeedNothing
    |> (given (_) -> Ashes.IO.print("all qualified shipped reference tests passed"))
