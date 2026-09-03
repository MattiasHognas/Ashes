// Unit tests for self-hosted heap-layout classification: resource containment, unresolved-type
// detection, structural copy kinds, runtime outer-cell reuse eligibility with its rejection
// flags, and per-child drop-kind and copy-kind description for list, tuple, and named-ADT shapes.

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

let pointDecl unit =
    TypeDecl(name = "Point", typeParameters = [], constructors = [
        TypeConstructor(name = "Point", parameters = [TypeNamed("Int"), TypeNamed("Int")], fieldNames = ["x", "y"])
    ], isRecord = true, derivingTraits = [])

let labelDecl unit =
    TypeDecl(name = "Label", typeParameters = [], constructors = [
        TypeConstructor(name = "Named", parameters = [TypeNamed("Str")], fieldNames = []),
        TypeConstructor(name = "Anonymous", parameters = [], fieldNames = [])
    ], isRecord = false, derivingTraits = [])

let callbackDecl unit =
    TypeDecl(name = "Callback", typeParameters = [], constructors = [
        TypeConstructor(name = "Call", parameters = [TypeArrow(TypeNamed("Int"))(TypeNamed("Int"))([])(None)], fieldNames = [])
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
            |> TopLevelType,
            Unit
            |> pointDecl
            |> TopLevelType,
            Unit
            |> labelDecl
            |> TopLevelType,
            Unit
            |> callbackDecl
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

let childConstructorName (child: HeapLayoutChild) =
    match child with
        | HeapLayoutChild { constructorName = constructorName } -> constructorName

let childDropKind (child: HeapLayoutChild) =
    match child with
        | HeapLayoutChild { dropKind = dropKind } -> dropKind

let childCopyKind (child: HeapLayoutChild) =
    match child with
        | HeapLayoutChild { copyKind = copyKind } -> copyKind

let capabilityRejections (facts: HeapLayoutFacts) =
    match facts with
        | HeapLayoutFacts { rejections = rejections } -> rejections

let noRejections unit = HeapLayoutRejections(resourceOrBorrowedViewContainment = false, unsupportedChildDropLayout = false, unresolvedType = false, unsupportedOuterCellReuse = false)

let outerCellOnlyRejection unit = HeapLayoutRejections(resourceOrBorrowedViewContainment = false, unsupportedChildDropLayout = false, unresolvedType = false, unsupportedOuterCellReuse = true)

let namedTypeAt (constructorName: Str) (concreteArguments: List(SemanticType)) (environment: TypeEnvironment) =
    match environment with
        | TypeEnvironment { constructors = constructors } ->
            match findConstructorResultType(constructorName)(constructors) with
                | SemNamed(symbolId, name, _genericArguments) -> SemNamed(symbolId)(name)(concreteArguments)
                | _ -> test.fail("constructor " + constructorName + " does not resolve to a named type")

let classifyNamed (constructorName: Str) (concreteArguments: List(SemanticType)) =
    (let environment = heapLayoutTestEnvironment(Unit)
    in
        classifyHeapLayout(namedTypeAt(constructorName)(concreteArguments)(environment))(environment))

let testScalarIsNeverOwned unit =
    (let facts =
        Unit
        |> heapLayoutTestEnvironment
        |> classifyHeapLayout(SemInt)
    in
        test.assertEqual(
            HeapLayoutFacts(
                containsResource = false,
                containsUnresolvedType = false,
                containsOwnedChild = false,
                structuralCopy = InlineCopy,
                arenaDeepCopySupported = true,
                ownedChildrenDroppable = true,
                runtimeOuterCellReuseSupported = false,
                runtimeCopyAdtSupported = false,
                runtimeRecordAdtSupported = false,
                runtimeOwnedChildAdtSupported = false,
                runtimeTcoOwnedChildAdtSupported = false,
                runtimeTcoListElementSupported = true,
                children = [],
                rejections = outerCellOnlyRejection(Unit)
            )
        )(facts))

let testLeafStringHasNoChildren unit =
    (let facts =
        Unit
        |> heapLayoutTestEnvironment
        |> classifyHeapLayout(SemString)
    in
        test.assertEqual(
            HeapLayoutFacts(
                containsResource = false,
                containsUnresolvedType = false,
                containsOwnedChild = false,
                structuralCopy = ShallowCopy,
                arenaDeepCopySupported = true,
                ownedChildrenDroppable = true,
                runtimeOuterCellReuseSupported = false,
                runtimeCopyAdtSupported = false,
                runtimeRecordAdtSupported = false,
                runtimeOwnedChildAdtSupported = false,
                runtimeTcoOwnedChildAdtSupported = false,
                runtimeTcoListElementSupported = true,
                children = [],
                rejections = outerCellOnlyRejection(Unit)
            )
        )(facts))

let testListOfScalarsIsDeepCopyableAndDroppable unit =
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
                structuralCopy = DeepCopy,
                arenaDeepCopySupported = true,
                ownedChildrenDroppable = true,
                runtimeOuterCellReuseSupported = false,
                runtimeCopyAdtSupported = false,
                runtimeRecordAdtSupported = false,
                runtimeOwnedChildAdtSupported = false,
                runtimeTcoOwnedChildAdtSupported = false,
                runtimeTcoListElementSupported = true,
                children = [
                    HeapLayoutChild(constructorName = None, fieldIndex = 0, childType = SemInt, dropKind = NoChildDrop, copyKind = InlineCopy),
                    HeapLayoutChild(constructorName = None, fieldIndex = 1, childType = SemList(SemInt), dropKind = DropList, copyKind = DeepCopy)
                ],
                rejections = outerCellOnlyRejection(Unit)
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
                structuralCopy = DeepCopy,
                arenaDeepCopySupported = true,
                ownedChildrenDroppable = false,
                runtimeOuterCellReuseSupported = false,
                runtimeCopyAdtSupported = false,
                runtimeRecordAdtSupported = false,
                runtimeOwnedChildAdtSupported = false,
                runtimeTcoOwnedChildAdtSupported = false,
                runtimeTcoListElementSupported = true,
                children = [
                    HeapLayoutChild(constructorName = None, fieldIndex = 0, childType = SemString, dropKind = DropString, copyKind = ShallowCopy),
                    HeapLayoutChild(constructorName = None, fieldIndex = 1, childType = SemList(SemString), dropKind = DropList, copyKind = DeepCopy)
                ],
                rejections = HeapLayoutRejections(resourceOrBorrowedViewContainment = false, unsupportedChildDropLayout = true, unresolvedType = false, unsupportedOuterCellReuse = true)
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
                structuralCopy = DeepCopy,
                arenaDeepCopySupported = true,
                ownedChildrenDroppable = true,
                runtimeOuterCellReuseSupported = false,
                runtimeCopyAdtSupported = false,
                runtimeRecordAdtSupported = false,
                runtimeOwnedChildAdtSupported = false,
                runtimeTcoOwnedChildAdtSupported = false,
                runtimeTcoListElementSupported = true,
                children = [
                    HeapLayoutChild(constructorName = None, fieldIndex = 0, childType = SemInt, dropKind = NoChildDrop, copyKind = InlineCopy),
                    HeapLayoutChild(constructorName = None, fieldIndex = 1, childType = SemString, dropKind = DropString, copyKind = ShallowCopy)
                ],
                rejections = outerCellOnlyRejection(Unit)
            )
        )(facts))

