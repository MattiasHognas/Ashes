// Constructs and tracks stable source and compiler-generated function origins.
//
// Invariants:
// - Generated labels identify emitted functions but never replace source identity.
// - Source-derived helpers retain their source origin and immediate generated parent.
// - Anonymous lambdas use deterministic discriminator strings based on spans.
// - Synthetic trait validation bindings (__trait_validate_) are excluded from source origins.

import Ashes.Collection.List.append
import Ashes.Collection.List.reverse
import AshesCompiler.Frontend.Syntax
import AshesCompiler.Frontend.Token
import AshesCompiler.Semantics.IrInstructions
import AshesCompiler.Semantics.IrOrigins
import AshesCompiler.Semantics.SourceContext
export (
    value createProgramEntryOrigin,
    value createSourceFunctionOrigin,
    value createLambdaOrigin,
    value createSpecializationOrigin,
    value createMutualRecursionDispatchOrigin,
    value createMutualRecursionWrapperOrigin,
    value createClosureNormalizerOrigin,
    value createCoroutineOrigin,
    value createCoroutineFrameDropperOrigin,
    value createExternalThunkOrigin,
    value createAdtDropperOrigin,
    value createResourceAdtDropperOrigin,
    value createClosureDropperOrigin,
    value createDeepCopierOrigin,
    value createStructuralOwnerDropperOrigin,
    value findInnermostLambdaUnderLets,
    value discoverSourceFunctionOrigins,
)

let createProgramEntryOrigin unit =
    IrFunctionOrigin(
        generatedLabel = "_start_main",
        originKind = ProgramEntryOrigin,
        sourceOrigin = None,
        parentGeneratedLabel = None,
        compilerOwner = Some(
            CompilerFunctionOwner(
                ownerKind = ProgramFunctionOwner,
                ownerName = "program entry"
            )
        ),
        stableDiscriminator = None,
        generationLocation = None
    )

let createSourceFunctionOrigin (name: Str) (qualifiedName: Maybe(Str)) (location: Maybe(IrSourceLocation)) (offset: Int) =
    SourceFunctionOrigin(
        functionSourceName = name,
        functionQualifiedName = qualifiedName,
        declarationLocation = location,
        declarationOffset = offset
    )

let lambdaSiteDiscriminator (paramName: Str) (span: TextSpan) =
    match span with
        | TextSpan(start, end) ->
            let length =
                if end > start
                then end - start
                else 0
            in "lambda:" + Ashes.Text.fromInt(start) + ":" + Ashes.Text.fromInt(length) + ":" + paramName

let createLambdaOrigin (label: Str) (paramName: Str) (span: TextSpan) (sourceOrigin: Maybe(SourceFunctionOrigin)) (parentOrigin: Maybe(IrFunctionOrigin)) (location: Maybe(IrSourceLocation)) =
    match sourceOrigin with
        | Some(SourceFunctionOrigin(name, qual, loc, off)) ->
            let src = SourceFunctionOrigin(name)(qual)(loc)(off)
            in
                if Ashes.Text.startsWith("__trait_validate_")(name)
                then
                    IrFunctionOrigin(
                        generatedLabel = label,
                        originKind = ClosureHelperOrigin,
                        sourceOrigin = None,
                        parentGeneratedLabel = None,
                        compilerOwner = Some(
                            CompilerFunctionOwner(
                                ownerKind = ProgramFunctionOwner,
                                ownerName = "trait declaration validation"
                            )
                        ),
                        stableDiscriminator = Some(name),
                        generationLocation = location
                    )
                else
                    IrFunctionOrigin(
                        generatedLabel = label,
                        originKind = SourceFunctionOriginKind,
                        sourceOrigin = Some(src),
                        parentGeneratedLabel = None,
                        compilerOwner = None,
                        stableDiscriminator = None,
                        generationLocation = location
                    )
        | None ->
            let discriminator = lambdaSiteDiscriminator(paramName)(span)
            in
                match parentOrigin with
                    | Some(IrFunctionOrigin(parentLabel, _kind, parentSrc, _pParent, _owner, _disc, _loc)) ->
                        IrFunctionOrigin(
                            generatedLabel = label,
                            originKind = ClosureHelperOrigin,
                            sourceOrigin = parentSrc,
                            parentGeneratedLabel = Some(parentLabel),
                            compilerOwner = None,
                            stableDiscriminator = Some(discriminator),
                            generationLocation = location
                        )
                    | None ->
                        IrFunctionOrigin(
                            generatedLabel = label,
                            originKind = ClosureHelperOrigin,
                            sourceOrigin = None,
                            parentGeneratedLabel = None,
                            compilerOwner = Some(
                                CompilerFunctionOwner(
                                    ownerKind = ProgramFunctionOwner,
                                    ownerName = "anonymous source function"
                                )
                            ),
                            stableDiscriminator = Some(discriminator),
                            generationLocation = location
                        )

