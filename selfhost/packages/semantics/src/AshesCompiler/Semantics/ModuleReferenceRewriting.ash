// Rewrites module syntax to the deterministic names assigned by semantic stitching.
//
// Invariants:
// - Lexical variables and type parameters always shadow stitched module bindings.
// - Top-level references resolve at their declaration visibility boundary, including recursive groups.
// - Qualified imports become direct compiler-name references while unresolved intrinsic/member syntax
//   remains intact for later semantic phases.
// - Every At wrapper and its original UTF-8 span is preserved.

import Ashes.Collection.List.append as appendList
import Ashes.Internal.deepCopy as deepCopy
import AshesCompiler.Frontend.Syntax
import AshesCompiler.Semantics.ModuleSemanticStitching
export (
    value rewriteStitchedModuleReferences,
    value rewriteStitchedProjectReferences,
    value patternNames,
    value patternListNames,
)

let recursive textExists name names =
    match names with
        | [] -> false
        | candidate :: rest ->
            if candidate == name
            then true
            else textExists(name)(rest)

let definitionCompilerName definition =
    match definition with
        | StitchedDefinition { compilerName = compilerName, id = _id, sourceName = _sourceName, qualifiedName = _qualifiedName, moduleName = _moduleName, packageId = _packageId, sourcePath = _sourcePath, kind = _kind, definitionSpan = _span, declarationOrder = _order, visibleFrom = _visibleFrom, exported = _exported } -> compilerName

let definitionKind definition =
    match definition with
        | StitchedDefinition { kind = kind, id = _id, sourceName = _sourceName, qualifiedName = _qualifiedName, compilerName = _compilerName, moduleName = _moduleName, packageId = _packageId, sourcePath = _sourcePath, definitionSpan = _span, declarationOrder = _order, visibleFrom = _visibleFrom, exported = _exported } -> kind

let appendNamePart prefix segment =
    if prefix == ""
    then deepCopy(segment)
    else prefix + "." + segment

let recursive parentAndLeafParts prefix parts =
    match parts with
        | [] -> None
        | _only :: [] -> None
        | parent :: leaf :: [] -> Some((appendNamePart(prefix)(parent), deepCopy(leaf)))
        | segment :: rest ->
            parentAndLeafParts(appendNamePart(prefix)(segment))(rest)

let parentAndLeaf name =
    "."
    |> Ashes.Text.split(name)
    |> parentAndLeafParts("")

let resolvedCompilerName fallback resolved =
    match resolved with
        | Some(definition) -> definitionCompilerName(definition)
        | None -> fallback

let resolveUnqualifiedCompilerName project moduleName boundary kind name =
    project
    |> resolveStitchedUnqualified(moduleName)(boundary)(kind)(name)
    |> resolvedCompilerName(name)

let resolveQualifiedCompilerName project moduleName qualifier kind name =
    project
    |> resolveStitchedQualified(moduleName)(qualifier)(kind)(name)
    |> resolvedCompilerName(qualifier + "." + name)

let rewriteTypeName project moduleName boundary typeParameters name =
    if textExists(name)(typeParameters)
    then deepCopy(name)
    else
        match parentAndLeaf(name) with
            | Some((qualifier, leaf)) ->
                resolveQualifiedCompilerName(
                    project,
                    moduleName,
                    qualifier,
                    StitchedType,
                    leaf
                )
            | None -> resolveUnqualifiedCompilerName(project)(moduleName)(boundary)(StitchedType)(name)

let recursive rewriteTypes project moduleName boundary typeParameters types =
    match types with
        | [] -> []
        | head :: rest ->
            rewriteType(project)(moduleName)(boundary)(typeParameters)(head) :: rewriteTypes(
                project,
                moduleName,
                boundary,
                typeParameters,
                rest
            )
and rewriteCapabilityTypes project moduleName boundary typeParameters capabilities =
    match capabilities with
        | [] -> []
        | (name, arguments) :: rest ->
            (rewriteTypeName(
                project,
                moduleName,
                boundary,
                typeParameters,
                name
            ), rewriteTypes(
                project,
                moduleName,
                boundary,
                typeParameters,
                arguments
            )) :: rewriteCapabilityTypes(project)(moduleName)(boundary)(typeParameters)(rest)