let testGenericAdtAtCopyTypeIsRuntimeCopyAdt unit =
    (let facts = classifyNamed("Some")([SemInt])
    in
        test.assertEqual(
            HeapLayoutFacts(
                containsResource = false,
                containsUnresolvedType = false,
                containsOwnedChild = false,
                structuralCopy = DeepCopy,
                arenaDeepCopySupported = true,
                ownedChildrenDroppable = true,
                runtimeOuterCellReuseSupported = true,
                runtimeCopyAdtSupported = true,
                runtimeRecordAdtSupported = false,
                runtimeOwnedChildAdtSupported = false,
                runtimeTcoOwnedChildAdtSupported = false,
                runtimeTcoListElementSupported = false,
                children = [HeapLayoutChild(constructorName = Some("Some"), fieldIndex = 0, childType = SemInt, dropKind = NoChildDrop, copyKind = InlineCopy)],
                rejections = noRejections(Unit)
            )
        )(facts))

let testGenericAdtAtOwnedTypeOwnsItsField unit =
    (let facts = classifyNamed("Some")([SemString])
    in
        test.assertEqual(
            HeapLayoutFacts(
                containsResource = false,
                containsUnresolvedType = false,
                containsOwnedChild = true,
                structuralCopy = DeepCopy,
                arenaDeepCopySupported = true,
                ownedChildrenDroppable = true,
                runtimeOuterCellReuseSupported = false,
                runtimeCopyAdtSupported = false,
                runtimeRecordAdtSupported = false,
                runtimeOwnedChildAdtSupported = false,
                runtimeTcoOwnedChildAdtSupported = false,
                runtimeTcoListElementSupported = false,
                children = [HeapLayoutChild(constructorName = Some("Some"), fieldIndex = 0, childType = SemString, dropKind = DropString, copyKind = ShallowCopy)],
                rejections = outerCellOnlyRejection(Unit)
            )
        )(facts))