let createSpecializationOrigin (kind: IrFunctionOriginKind) (label: Str) (sourceOrigin: Maybe(SourceFunctionOrigin)) (parentLabel: Maybe(Str)) (discriminator: Str) (location: Maybe(IrSourceLocation)) =
    IrFunctionOrigin(
        generatedLabel = label,
        originKind = kind,
        sourceOrigin = sourceOrigin,
        parentGeneratedLabel = parentLabel,
        compilerOwner = None,
        stableDiscriminator = Some(discriminator),
        generationLocation = location
    )

let createMutualRecursionDispatchOrigin (label: Str) (memberNames: List(Str)) (arity: Int) =
    (let ownerName = Ashes.Text.join(",")(memberNames)
    in
        IrFunctionOrigin(
            generatedLabel = label,
            originKind = MutualRecursionDispatchOrigin,
            sourceOrigin = None,
            parentGeneratedLabel = None,
            compilerOwner = Some(
                CompilerFunctionOwner(
                    ownerKind = MutualRecursionGroupFunctionOwner,
                    ownerName = ownerName
                )
            ),
            stableDiscriminator = Some("arity:" + Ashes.Text.fromInt(arity)),
            generationLocation = None
        ))

let createMutualRecursionWrapperOrigin (label: Str) (sourceOrigin: SourceFunctionOrigin) (parentLabel: Str) =
    IrFunctionOrigin(
        generatedLabel = label,
        originKind = MutualRecursionWrapperOrigin,
        sourceOrigin = Some(sourceOrigin),
        parentGeneratedLabel = Some(parentLabel),
        compilerOwner = None,
        stableDiscriminator = None,
        generationLocation = None
    )

let createClosureNormalizerOrigin (label: Str) (parentOrigin: Maybe(IrFunctionOrigin)) (parentLabel: Str) (capturesText: Str) =
    IrFunctionOrigin(
        generatedLabel = label,
        originKind = ClosureEnvironmentNormalizerOrigin,
        sourceOrigin = match parentOrigin with
            | Some(IrFunctionOrigin(_l, _k, src, _p, _o, _d, _loc)) -> src
            | None -> None,
        parentGeneratedLabel = Some(parentLabel),
        compilerOwner = match parentOrigin with
            | None ->
                Some(
                    CompilerFunctionOwner(
                        ownerKind = RuntimeLayoutFunctionOwner,
                        ownerName = capturesText
                    )
                )
            | Some(_) -> None,
        stableDiscriminator = Some(capturesText),
        generationLocation = None
    )

let createCoroutineOrigin (label: Str) (sourceOrigin: Maybe(SourceFunctionOrigin)) (parentLabel: Maybe(Str)) (discriminator: Maybe(Str)) (location: Maybe(IrSourceLocation)) =
    IrFunctionOrigin(
        generatedLabel = label,
        originKind = CoroutineOrigin,
        sourceOrigin = sourceOrigin,
        parentGeneratedLabel = parentLabel,
        compilerOwner = None,
        stableDiscriminator = discriminator,
        generationLocation = location
    )

let createCoroutineFrameDropperOrigin (label: Str) (parentLabel: Str) =
    IrFunctionOrigin(
        generatedLabel = label,
        originKind = CoroutineFrameDropperOrigin,
        sourceOrigin = None,
        parentGeneratedLabel = Some(parentLabel),
        compilerOwner = Some(
            CompilerFunctionOwner(
                ownerKind = RuntimeLayoutFunctionOwner,
                ownerName = "coroutine frame teardown"
            )
        ),
        stableDiscriminator = None,
        generationLocation = None
    )

let createExternalThunkOrigin (label: Str) (functionName: Str) =
    IrFunctionOrigin(
        generatedLabel = label,
        originKind = ExternalThunkOrigin,
        sourceOrigin = None,
        parentGeneratedLabel = None,
        compilerOwner = Some(
            CompilerFunctionOwner(
                ownerKind = ExternalFunctionOwner,
                ownerName = functionName
            )
        ),
        stableDiscriminator = Some(functionName),
        generationLocation = None
    )

let createAdtDropperOrigin (label: Str) (typeName: Str) =
    IrFunctionOrigin(
        generatedLabel = label,
        originKind = RuntimeManagedAdtDropperOrigin,
        sourceOrigin = None,
        parentGeneratedLabel = None,
        compilerOwner = Some(
            CompilerFunctionOwner(
                ownerKind = TypeFunctionOwner,
                ownerName = typeName
            )
        ),
        stableDiscriminator = Some(typeName),
        generationLocation = None
    )

