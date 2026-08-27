// Unit tests for self-hosted heap-layout classification: resource containment, unresolved-type
// detection, and per-child drop-kind description for list, tuple, and named-ADT shapes.

import Ashes.Test as test
import AshesCompiler.Frontend.Syntax
import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.TypeInference
import AshesCompiler.Semantics.ProgramInference
import AshesCompiler.Semantics.HeapLayoutClassification
export (
    value runHeapLayoutClassificationTests,
)

let optionDecl unit =
    TypeDecl(name = "Option", typeParameters = [TypeParameter(name = "a")], constructors = [
        TypeConstructor(name = "None", parameters = [], fieldNames = []),
        TypeConstructor(name = "Some", parameters = [TypeNamed("a")], fieldNames = [])
    ], isRecord = false, derivingTraits = [])

let wrappedDecl unit =
    TypeDecl(name = "Wrapped", typeParameters = [], constructors = [
        TypeConstructor(name = "Wrap", parameters = [TypeNamed("Handle")], fieldNames = [])
    ], isRecord = false, derivingTraits = [])

let intListDecl unit =
    TypeDecl(name = "IntList", typeParameters = [], constructors = [
        TypeConstructor(name = "Nil", parameters = [], fieldNames = []),
        TypeConstructor(name = "Cons", parameters = [TypeNamed("Int"), TypeNamed("IntList")], fieldNames = [])
    ], isRecord = false, derivingTraits = [])

let closeHandleDecl =
    ExternalFunction(
        "closeHandle",
        [
            ParsedNamed("Handle")
        ],
        ParsedNamed("void"),
        Some("close_handle@libresource.so"),
        [
            ExternalOwnershipConsume
        ],
        None
    )

let heapLayoutTestProgram unit =
    ProgramSyntax(
        items = [
            Some("closeHandle")
            |> ExternalOpaqueType("Handle")
            |> TopLevelExternal,
            TopLevelExternal(closeHandleDecl),
            None
            |> ExternalOpaqueType("View")
            |> TopLevelExternal,
            Unit
            |> optionDecl
            |> TopLevelType,
            Unit
            |> wrappedDecl
            |> TopLevelType,
            Unit
            |> intListDecl
            |> TopLevelType
        ],
        body = None
    )

let heapLayoutTestEnvironment unit =
    match Unit
    |> heapLayoutTestProgram
    |> inferProgram with
        | ProgramInferenceResult { environment = environment, error = None } -> environment
        | ProgramInferenceResult { error = Some(error) } -> test.fail("heap layout test program should infer: " + Ashes.Trait.Show.show(error))

let recursive unwrapConstructorResult (semanticType: SemanticType) =
    match semanticType with
        | SemFunction(_argument, result, _row) -> unwrapConstructorResult(result)
        | _ -> semanticType

let recursive findConstructorResultType (name: Str) (constructors: List(ConstructorInferenceDefinition)) =
    match constructors with
        | [] -> test.fail("no constructor named " + name)
        | ConstructorInferenceDefinition { name = candidateName, scheme = TypeScheme { body = body } } :: rest ->
            if candidateName == name
            then unwrapConstructorResult(body)
            else findConstructorResultType(name)(rest)

let capabilityContainsResource (facts: HeapLayoutFacts) =
    match facts with
        | HeapLayoutFacts { containsResource = containsResource } -> containsResource

let capabilityContainsUnresolvedType (facts: HeapLayoutFacts) =
    match facts with
        | HeapLayoutFacts { containsUnresolvedType = containsUnresolvedType } -> containsUnresolvedType

let namedTypeAt (constructorName: Str) (concreteArguments: List(SemanticType)) (environment: TypeEnvironment) =
    match environment with
        | TypeEnvironment { constructors = constructors } ->
            match findConstructorResultType(constructorName)(constructors) with
                | SemNamed(symbolId, name, _genericArguments) -> SemNamed(symbolId)(name)(concreteArguments)
                | _ -> test.fail("constructor " + constructorName + " does not resolve to a named type")

let testScalarIsNeverOwned unit =
    (let facts =
        Unit
        |> heapLayoutTestEnvironment
        |> classifyHeapLayout(SemInt)
    in
        test.assertEqual(
            HeapLayoutFacts(containsResource = false, containsUnresolvedType = false, containsOwnedChild = false, children = [])
        )(facts))

let testLeafStringHasNoChildren unit =
    (let facts =
        Unit
        |> heapLayoutTestEnvironment
        |> classifyHeapLayout(SemString)
    in
        test.assertEqual(
            HeapLayoutFacts(containsResource = false, containsUnresolvedType = false, containsOwnedChild = false, children = [])
        )(facts))

let testListOfIntHasNoOwnedElement unit =
    (let facts =
        Unit
        |> heapLayoutTestEnvironment
        |> classifyHeapLayout(SemList(SemInt))
    in
        test.assertEqual(
            HeapLayoutFacts(
                containsResource = false,
                containsUnresolvedType = false,
                containsOwnedChild = true,
                children = [
                    HeapLayoutChild(constructorName = None, fieldIndex = 0, childType = SemInt, dropKind = NoChildDrop),
                    HeapLayoutChild(constructorName = None, fieldIndex = 1, childType = SemList(SemInt), dropKind = DropList)
                ]
            )
        )(facts))

let testListOfStringOwnsBothChildren unit =
    (let facts =
        Unit
        |> heapLayoutTestEnvironment
        |> classifyHeapLayout(SemList(SemString))
    in
        test.assertEqual(
            HeapLayoutFacts(
                containsResource = false,
                containsUnresolvedType = false,
                containsOwnedChild = true,
                children = [
                    HeapLayoutChild(constructorName = None, fieldIndex = 0, childType = SemString, dropKind = DropString),
                    HeapLayoutChild(constructorName = None, fieldIndex = 1, childType = SemList(SemString), dropKind = DropList)
                ]
            )
        )(facts))