and rewriteType project moduleName boundary typeParameters typeExpression =
    match typeExpression with
        | TypeAt(span, inner) ->
            inner
            |> rewriteType(project)(moduleName)(boundary)(typeParameters)
            |> TypeAt(span)
        | TypeNamed(name) ->
            name
            |> rewriteTypeName(project)(moduleName)(boundary)(typeParameters)
            |> TypeNamed
        | TypeApplied(name, arguments) ->
            arguments
            |> rewriteTypes(project)(moduleName)(boundary)(typeParameters)
            |> TypeApplied(rewriteTypeName(project)(moduleName)(boundary)(typeParameters)(name))
        | TypeArrow(argument, result, capabilities, tail) ->
            TypeArrow(
                rewriteType(project)(moduleName)(boundary)(typeParameters)(argument),
                rewriteType(project)(moduleName)(boundary)(typeParameters)(result),
                rewriteCapabilityTypes(project)(moduleName)(boundary)(typeParameters)(capabilities),
                tail
            )
        | TypeTuple(elements) ->
            elements
            |> rewriteTypes(project)(moduleName)(boundary)(typeParameters)
            |> TypeTuple
        | TypeUnit -> TypeUnit

let rewriteOptionalType project moduleName boundary typeParameters annotation =
    match annotation with
        | None -> None
        | Some(value) ->
            value
            |> rewriteType(project)(moduleName)(boundary)(typeParameters)
            |> Some

let recursive rewriteTraitConstraints project moduleName boundary typeParameters constraints =
    match constraints with
        | [] -> []
        | TraitConstraintSyntax { traitName = traitName, typeArguments = arguments } :: rest ->
            TraitConstraintSyntax(traitName = rewriteTypeName(
                project,
                moduleName,
                boundary,
                typeParameters,
                traitName
            ), typeArguments = rewriteTypes(
                project,
                moduleName,
                boundary,
                typeParameters,
                arguments
            )) :: rewriteTraitConstraints(project)(moduleName)(boundary)(typeParameters)(rest)

let recursive rewriteNeedsRow project moduleName boundary typeParameters row =
    match row with
        | NeedsRowSyntax { capabilities = capabilities, tailVariable = tail } ->
            NeedsRowSyntax(capabilities = rewriteCapabilityRefs(
                project,
                moduleName,
                boundary,
                typeParameters,
                capabilities
            ), tailVariable = tail)
and rewriteCapabilityRefs project moduleName boundary typeParameters capabilities =
    match capabilities with
        | [] -> []
        | CapabilityRefSyntax { name = name, args = arguments } :: rest ->
            CapabilityRefSyntax(name = rewriteTypeName(
                project,
                moduleName,
                boundary,
                typeParameters,
                name
            ), args = rewriteTypes(
                project,
                moduleName,
                boundary,
                typeParameters,
                arguments
            )) :: rewriteCapabilityRefs(project)(moduleName)(boundary)(typeParameters)(rest)

let recursive patternNames pattern =
    match pattern with
        | PatternAt(_span, inner) -> patternNames(inner)
        | PatternVar(name) -> [name]
        | PatternCons(head, tail) ->
            tail
            |> patternNames
            |> appendList(patternNames(head))
        | PatternTuple(elements) -> patternListNames(elements)
        | PatternConstructor(_name, arguments) -> patternListNames(arguments)
        | PatternRecord(_name, fields) -> patternFieldNames(fields)
        | PatternAs(inner, name) -> name :: patternNames(inner)
        | PatternOr(alternatives) ->
            match alternatives with
                | [] -> []
                | first :: _rest -> patternNames(first)
        | _ -> []
and patternListNames patterns =
    match patterns with
        | [] -> []
        | head :: rest ->
            rest
            |> patternListNames
            |> appendList(patternNames(head))
and patternFieldNames fields =
    match fields with
        | [] -> []
        | (_name, pattern) :: rest ->
            rest
            |> patternFieldNames
            |> appendList(patternNames(pattern))

let recursive rewritePatterns project moduleName boundary patterns =
    match patterns with
        | [] -> []
        | head :: rest ->
            rewritePattern(project)(moduleName)(boundary)(head) :: rewritePatterns(
                project,
                moduleName,
                boundary,
                rest
            )
and rewritePatternFields project moduleName boundary fields =
    match fields with
        | [] -> []
        | (name, pattern) :: rest ->
            (name, rewritePattern(
                project,
                moduleName,
                boundary,
                pattern
            )) :: rewritePatternFields(project)(moduleName)(boundary)(rest)
and rewritePattern project moduleName boundary pattern =
    match pattern with
        | PatternAt(span, inner) ->
            inner
            |> rewritePattern(project)(moduleName)(boundary)
            |> PatternAt(span)
        | PatternCons(head, tail) ->
            tail
            |> rewritePattern(project)(moduleName)(boundary)
            |> PatternCons(rewritePattern(project)(moduleName)(boundary)(head))
        | PatternTuple(elements) ->
            elements
            |> rewritePatterns(project)(moduleName)(boundary)
            |> PatternTuple
        | PatternConstructor(name, arguments) ->
            arguments
            |> rewritePatterns(project)(moduleName)(boundary)
            |> PatternConstructor(
                resolveUnqualifiedCompilerName(project)(moduleName)(boundary)(StitchedConstructor)(name)
            )
        | PatternRecord(name, fields) ->
            fields
            |> rewritePatternFields(project)(moduleName)(boundary)
            |> PatternRecord(rewriteTypeName(project)(moduleName)(boundary)([])(name))
        | PatternAs(inner, name) ->
            PatternAs(rewritePattern(project)(moduleName)(boundary)(inner))(name)
        | PatternOr(alternatives) ->
            alternatives
            |> rewritePatterns(project)(moduleName)(boundary)
            |> PatternOr
        | _ -> pattern