let testCopyableFlatRecordIsShallowCopyAndRecordReusable unit =
    (let facts = classifyNamed("Point")([])
    in
        test.assertEqual(
            HeapLayoutFacts(
                containsResource = false,
                containsUnresolvedType = false,
                containsOwnedChild = false,
                structuralCopy = ShallowCopy,
                arenaDeepCopySupported = true,
                ownedChildrenDroppable = true,
                runtimeOuterCellReuseSupported = true,
                runtimeCopyAdtSupported = true,
                runtimeRecordAdtSupported = true,
                runtimeOwnedChildAdtSupported = false,
                runtimeTcoOwnedChildAdtSupported = false,
                runtimeTcoListElementSupported = true,
                children = [
                    HeapLayoutChild(constructorName = Some("Point"), fieldIndex = 0, childType = SemInt, dropKind = NoChildDrop, copyKind = InlineCopy),
                    HeapLayoutChild(constructorName = Some("Point"), fieldIndex = 1, childType = SemInt, dropKind = NoChildDrop, copyKind = InlineCopy)
                ],
                rejections = noRejections(Unit)
            )
        )(facts))

let testAdtWithStringFieldIsOwnedChildReusable unit =
    (let facts = classifyNamed("Named")([])
    in
        test.assertEqual(
            HeapLayoutFacts(
                containsResource = false,
                containsUnresolvedType = false,
                containsOwnedChild = true,
                structuralCopy = DeepCopy,
                arenaDeepCopySupported = true,
                ownedChildrenDroppable = true,
                runtimeOuterCellReuseSupported = true,
                runtimeCopyAdtSupported = false,
                runtimeRecordAdtSupported = false,
                runtimeOwnedChildAdtSupported = true,
                runtimeTcoOwnedChildAdtSupported = true,
                runtimeTcoListElementSupported = true,
                children = [HeapLayoutChild(constructorName = Some("Named"), fieldIndex = 0, childType = SemString, dropKind = DropString, copyKind = ShallowCopy)],
                rejections = noRejections(Unit)
            )
        )(facts))

let testResourceBearingAdtIsRejectedWithResourceFlag unit =
    (let facts = classifyNamed("Wrap")([])
    in
        test.assertEqual(
            HeapLayoutFacts(
                containsResource = true,
                containsUnresolvedType = false,
                containsOwnedChild = true,
                structuralCopy = NoStructuralCopy,
                arenaDeepCopySupported = false,
                ownedChildrenDroppable = false,
                runtimeOuterCellReuseSupported = false,
                runtimeCopyAdtSupported = false,
                runtimeRecordAdtSupported = false,
                runtimeOwnedChildAdtSupported = false,
                runtimeTcoOwnedChildAdtSupported = false,
                runtimeTcoListElementSupported = false,
                children = [HeapLayoutChild(constructorName = Some("Wrap"), fieldIndex = 0, childType = SemOpaque("Handle"), dropKind = UnsupportedChildDrop, copyKind = NoStructuralCopy)],
                rejections = HeapLayoutRejections(resourceOrBorrowedViewContainment = true, unsupportedChildDropLayout = true, unresolvedType = false, unsupportedOuterCellReuse = true)
            )
        )(facts))

let testAdtContainingFunctionHasUnsupportedChild unit =
    match classifyNamed("Call")([]) with
        | HeapLayoutFacts { containsResource = containsResource, containsUnresolvedType = containsUnresolvedType, containsOwnedChild = containsOwnedChild, structuralCopy = structuralCopy, arenaDeepCopySupported = arenaDeepCopySupported, ownedChildrenDroppable = ownedChildrenDroppable, runtimeOuterCellReuseSupported = runtimeOuterCellReuseSupported, runtimeTcoOwnedChildAdtSupported = runtimeTcoOwnedChildAdtSupported, runtimeTcoListElementSupported = runtimeTcoListElementSupported, children = children, rejections = rejections } ->
            Unit
            |> (given (_) -> test.assertEqual(false)(containsResource))
            |> (given (_) -> test.assertEqual(false)(containsUnresolvedType))
            |> (given (_) -> test.assertEqual(true)(containsOwnedChild))
            |> (given (_) -> test.assertEqual(NoStructuralCopy)(structuralCopy))
            |> (given (_) -> test.assertEqual(false)(arenaDeepCopySupported))
            |> (given (_) -> test.assertEqual(false)(ownedChildrenDroppable))
            |> (given (_) -> test.assertEqual(false)(runtimeOuterCellReuseSupported))
            |> (given (_) -> test.assertEqual(false)(runtimeTcoOwnedChildAdtSupported))
            |> (given (_) -> test.assertEqual(false)(runtimeTcoListElementSupported))
            |> (given (_) ->
                children
                |> Ashes.Collection.List.map(childConstructorName)
                |> test.assertEqual([Some("Call")]))
            |> (given (_) ->
                children
                |> Ashes.Collection.List.map(childDropKind)
                |> test.assertEqual([UnsupportedChildDrop]))
            |> (given (_) ->
                children
                |> Ashes.Collection.List.map(childCopyKind)
                |> test.assertEqual([NoStructuralCopy]))
            |> (given (_) -> test.assertEqual(HeapLayoutRejections(resourceOrBorrowedViewContainment = false, unsupportedChildDropLayout = true, unresolvedType = false, unsupportedOuterCellReuse = true))(rejections))