let testTupleDropsPerElement unit =
    (let facts =
        Unit
        |> heapLayoutTestEnvironment
        |> classifyHeapLayout(SemTuple([SemInt, SemString]))
    in
        test.assertEqual(
            HeapLayoutFacts(
                containsResource = false,
                containsUnresolvedType = false,
                containsOwnedChild = true,
                children = [
                    HeapLayoutChild(constructorName = None, fieldIndex = 0, childType = SemInt, dropKind = NoChildDrop),
                    HeapLayoutChild(constructorName = None, fieldIndex = 1, childType = SemString, dropKind = DropString)
                ]
            )
        )(facts))

let testGenericAdtAtCopyTypeHasNoOwnedChild unit =
    (let environment = heapLayoutTestEnvironment(Unit)
    in
        let facts =
            classifyHeapLayout(namedTypeAt("Some")([SemInt])(environment))(environment)
        in
            test.assertEqual(
                HeapLayoutFacts(
                    containsResource = false,
                    containsUnresolvedType = false,
                    containsOwnedChild = false,
                    children = [HeapLayoutChild(constructorName = Some("Some"), fieldIndex = 0, childType = SemInt, dropKind = NoChildDrop)]
                )
            )(facts))

let testGenericAdtAtOwnedTypeOwnsItsField unit =
    (let environment = heapLayoutTestEnvironment(Unit)
    in
        let facts =
            classifyHeapLayout(namedTypeAt("Some")([SemString])(environment))(environment)
        in
            test.assertEqual(
                HeapLayoutFacts(
                    containsResource = false,
                    containsUnresolvedType = false,
                    containsOwnedChild = true,
                    children = [HeapLayoutChild(constructorName = Some("Some"), fieldIndex = 0, childType = SemString, dropKind = DropString)]
                )
            )(facts))

let testRecursiveAdtCycleDoesNotDiverge unit =
    (let environment = heapLayoutTestEnvironment(Unit)
    in
        let facts =
            classifyHeapLayout(namedTypeAt("Cons")([])(environment))(environment)
        in
            match facts with
                | HeapLayoutFacts { containsResource = containsResource, containsUnresolvedType = containsUnresolvedType, containsOwnedChild = containsOwnedChild, children = children } ->
                    let _ = test.assertEqual(false)(containsResource)
                    in
                        let _ = test.assertEqual(false)(containsUnresolvedType)
                        in
                            let _ = test.assertEqual(true)(containsOwnedChild)
                            in
                                children
                                |> Ashes.Collection.List.length
                                |> test.assertEqual(2))

let testDirectResourceTypeIsResourceBearing unit =
    (let facts =
        Unit
        |> heapLayoutTestEnvironment
        |> classifyHeapLayout(SemOpaque("Handle"))
    in
        facts
        |> capabilityContainsResource
        |> test.assertEqual(true))

let testOpaqueTypeWithoutDestructorIsNotResourceBearing unit =
    (let facts =
        Unit
        |> heapLayoutTestEnvironment
        |> classifyHeapLayout(SemOpaque("View"))
    in
        facts
        |> capabilityContainsResource
        |> test.assertEqual(false))

let testResourceNestedInTupleIsDetected unit =
    (let facts =
        Unit
        |> heapLayoutTestEnvironment
        |> classifyHeapLayout(SemTuple([SemOpaque("Handle"), SemInt]))
    in
        facts
        |> capabilityContainsResource
        |> test.assertEqual(true))

let testResourceNestedInAdtFieldIsDetected unit =
    (let environment = heapLayoutTestEnvironment(Unit)
    in
        let facts =
            classifyHeapLayout(namedTypeAt("Wrap")([])(environment))(environment)
        in
            facts
            |> capabilityContainsResource
            |> test.assertEqual(true))

let testUnresolvedTypeVariableIsDetected unit =
    (let facts =
        Unit
        |> heapLayoutTestEnvironment
        |> classifyHeapLayout(SemVariable(999))
    in
        facts
        |> capabilityContainsUnresolvedType
        |> test.assertEqual(true))

let testUnresolvedTypeParameterInsideListIsDetected unit =
    (let facts =
        Unit
        |> heapLayoutTestEnvironment
        |> classifyHeapLayout("a"
        |> SemParameter(999)
        |> SemList)
    in
        facts
        |> capabilityContainsUnresolvedType
        |> test.assertEqual(true))

let reportSuccess unit = Ashes.IO.print("all self-hosted heap layout classification tests passed")

let runHeapLayoutClassificationTests unit =
    unit
    |> testScalarIsNeverOwned
    |> testLeafStringHasNoChildren
    |> testListOfIntHasNoOwnedElement
    |> testListOfStringOwnsBothChildren
    |> testTupleDropsPerElement
    |> testGenericAdtAtCopyTypeHasNoOwnedChild
    |> testGenericAdtAtOwnedTypeOwnsItsField
    |> testRecursiveAdtCycleDoesNotDiverge
    |> testDirectResourceTypeIsResourceBearing
    |> testOpaqueTypeWithoutDestructorIsNotResourceBearing
    |> testResourceNestedInTupleIsDetected
    |> testResourceNestedInAdtFieldIsDetected
    |> testUnresolvedTypeVariableIsDetected
    |> testUnresolvedTypeParameterInsideListIsDetected
    |> reportSuccess