let resolveExpressionVariable project moduleName boundary locals name =
    if textExists(name)(locals)
    then ExprVar(name)
    else
        name
        |> resolveUnqualifiedCompilerName(project)(moduleName)(boundary)(StitchedValue)
        |> ExprVar

let rewriteQualifiedExpression project moduleName boundary qualifier name =
    match resolveStitchedQualified(moduleName)(qualifier)(StitchedValue)(name)(project) with
        | Some(definition) ->
            definition
            |> definitionCompilerName
            |> ExprVar
        | None ->
            match parentAndLeaf(qualifier) with
                | Some((parent, leaf)) ->
                    match resolveStitchedQualified(moduleName)(parent)(StitchedType)(leaf)(project) with
                        | Some(definition) ->
                            ExprQualifiedVar(definitionCompilerName(definition))(name)
                        | None -> ExprQualifiedVar(qualifier)(name)
                | None ->
                    match resolveStitchedUnqualified(moduleName)(boundary)(StitchedType)(qualifier)(project) with
                        | Some(definition) ->
                            match definitionKind(definition) with
                                | StitchedTrait ->
                                    ExprQualifiedVar(definitionCompilerName(definition))(name)
                                | StitchedCapability ->
                                    ExprQualifiedVar(definitionCompilerName(definition))(name)
                                | _ -> ExprQualifiedVar(qualifier)(name)
                        | None -> ExprQualifiedVar(qualifier)(name)

let recursive rewriteOptionalExpression project moduleName boundary locals expression =
    match expression with
        | None -> None
        | Some(value) ->
            value
            |> rewriteExpression(project)(moduleName)(boundary)(locals)
            |> Some
and rewriteExpressions project moduleName boundary locals expressions =
    match expressions with
        | [] -> []
        | head :: rest ->
            rewriteExpression(project)(moduleName)(boundary)(locals)(head) :: rewriteExpressions(
                project,
                moduleName,
                boundary,
                locals,
                rest
            )
and rewriteExpressionFields project moduleName boundary locals fields =
    match fields with
        | [] -> []
        | (name, value) :: rest ->
            (name, rewriteExpression(
                project,
                moduleName,
                boundary,
                locals,
                value
            )) :: rewriteExpressionFields(project)(moduleName)(boundary)(locals)(rest)
and rewriteMatchCases project moduleName boundary locals cases =
    match cases with
        | [] -> []
        | (pattern, body, guard) :: rest ->
            let caseLocals =
                appendList(patternNames(pattern))(locals)
            in
                (rewritePattern(project)(moduleName)(boundary)(pattern), rewriteExpression(
                    project,
                    moduleName,
                    boundary,
                    caseLocals,
                    body
                ), rewriteOptionalExpression(
                    project,
                    moduleName,
                    boundary,
                    caseLocals,
                    guard
                )) :: rewriteMatchCases(project)(moduleName)(boundary)(locals)(rest)
and rewriteHandlerArms project moduleName boundary locals arms =
    match arms with
        | [] -> []
        | (instance, operation, patterns, body) :: rest ->
            let armLocals =
                appendList(patternListNames(patterns))(locals)
            in
                let rewrittenInstance =
                    match instance with
                        | None -> None
                        | Some(name) ->
                            name
                            |> rewriteTypeName(project)(moduleName)(boundary)([])
                            |> Some
                in
                    (rewrittenInstance, operation, rewritePatterns(
                        project,
                        moduleName,
                        boundary,
                        patterns
                    ), rewriteExpression(
                        project,
                        moduleName,
                        boundary,
                        armLocals,
                        body
                    )) :: rewriteHandlerArms(project)(moduleName)(boundary)(locals)(rest)
