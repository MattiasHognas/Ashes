import Ashes.Test as test
import AshesCompiler.Semantics.TaglessAdtLayout
import AshesCompiler.Semantics.Types
export (
    value runTaglessConstructorIndexTests,
)

let named name = SemNamed(1)(name)([])

let recursive body fields result =
    match fields with
        | [] -> result
        | field :: rest ->
            SemFunction(field)(body(rest)(result))(None)

let scheme fields result = TypeScheme(quantified = [], body = body(fields)(result), constraints = [])

let declared (name: Str) = name == "Declared"

let generic argument = SemNamed(2)("Generic")([argument])

let cycle argument = SemNamed(3)("Cycle")([argument])

let schemes =
    [
        "Scalar"
        |> named
        |> scheme([SemInt]),
        "Nullary"
        |> named
        |> scheme([]),
        "Multiple"
        |> named
        |> scheme([SemInt]),
        []
        |> SemNamed(999)("Multiple")
        |> scheme([SemString]),
        "Task"
        |> named
        |> scheme([SemInt]),
        "OpaqueResource"
        |> named
        |> scheme([SemOpaque("Declared")]),
        "NamedResource"
        |> named
        |> scheme([named("Declared")]),
        "NestedResource"
        |> named
        |> scheme([SemList(SemTuple([SemInt, named("FileHandle")]))]),
        SemVariable(7)
        |> generic
        |> scheme([SemVariable(7)]),
        "GenericScalar"
        |> named
        |> scheme([generic(SemInt)]),
        "GenericResource"
        |> named
        |> scheme(["FileHandle"
        |> named
        |> generic]),
        SemVariable(7)
        |> cycle
        |> scheme([cycle(SemList(SemInt))]),
        "ChangingCycle"
        |> named
        |> scheme([cycle(SemInt)]),
        "CycleA"
        |> named
        |> scheme([named("CycleB")]),
        "CycleB"
        |> named
        |> scheme([named("CycleA"), named("FileHandle")]),
        scheme([SemInt])(SemInt),
        scheme([SemString])(SemTuple([SemInt]))
    ]

let recursive compareSchemes all index candidates =
    match candidates with
        | [] -> Unit
        | candidate :: rest ->
            candidate
            |> isTaglessAdtConstructorPrepared(declared)(index)(false)
            |> test.assertEqual(isTaglessAdtConstructor(declared)(all)(false)(candidate))
            |> (given (_) -> isTaglessAdtConstructorPrepared(declared)(index)(true)(candidate))
            |> test.assertEqual(false)
            |> (given (_) -> compareSchemes(all)(index)(rest))

let runTaglessConstructorIndexTests unit =
    (let index = prepareTaglessConstructorIndex(schemes)
    in
        scheme([SemInt])(named("Absent")) :: schemes
        |> compareSchemes(schemes)(index)
        |> (given (_) ->
            let scalar =
                "Independent"
                |> named
                |> scheme([SemInt])
            in
                let changed =
                    "Independent"
                    |> named
                    |> scheme([SemString])
                in
                    scalar
                    |> isTaglessAdtConstructorPrepared(declared)(prepareTaglessConstructorIndex([scalar]))(false)
                    |> test.assertEqual(true)
                    |> (given (_) ->
                        isTaglessAdtConstructorPrepared(declared)(prepareTaglessConstructorIndex([scalar, changed]))(false)(scalar))
                    |> test.assertEqual(false))
        |> (given (_) -> Ashes.IO.print("tagless constructor index tests passed")))