let createResourceAdtDropperOrigin (label: Str) (typeName: Str) =
    IrFunctionOrigin(
        generatedLabel = label,
        originKind = ResourceAdtDropperOrigin,
        sourceOrigin = None,
        parentGeneratedLabel = None,
        compilerOwner = Some(
            CompilerFunctionOwner(
                ownerKind = TypeFunctionOwner,
                ownerName = typeName
            )
        ),
        stableDiscriminator = Some(typeName),
        generationLocation = None
    )

let createClosureDropperOrigin (label: Str) (captureCount: Int) =
    IrFunctionOrigin(
        generatedLabel = label,
        originKind = RuntimeManagedClosureDropperOrigin,
        sourceOrigin = None,
        parentGeneratedLabel = None,
        compilerOwner = Some(
            CompilerFunctionOwner(
                ownerKind = RuntimeLayoutFunctionOwner,
                ownerName = "closure captures: " + Ashes.Text.fromInt(captureCount)
            )
        ),
        stableDiscriminator = Some("captures:" + Ashes.Text.fromInt(captureCount)),
        generationLocation = None
    )

let createDeepCopierOrigin (label: Str) (typeName: Str) (isList: Bool) =
    IrFunctionOrigin(
        generatedLabel = label,
        originKind = if isList
        then ListDeepCopierOrigin
        else AdtDeepCopierOrigin,
        sourceOrigin = None,
        parentGeneratedLabel = None,
        compilerOwner = Some(
            CompilerFunctionOwner(
                ownerKind = TypeFunctionOwner,
                ownerName = typeName
            )
        ),
        stableDiscriminator = Some(typeName),
        generationLocation = None
    )

let createStructuralOwnerDropperOrigin (label: Str) (typeName: Str) =
    IrFunctionOrigin(
        generatedLabel = label,
        originKind = StructuralOwnerDropperOrigin,
        sourceOrigin = None,
        parentGeneratedLabel = None,
        compilerOwner = Some(
            CompilerFunctionOwner(
                ownerKind = TypeFunctionOwner,
                ownerName = typeName
            )
        ),
        stableDiscriminator = Some(typeName),
        generationLocation = None
    )

let recursive findInnermostLambdaUnderLets (expr: Expr) =
    match expr with
        | ExprAt(_span, inner) -> findInnermostLambdaUnderLets(inner)
        | ExprLambda(_, _, _) -> Some(expr)
        | ExprLet(_name, _val, body, _params, _type, _reqs) -> findInnermostLambdaUnderLets(body)
        | ExprLetRecursive(_name, _val, body, _params, _type, _reqs) -> findInnermostLambdaUnderLets(body)
        | ExprLetResult(_name, _val, body) -> findInnermostLambdaUnderLets(body)
        | _ -> None

let recursive getExpressionSpan (expr: Expr) =
    match expr with
        | ExprAt(span, _inner) -> span
        | _ -> TextSpan(start = 0, end = 0)