and rewriteExpression project moduleName boundary locals expression =
    match expression with
        | ExprAt(span, inner) ->
            inner
            |> rewriteExpression(project)(moduleName)(boundary)(locals)
            |> ExprAt(span)
        | ExprVar(name) -> resolveExpressionVariable(project)(moduleName)(boundary)(locals)(name)
        | ExprQualifiedVar(qualifier, name) ->
            rewriteQualifiedExpression(
                project,
                moduleName,
                boundary,
                qualifier,
                name
            )
        | ExprAdd(left, right) ->
            right
            |> rewriteExpression(project)(moduleName)(boundary)(locals)
            |> ExprAdd(rewriteExpression(project)(moduleName)(boundary)(locals)(left))
        | ExprSubtract(left, right) ->
            right
            |> rewriteExpression(project)(moduleName)(boundary)(locals)
            |> ExprSubtract(rewriteExpression(project)(moduleName)(boundary)(locals)(left))
        | ExprMultiply(left, right) ->
            right
            |> rewriteExpression(project)(moduleName)(boundary)(locals)
            |> ExprMultiply(rewriteExpression(project)(moduleName)(boundary)(locals)(left))
        | ExprDivide(left, right) ->
            right
            |> rewriteExpression(project)(moduleName)(boundary)(locals)
            |> ExprDivide(rewriteExpression(project)(moduleName)(boundary)(locals)(left))
        | ExprModulo(left, right) ->
            right
            |> rewriteExpression(project)(moduleName)(boundary)(locals)
            |> ExprModulo(rewriteExpression(project)(moduleName)(boundary)(locals)(left))
        | ExprBitwiseAnd(left, right) ->
            right
            |> rewriteExpression(project)(moduleName)(boundary)(locals)
            |> ExprBitwiseAnd(rewriteExpression(project)(moduleName)(boundary)(locals)(left))
        | ExprBitwiseOr(left, right) ->
            right
            |> rewriteExpression(project)(moduleName)(boundary)(locals)
            |> ExprBitwiseOr(rewriteExpression(project)(moduleName)(boundary)(locals)(left))
        | ExprBitwiseXor(left, right) ->
            right
            |> rewriteExpression(project)(moduleName)(boundary)(locals)
            |> ExprBitwiseXor(rewriteExpression(project)(moduleName)(boundary)(locals)(left))
        | ExprShiftLeft(left, right) ->
            right
            |> rewriteExpression(project)(moduleName)(boundary)(locals)
            |> ExprShiftLeft(rewriteExpression(project)(moduleName)(boundary)(locals)(left))
        | ExprShiftRight(left, right) ->
            right
            |> rewriteExpression(project)(moduleName)(boundary)(locals)
            |> ExprShiftRight(rewriteExpression(project)(moduleName)(boundary)(locals)(left))
        | ExprBitwiseNot(operand) ->
            operand
            |> rewriteExpression(project)(moduleName)(boundary)(locals)
            |> ExprBitwiseNot
        | ExprLogicalNot(operand) ->
            operand
            |> rewriteExpression(project)(moduleName)(boundary)(locals)
            |> ExprLogicalNot
        | ExprGreaterThan(left, right) ->
            right
            |> rewriteExpression(project)(moduleName)(boundary)(locals)
            |> ExprGreaterThan(rewriteExpression(project)(moduleName)(boundary)(locals)(left))
        | ExprLessThan(left, right) ->
            right
            |> rewriteExpression(project)(moduleName)(boundary)(locals)
            |> ExprLessThan(rewriteExpression(project)(moduleName)(boundary)(locals)(left))
        | ExprGreaterOrEqual(left, right) ->
            right
            |> rewriteExpression(project)(moduleName)(boundary)(locals)
            |> ExprGreaterOrEqual(rewriteExpression(project)(moduleName)(boundary)(locals)(left))
        | ExprLessOrEqual(left, right) ->
            right
            |> rewriteExpression(project)(moduleName)(boundary)(locals)
            |> ExprLessOrEqual(rewriteExpression(project)(moduleName)(boundary)(locals)(left))
        | ExprEqual(left, right) ->
            right
            |> rewriteExpression(project)(moduleName)(boundary)(locals)
            |> ExprEqual(rewriteExpression(project)(moduleName)(boundary)(locals)(left))
        | ExprNotEqual(left, right) ->
            right
            |> rewriteExpression(project)(moduleName)(boundary)(locals)
            |> ExprNotEqual(rewriteExpression(project)(moduleName)(boundary)(locals)(left))
        | ExprResultPipe(left, right) ->
            right
            |> rewriteExpression(project)(moduleName)(boundary)(locals)
            |> ExprResultPipe(rewriteExpression(project)(moduleName)(boundary)(locals)(left))
        | ExprResultMapErrorPipe(left, right) ->
            right
            |> rewriteExpression(project)(moduleName)(boundary)(locals)
            |> ExprResultMapErrorPipe(rewriteExpression(project)(moduleName)(boundary)(locals)(left))
        | ExprLet(name, value, body, parameters, annotation, requirements) ->
            requirements
            |> rewriteTraitConstraints(project)(moduleName)(boundary)([])
            |> ExprLet(
                name,
                rewriteExpression(project)(moduleName)(boundary)(appendList(parameters)(locals))(value),
                rewriteExpression(project)(moduleName)(boundary)(name :: locals)(body),
                parameters,
                rewriteOptionalType(project)(moduleName)(boundary)([])(annotation)
            )
        | ExprLetResult(name, value, body) ->
            body
            |> rewriteExpression(project)(moduleName)(boundary)(name :: locals)
            |> ExprLetResult(name)(rewriteExpression(project)(moduleName)(boundary)(locals)(value))
        | ExprLetRecursive(name, value, body, parameters, annotation, requirements) ->
            requirements
            |> rewriteTraitConstraints(project)(moduleName)(boundary)([])
            |> ExprLetRecursive(
                name,
                rewriteExpression(project)(moduleName)(boundary)(name :: appendList(parameters)(locals))(value),
                rewriteExpression(project)(moduleName)(boundary)(name :: locals)(body),
                parameters,
                rewriteOptionalType(project)(moduleName)(boundary)([])(annotation)
            )
        | ExprIf(condition, thenBranch, elseBranch) ->
            elseBranch
            |> rewriteExpression(project)(moduleName)(boundary)(locals)
            |> ExprIf(
                rewriteExpression(project)(moduleName)(boundary)(locals)(condition),
                rewriteExpression(project)(moduleName)(boundary)(locals)(thenBranch)
            )
        | ExprLambda(name, body, annotation) ->
            annotation
            |> rewriteOptionalType(project)(moduleName)(boundary)([])
            |> ExprLambda(name)(rewriteExpression(project)(moduleName)(boundary)(name :: locals)(body))
        | ExprCall(function, argument, whitespace, layout) ->
            ExprCall(
                rewriteExpression(project)(moduleName)(boundary)(locals)(function),
                rewriteExpression(project)(moduleName)(boundary)(locals)(argument),
                whitespace,
                layout
            )
        | ExprTuple(elements) ->
            elements
            |> rewriteExpressions(project)(moduleName)(boundary)(locals)
            |> ExprTuple
        | ExprList(elements, isMultiline) ->
            elements
            |> rewriteExpressions(project)(moduleName)(boundary)(locals)
            |> (given (rewrittenElements) -> ExprList(rewrittenElements)(isMultiline))
        | ExprCons(head, tail) ->
            tail
            |> rewriteExpression(project)(moduleName)(boundary)(locals)
            |> ExprCons(rewriteExpression(project)(moduleName)(boundary)(locals)(head))
        | ExprMatch(value, cases, offset) ->
            ExprMatch(
                rewriteExpression(project)(moduleName)(boundary)(locals)(value),
                rewriteMatchCases(project)(moduleName)(boundary)(locals)(cases),
                offset
            )
        | ExprAwait(task) ->
            task
            |> rewriteExpression(project)(moduleName)(boundary)(locals)
            |> ExprAwait
        | ExprRecord(name, fields, isMultiline) ->
            fields
            |> rewriteExpressionFields(project)(moduleName)(boundary)(locals)
            |> (given (rewrittenFields) ->
                ExprRecord(
                    rewriteTypeName(project)(moduleName)(boundary)([])(name)
                )(rewrittenFields)(isMultiline))
        | ExprRecordUpdate(value, fields) ->
            fields
            |> rewriteExpressionFields(project)(moduleName)(boundary)(locals)
            |> ExprRecordUpdate(rewriteExpression(project)(moduleName)(boundary)(locals)(value))
        | ExprPerform(operation) ->
            operation
            |> rewriteExpression(project)(moduleName)(boundary)(locals)
            |> ExprPerform
        | ExprHandle(body, arms) ->
            arms
            |> rewriteHandlerArms(project)(moduleName)(boundary)(locals)
            |> ExprHandle(rewriteExpression(project)(moduleName)(boundary)(locals)(body))
        | _ -> expression