let testRecursiveAdtIsRecursiveCopyReusable unit =
    (let facts = classifyNamed("Cons")([])
    in
        match facts with
            | HeapLayoutFacts { containsResource = containsResource, containsUnresolvedType = containsUnresolvedType, containsOwnedChild = containsOwnedChild, structuralCopy = structuralCopy, arenaDeepCopySupported = arenaDeepCopySupported, ownedChildrenDroppable = ownedChildrenDroppable, runtimeOuterCellReuseSupported = runtimeOuterCellReuseSupported, runtimeCopyAdtSupported = runtimeCopyAdtSupported, runtimeRecordAdtSupported = runtimeRecordAdtSupported, runtimeOwnedChildAdtSupported = runtimeOwnedChildAdtSupported, runtimeTcoOwnedChildAdtSupported = runtimeTcoOwnedChildAdtSupported, runtimeTcoListElementSupported = runtimeTcoListElementSupported, children = children, rejections = rejections } ->
                Unit
                |> (given (_) -> test.assertEqual(false)(containsResource))
                |> (given (_) -> test.assertEqual(false)(containsUnresolvedType))
                |> (given (_) -> test.assertEqual(true)(containsOwnedChild))
                |> (given (_) -> test.assertEqual(NoStructuralCopy)(structuralCopy))
                |> (given (_) -> test.assertEqual(false)(arenaDeepCopySupported))
                |> (given (_) -> test.assertEqual(true)(ownedChildrenDroppable))
                |> (given (_) -> test.assertEqual(true)(runtimeOuterCellReuseSupported))
                |> (given (_) -> test.assertEqual(false)(runtimeCopyAdtSupported))
                |> (given (_) -> test.assertEqual(false)(runtimeRecordAdtSupported))
                |> (given (_) -> test.assertEqual(false)(runtimeOwnedChildAdtSupported))
                |> (given (_) -> test.assertEqual(false)(runtimeTcoOwnedChildAdtSupported))
                |> (given (_) -> test.assertEqual(false)(runtimeTcoListElementSupported))
                |> (given (_) ->
                    children
                    |> Ashes.Collection.List.length
                    |> test.assertEqual(2))
                |> (given (_) ->
                    test.assertEqual(noRejections(Unit))(rejections)))

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
    []
    |> classifyNamed("Wrap")
    |> capabilityContainsResource
    |> test.assertEqual(true)

let testUnresolvedTypeVariableIsDetected unit =
    (let facts =
        Unit
        |> heapLayoutTestEnvironment
        |> classifyHeapLayout(SemVariable(999))
    in
        facts
        |> capabilityContainsUnresolvedType
        |> test.assertEqual(true))

let testUnresolvedTypeVariableIsRejectedWithUnresolvedFlag unit =
    (let facts =
        Unit
        |> heapLayoutTestEnvironment
        |> classifyHeapLayout(SemVariable(999))
    in
        facts
        |> capabilityRejections
        |> test.assertEqual(HeapLayoutRejections(resourceOrBorrowedViewContainment = false, unsupportedChildDropLayout = true, unresolvedType = true, unsupportedOuterCellReuse = true)))

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
    |> testListOfScalarsIsDeepCopyableAndDroppable
    |> testListOfStringOwnsBothChildren
    |> testTupleDropsPerElement
    |> testGenericAdtAtCopyTypeIsRuntimeCopyAdt
    |> testGenericAdtAtOwnedTypeOwnsItsField
    |> testCopyableFlatRecordIsShallowCopyAndRecordReusable
    |> testAdtWithStringFieldIsOwnedChildReusable
    |> testResourceBearingAdtIsRejectedWithResourceFlag
    |> testAdtContainingFunctionHasUnsupportedChild
    |> testRecursiveAdtIsRecursiveCopyReusable
    |> testDirectResourceTypeIsResourceBearing
    |> testOpaqueTypeWithoutDestructorIsNotResourceBearing
    |> testResourceNestedInTupleIsDetected
    |> testResourceNestedInAdtFieldIsDetected
    |> testUnresolvedTypeVariableIsDetected
    |> testUnresolvedTypeVariableIsRejectedWithUnresolvedFlag
    |> testUnresolvedTypeParameterInsideListIsDetected
    |> reportSuccess