let recursive discoverSourceFunctionOrigins (expr: Expr) (enclosingSource: Maybe(SourceFunctionOrigin)) (sourceContext: Maybe(SourceContext)) =
    match expr with
        | ExprAt(_span, inner) -> discoverSourceFunctionOrigins(inner)(enclosingSource)(sourceContext)
        | ExprLet(name, value, body, _params, _type, _reqs) ->
            if Ashes.Text.startsWith("__trait_validate_")(name)
            then
                let valOrigins = discoverSourceFunctionOrigins(value)(enclosingSource)(sourceContext)
                in
                    let bodyOrigins = discoverSourceFunctionOrigins(body)(enclosingSource)(sourceContext)
                    in append(valOrigins)(bodyOrigins)
            else
                let span = getExpressionSpan(value)
                in
                    let offset =
                        match span with
                            | TextSpan(start, _end) -> start
                    in
                        let loc =
                            match sourceContext with
                                | Some(ctx) -> resolveSourceLocation(ctx)(span)
                                | None -> None
                        in
                            match findInnermostLambdaUnderLets(value) with
                                | Some(_) ->
                                    let declared =
                                        createSourceFunctionOrigin(
                                            name,
                                            None,
                                            loc,
                                            offset
                                        )
                                    in
                                        let valOrigins =
                                            discoverSourceFunctionOrigins(
                                                value
                                            )(
                                                Some(declared)
                                            )(
                                                sourceContext
                                            )
                                        in
                                            let bodyOrigins =
                                                discoverSourceFunctionOrigins(
                                                    body
                                                )(
                                                    enclosingSource
                                                )(
                                                    sourceContext
                                                )
                                            in (name, declared) :: append(valOrigins)(bodyOrigins)
                                | None ->
                                    let valOrigins = discoverSourceFunctionOrigins(value)(enclosingSource)(sourceContext)
                                    in
                                        let bodyOrigins = discoverSourceFunctionOrigins(body)(enclosingSource)(sourceContext)
                                        in append(valOrigins)(bodyOrigins)
        | ExprLetRecursive(name, value, body, _params, _type, _reqs) ->
            let span = getExpressionSpan(value)
            in
                let offset =
                    match span with
                        | TextSpan(start, _end) -> start
                in
                    let loc =
                        match sourceContext with
                            | Some(ctx) -> resolveSourceLocation(ctx)(span)
                            | None -> None
                    in
                        let declared =
                            createSourceFunctionOrigin(
                                name,
                                None,
                                loc,
                                offset
                            )
                        in
                            let valOrigins = discoverSourceFunctionOrigins(value)(Some(declared))(sourceContext)
                            in
                                let bodyOrigins = discoverSourceFunctionOrigins(body)(enclosingSource)(sourceContext)
                                in (name, declared) :: append(valOrigins)(bodyOrigins)
        | ExprLetResult(_name, value, body) ->
            let valOrigins = discoverSourceFunctionOrigins(value)(enclosingSource)(sourceContext)
            in
                let bodyOrigins = discoverSourceFunctionOrigins(body)(enclosingSource)(sourceContext)
                in append(valOrigins)(bodyOrigins)
        | ExprLambda(_param, body, _type) -> discoverSourceFunctionOrigins(body)(enclosingSource)(sourceContext)
        | ExprIf(c, t, e) ->
            append(
                discoverSourceFunctionOrigins(c)(enclosingSource)(sourceContext)
            )(
                append(
                    discoverSourceFunctionOrigins(t)(enclosingSource)(sourceContext)
                )(
                    discoverSourceFunctionOrigins(e)(enclosingSource)(sourceContext)
                )
            )
        | ExprCall(callee, arg, _isPipe, _layout) ->
            append(
                discoverSourceFunctionOrigins(callee)(enclosingSource)(sourceContext)
            )(
                discoverSourceFunctionOrigins(arg)(enclosingSource)(sourceContext)
            )
        | ExprTuple(elements) ->
            let recursive go elems =
                match elems with
                    | [] -> []
                    | head :: tail -> append(discoverSourceFunctionOrigins(head)(enclosingSource)(sourceContext))(go(tail))
            in go(elements)
        | ExprList(elements, _hasTrailing) ->
            let recursive go elems =
                match elems with
                    | [] -> []
                    | head :: tail -> append(discoverSourceFunctionOrigins(head)(enclosingSource)(sourceContext))(go(tail))
            in go(elements)
        | ExprCons(head, tail) ->
            append(
                discoverSourceFunctionOrigins(head)(enclosingSource)(sourceContext)
            )(
                discoverSourceFunctionOrigins(tail)(enclosingSource)(sourceContext)
            )
        | ExprMatch(scrutinee, arms, _len) ->
            let scrutOrigins = discoverSourceFunctionOrigins(scrutinee)(enclosingSource)(sourceContext)
            in
                let recursive goArms remaining =
                    match remaining with
                        | [] -> []
                        | (_pat, body, guard) :: tail ->
                            let bodyOrigins = discoverSourceFunctionOrigins(body)(enclosingSource)(sourceContext)
                            in
                                let guardOrigins =
                                    match guard with
                                        | Some(g) -> discoverSourceFunctionOrigins(g)(enclosingSource)(sourceContext)
                                        | None -> []
                                in append(bodyOrigins)(append(guardOrigins)(goArms(tail)))
                in append(scrutOrigins)(goArms(arms))
        | ExprRecord(_name, fields, _hasTrailing) ->
            let recursive goFields flds =
                match flds with
                    | [] -> []
                    | (_fName, fVal) :: tail -> append(discoverSourceFunctionOrigins(fVal)(enclosingSource)(sourceContext))(goFields(tail))
            in goFields(fields)
        | ExprRecordUpdate(target, fields) ->
            let targetOrigins = discoverSourceFunctionOrigins(target)(enclosingSource)(sourceContext)
            in
                let recursive goFields flds =
                    match flds with
                        | [] -> []
                        | (_fName, fVal) :: tail -> append(discoverSourceFunctionOrigins(fVal)(enclosingSource)(sourceContext))(goFields(tail))
                in append(targetOrigins)(goFields(fields))
        | ExprHandle(body, arms) ->
            let bodyOrigins = discoverSourceFunctionOrigins(body)(enclosingSource)(sourceContext)
            in
                let recursive goArms remaining =
                    match remaining with
                        | [] -> []
                        | (_cap, _op, _pats, armBody) :: tail ->
                            append(
                                discoverSourceFunctionOrigins(armBody)(enclosingSource)(sourceContext)
                            )(
                                goArms(tail)
                            )
                in append(bodyOrigins)(goArms(arms))
        | _ -> []