let recursive typeParameterNames parameters =
    match parameters with
        | [] -> []
        | TypeParameter { name = name } :: rest -> name :: typeParameterNames(rest)

let declarationCompilerName project moduleName boundary kind name =
    resolveUnqualifiedCompilerName(
        project,
        moduleName,
        boundary + 1,
        kind,
        name
    )

let rewriteTypeConstructor project moduleName boundary typeParameters constructor =
    match constructor with
        | TypeConstructor { name = name, parameters = fields, fieldNames = fieldNames } ->
            TypeConstructor(name = declarationCompilerName(
                project,
                moduleName,
                boundary,
                StitchedConstructor,
                name
            ), parameters = rewriteTypes(
                project,
                moduleName,
                boundary,
                typeParameters,
                fields
            ), fieldNames = fieldNames)

let recursive rewriteTypeConstructors project moduleName boundary typeParameters constructors =
    match constructors with
        | [] -> []
        | constructor :: rest ->
            rewriteTypeConstructor(
                project,
                moduleName,
                boundary,
                typeParameters,
                constructor
            ) :: rewriteTypeConstructors(project)(moduleName)(boundary)(typeParameters)(rest)

let recursive rewriteDerivingTraits project moduleName boundary traits =
    match traits with
        | [] -> []
        | name :: rest ->
            rewriteTypeName(
                project,
                moduleName,
                boundary,
                [],
                name
            ) :: rewriteDerivingTraits(project)(moduleName)(boundary)(rest)

let rewriteTypeDeclaration project moduleName boundary declaration =
    match declaration with
        | TypeDecl { name = name, typeParameters = parameters, constructors = constructors, isRecord = isRecord, derivingTraits = derivingTraits } ->
            let parameterNames = typeParameterNames(parameters)
            in
                TypeDecl(name = declarationCompilerName(
                    project,
                    moduleName,
                    boundary,
                    StitchedType,
                    name
                ), typeParameters = parameters, constructors = rewriteTypeConstructors(
                    project,
                    moduleName,
                    boundary,
                    parameterNames,
                    constructors
                ), isRecord = isRecord, derivingTraits = rewriteDerivingTraits(
                    project,
                    moduleName,
                    boundary,
                    derivingTraits
                ))

let rewriteTypeAliasDeclaration project moduleName boundary declaration =
    match declaration with
        | TypeAliasDecl { name = name, typeParameters = parameters, target = target } ->
            TypeAliasDecl(name = declarationCompilerName(
                project,
                moduleName,
                boundary,
                StitchedType,
                name
            ), typeParameters = parameters, target = rewriteType(
                project,
                moduleName,
                boundary,
                typeParameterNames(parameters),
                target
            ))

let rewriteZeroCostDeclaration project moduleName boundary declaration =
    match declaration with
        | ZeroCostTypeDecl { name = name, typeParameters = parameters, constructor = constructor, derivingTraits = derivingTraits } ->
            let parameterNames = typeParameterNames(parameters)
            in
                ZeroCostTypeDecl(name = declarationCompilerName(
                    project,
                    moduleName,
                    boundary,
                    StitchedType,
                    name
                ), typeParameters = parameters, constructor = rewriteTypeConstructor(
                    project,
                    moduleName,
                    boundary,
                    parameterNames,
                    constructor
                ), derivingTraits = rewriteDerivingTraits(project)(moduleName)(boundary)(derivingTraits))

let recursive rewriteParsedTypes project moduleName boundary types =
    match types with
        | [] -> []
        | head :: rest ->
            rewriteParsedType(project)(moduleName)(boundary)(head) :: rewriteParsedTypes(
                project,
                moduleName,
                boundary,
                rest
            )
and rewriteParsedType project moduleName boundary parsed =
    match parsed with
        | ParsedNamed(name) ->
            name
            |> rewriteTypeName(project)(moduleName)(boundary)([])
            |> ParsedNamed
        | ParsedPointer(inner) ->
            inner
            |> rewriteParsedType(project)(moduleName)(boundary)
            |> ParsedPointer
        | ParsedBuffer(inner) ->
            inner
            |> rewriteParsedType(project)(moduleName)(boundary)
            |> ParsedBuffer
        | ParsedOut(inner) ->
            inner
            |> rewriteParsedType(project)(moduleName)(boundary)
            |> ParsedOut
        | ParsedNativeString(nullable, ownership, encoding) -> ParsedNativeString(nullable)(ownership)(encoding)

let rewriteOptionalNeeds project moduleName boundary row =
    match row with
        | None -> None
        | Some(row) ->
            row
            |> rewriteNeedsRow(project)(moduleName)(boundary)([])
            |> Some

let rewriteExternalDeclaration project moduleName boundary declaration =
    match declaration with
        | ExternalOpaqueType(name, resource) ->
            ExternalOpaqueType(declarationCompilerName(project)(moduleName)(boundary)(StitchedType)(name))(resource)
        | ExternalFunction(name, parameters, result, symbol, ownership, row) ->
            row
            |> rewriteOptionalNeeds(project)(moduleName)(boundary)
            |> ExternalFunction(
                declarationCompilerName(project)(moduleName)(boundary)(StitchedExternal)(name),
                rewriteParsedTypes(project)(moduleName)(boundary)(parameters),
                rewriteParsedType(project)(moduleName)(boundary)(result),
                symbol,
                ownership
            )

let rewriteCapabilityOperation project moduleName boundary typeParameters operation =
    match operation with
        | CapabilityOperation { name = name, signature = signature } ->
            CapabilityOperation(name = name, signature = rewriteOptionalType(
                project,
                moduleName,
                boundary,
                typeParameters,
                signature
            ))

let recursive rewriteCapabilityOperations project moduleName boundary typeParameters operations =
    match operations with
        | [] -> []
        | operation :: rest ->
            rewriteCapabilityOperation(
                project,
                moduleName,
                boundary,
                typeParameters,
                operation
            ) :: rewriteCapabilityOperations(project)(moduleName)(boundary)(typeParameters)(rest)

let rewriteCapabilityDeclaration project moduleName boundary declaration =
    match declaration with
        | CapabilityDecl { name = name, typeParameters = parameters, operations = operations } ->
            CapabilityDecl(name = declarationCompilerName(
                project,
                moduleName,
                boundary,
                StitchedCapability,
                name
            ), typeParameters = parameters, operations = rewriteCapabilityOperations(
                project,
                moduleName,
                boundary,
                typeParameterNames(parameters),
                operations
            ))

let rewriteProvideBinding project moduleName boundary binding =
    match binding with
        | ProvideBinding { operationName = name, implementation = implementation } ->
            ProvideBinding(operationName = name, implementation = rewriteExpression(
                project,
                moduleName,
                boundary,
                [],
                implementation
            ))

let recursive rewriteProvideBindings project moduleName boundary bindings =
    match bindings with
        | [] -> []
        | binding :: rest ->
            rewriteProvideBinding(
                project,
                moduleName,
                boundary,
                binding
            ) :: rewriteProvideBindings(project)(moduleName)(boundary)(rest)

let rewriteProvideDeclaration project moduleName boundary declaration =
    match declaration with
        | ProvideDecl { capabilityName = name, typeArguments = arguments, bindings = bindings } ->
            ProvideDecl(capabilityName = rewriteTypeName(
                project,
                moduleName,
                boundary,
                [],
                name
            ), typeArguments = rewriteTypes(
                project,
                moduleName,
                boundary,
                [],
                arguments
            ), bindings = rewriteProvideBindings(project)(moduleName)(boundary)(bindings))

let rewriteTraitMethod project moduleName boundary typeParameters method =
    match method with
        | TraitMethodDecl { name = name, signature = signature, defaultImplementation = defaultImplementation } ->
            TraitMethodDecl(name = name, signature = rewriteType(
                project,
                moduleName,
                boundary,
                typeParameters,
                signature
            ), defaultImplementation = rewriteOptionalExpression(
                project,
                moduleName,
                boundary,
                [],
                defaultImplementation
            ))

let recursive rewriteTraitMethods project moduleName boundary typeParameters methods =
    match methods with
        | [] -> []
        | method :: rest ->
            rewriteTraitMethod(
                project,
                moduleName,
                boundary,
                typeParameters,
                method
            ) :: rewriteTraitMethods(project)(moduleName)(boundary)(typeParameters)(rest)

let rewriteTraitDeclaration project moduleName boundary declaration =
    match declaration with
        | TraitDecl { name = name, typeParameters = parameters, supertraits = supertraits, methods = methods } ->
            let parameterNames = typeParameterNames(parameters)
            in
                TraitDecl(name = declarationCompilerName(
                    project,
                    moduleName,
                    boundary,
                    StitchedTrait,
                    name
                ), typeParameters = parameters, supertraits = rewriteTraitConstraints(
                    project,
                    moduleName,
                    boundary,
                    parameterNames,
                    supertraits
                ), methods = rewriteTraitMethods(project)(moduleName)(boundary)(parameterNames)(methods))

let rewriteImplementationBinding project moduleName boundary binding =
    match binding with
        | TraitImplementationMethodBinding { methodName = name, implementation = implementation } ->
            TraitImplementationMethodBinding(methodName = name, implementation = rewriteExpression(
                project,
                moduleName,
                boundary,
                [],
                implementation
            ))

let recursive rewriteImplementationBindings project moduleName boundary bindings =
    match bindings with
        | [] -> []
        | binding :: rest ->
            rewriteImplementationBinding(
                project,
                moduleName,
                boundary,
                binding
            ) :: rewriteImplementationBindings(project)(moduleName)(boundary)(rest)

let rewriteImplementationDeclaration project moduleName boundary declaration =
    match declaration with
        | TraitImplementationDecl { traitName = traitName, typeArguments = arguments, requirements = requirements, bindings = bindings } ->
            TraitImplementationDecl(traitName = rewriteTypeName(
                project,
                moduleName,
                boundary,
                [],
                traitName
            ), typeArguments = rewriteTypes(
                project,
                moduleName,
                boundary,
                [],
                arguments
            ), requirements = rewriteTraitConstraints(
                project,
                moduleName,
                boundary,
                [],
                requirements
            ), bindings = rewriteImplementationBindings(project)(moduleName)(boundary)(bindings))

let rewriteTopLevelBinding project moduleName boundary binding =
    match binding with
        | LetBindingSyntax { name = name, value = value, sugarParameters = parameters, typeAnnotation = annotation, requirements = requirements } ->
            let compilerName = declarationCompilerName(project)(moduleName)(boundary)(StitchedValue)(name)
            in
                LetBindingSyntax(name = compilerName, value = rewriteExpression(
                    project,
                    moduleName,
                    boundary,
                    parameters,
                    value
                ), sugarParameters = parameters, typeAnnotation = rewriteOptionalType(
                    project,
                    moduleName,
                    boundary,
                    [],
                    annotation
                ), requirements = rewriteTraitConstraints(
                    project,
                    moduleName,
                    boundary,
                    [],
                    requirements
                ))

let recursive rewriteRecursiveBindings project moduleName boundary bindings =
    match bindings with
        | [] -> []
        | binding :: rest ->
            rewriteTopLevelBinding(
                project,
                moduleName,
                boundary,
                binding
            ) :: rewriteRecursiveBindings(project)(moduleName)(boundary)(rest)

let recursive rewriteTopLevelItem project moduleName boundary item =
    match item with
        | TopLevelAt(span, inner) ->
            inner
            |> rewriteTopLevelItem(project)(moduleName)(boundary)
            |> TopLevelAt(span)
        | TopLevelExport(declaration) -> TopLevelExport(declaration)
        | TopLevelType(declaration) ->
            declaration
            |> rewriteTypeDeclaration(project)(moduleName)(boundary)
            |> TopLevelType
        | TopLevelTypeAlias(declaration) ->
            declaration
            |> rewriteTypeAliasDeclaration(project)(moduleName)(boundary)
            |> TopLevelTypeAlias
        | TopLevelZeroCostType(declaration) ->
            declaration
            |> rewriteZeroCostDeclaration(project)(moduleName)(boundary)
            |> TopLevelZeroCostType
        | TopLevelExternal(declaration) ->
            declaration
            |> rewriteExternalDeclaration(project)(moduleName)(boundary)
            |> TopLevelExternal
        | TopLevelCapability(declaration) ->
            declaration
            |> rewriteCapabilityDeclaration(project)(moduleName)(boundary)
            |> TopLevelCapability
        | TopLevelProvide(declaration) ->
            declaration
            |> rewriteProvideDeclaration(project)(moduleName)(boundary)
            |> TopLevelProvide
        | TopLevelTrait(declaration) ->
            declaration
            |> rewriteTraitDeclaration(project)(moduleName)(boundary)
            |> TopLevelTrait
        | TopLevelImplementation(declaration) ->
            declaration
            |> rewriteImplementationDeclaration(project)(moduleName)(boundary)
            |> TopLevelImplementation
        | TopLevelLet(binding, isRecursive) ->
            TopLevelLet(rewriteTopLevelBinding(project)(moduleName)(boundary)(binding))(isRecursive)
        | TopLevelRecursiveGroup(bindings) ->
            bindings
            |> rewriteRecursiveBindings(project)(moduleName)(boundary)
            |> TopLevelRecursiveGroup

let recursive rewriteTopLevelItems project moduleName boundary items =
    match items with
        | [] -> ([], boundary)
        | item :: rest ->
            match rewriteTopLevelItems(project)(moduleName)(boundary + 1)(rest) with
                | (rewrittenRest, nextBoundary) ->
                    (rewriteTopLevelItem(
                        project,
                        moduleName,
                        boundary,
                        item
                    ) :: rewrittenRest, nextBoundary)

let rewriteProgram project moduleName program =
    match program with
        | ProgramSyntax { items = items, body = body } ->
            match rewriteTopLevelItems(project)(moduleName)(0)(items) with
                | (rewrittenItems, bodyBoundary) ->
                    ProgramSyntax(items = rewrittenItems, body = rewriteOptionalExpression(
                        project,
                        moduleName,
                        bodyBoundary,
                        [],
                        body
                    ))

let rewriteStitchedModuleReferences project (unit: SemanticStitchUnit) =
    match unit with
        | SemanticStitchUnit { name = name, packageId = packageId, sourcePath = sourcePath, imports = imports, interface = moduleInterface, program = program, isEntry = isEntry } ->
            SemanticStitchUnit(name = name, packageId = packageId, sourcePath = sourcePath, imports = imports, interface = moduleInterface, program = rewriteProgram(
                project,
                name,
                program
            ), isEntry = isEntry)

let recursive rewriteStitchedProjectReferences project units =
    match units with
        | [] -> []
        | unit :: rest ->
            rewriteStitchedModuleReferences(project)(unit) :: rewriteStitchedProjectReferences(
                project,
                rest
            )
