// Builds the explicit public interface of a parsed module.
//
// Invariants:
// - Only export declarations contribute bindings; imports are never implicit re-exports.
// - Externals and a trailing expression are not module exports.
// - Constructor exports remain associated with their declared type and duplicates are rejected.

import Ashes.Collection.List.reverse as reverseList
import Ashes.Internal.deepCopy as deepCopy
import AshesCompiler.Frontend.ImportResolution
import AshesCompiler.Frontend.Syntax
export (
    type ModuleInterfaceBuildError(..),
    value buildModuleInterface,
)

type ModuleInterfaceBuildError =
    | InvalidExportDeclaration(Str)
    | DuplicateModuleExport(Str, Str)
    | UnknownModuleExport(Str, Str)
    deriving {Eq, Show}

type DeclaredTypeInterface =
    | name: Str
    | constructors: List(Str)

let recursive unspanTopLevel item =
    match item with
        | TopLevelAt(_span, inner) -> unspanTopLevel(inner)
        | _ -> item

let recursive containsText name values =
    match values with
        | [] -> false
        | candidate :: rest ->
            if candidate == name
            then true
            else containsText(name)(rest)

let recursive containsExport exportToFind exports =
    match exports with
        | [] -> false
        | candidate :: rest ->
            if candidate == exportToFind
            then true
            else containsExport(exportToFind)(rest)

let recursive constructorNames (constructors: List(TypeConstructor)) =
    match constructors with
        | [] -> []
        | TypeConstructor { name = name, parameters = _parameters, fieldNames = _fieldNames } :: rest ->
            deepCopy(
                name
            ) :: constructorNames(
                rest
            )

let recursive bindingNames (bindings: List(LetBindingSyntax)) =
    match bindings with
        | [] -> []
        | LetBindingSyntax { name = name, value = _value, sugarParameters = _sugarParameters, typeAnnotation = _typeAnnotation, requirements = _requirements } :: rest ->
            deepCopy(
                name
            ) :: bindingNames(
                rest
            )

let recursive valueExportsForBindings (bindings: List(LetBindingSyntax)) =
    match bindings with
        | [] -> []
        | LetBindingSyntax { name = name, value = _value, sugarParameters = _sugarParameters, typeAnnotation = _typeAnnotation, requirements = _requirements } :: rest ->
            ImportValueExport(
                deepCopy(name)
            ) :: valueExportsForBindings(rest)

let recursive constructorExports (constructors: List(TypeConstructor)) =
    match constructors with
        | [] -> []
        | TypeConstructor { name = name, parameters = _parameters, fieldNames = _fieldNames } :: rest ->
            ImportConstructorExport(
                deepCopy(name)
            ) :: constructorExports(rest)

let compatibilityExportsForItem item =
    match unspanTopLevel(item) with
        | TopLevelLet(LetBindingSyntax { name = name, value = _value, sugarParameters = _sugarParameters, typeAnnotation = _typeAnnotation, requirements = _requirements }, _recursive) ->
            [ImportValueExport(
                deepCopy(name)
            )]
        | TopLevelRecursiveGroup(bindings) -> valueExportsForBindings(bindings)
        | TopLevelType(TypeDecl { name = name, typeParameters = _typeParameters, constructors = constructors, isRecord = _isRecord, derivingTraits = _derivingTraits }) ->
            ImportTypeExport(
                deepCopy(name)
            ) :: constructorExports(constructors)
        | TopLevelTypeAlias(TypeAliasDecl { name = name, typeParameters = _typeParameters, target = _target }) ->
            [ImportTypeExport(
                deepCopy(name)
            )]
        | TopLevelZeroCostType(ZeroCostTypeDecl { name = name, typeParameters = _typeParameters, constructor = TypeConstructor { name = constructorName, parameters = _parameters, fieldNames = _fieldNames }, derivingTraits = _derivingTraits }) ->
            [ImportTypeExport(
                deepCopy(name)
            ), ImportConstructorExport(
                deepCopy(constructorName)
            )]
        | TopLevelTrait(TraitDecl { name = name, typeParameters = _typeParameters, supertraits = _supertraits, methods = _methods }) ->
            [ImportTypeExport(
                deepCopy(name)
            )]
        | _ -> []

let recursive appendExports left right =
    match left with
        | [] -> right
        | head :: rest -> head :: appendExports(rest)(right)

let recursive collectCompatibilityExports items =
    match items with
        | [] -> []
        | item :: rest -> appendExports(compatibilityExportsForItem(item))(collectCompatibilityExports(rest))

let recursive hasExportDeclaration items =
    match items with
        | [] -> false
        | item :: rest ->
            match unspanTopLevel(item) with
                | TopLevelExport(_declaration) -> true
                | _ -> hasExportDeclaration(rest)

let recursive hasDeclaredValue name items =
    match items with
        | [] -> false
        | item :: rest ->
            match unspanTopLevel(item) with
                | TopLevelLet(LetBindingSyntax { name = bindingName, value = _value, sugarParameters = _sugarParameters, typeAnnotation = _typeAnnotation, requirements = _requirements }, _recursive) ->
                    if bindingName == name
                    then true
                    else hasDeclaredValue(name)(rest)
                | TopLevelRecursiveGroup(bindings) ->
                    if containsText(name)(bindingNames(bindings))
                    then true
                    else hasDeclaredValue(name)(rest)
                | _ -> hasDeclaredValue(name)(rest)

let declaredTypeForItem item =
    match unspanTopLevel(item) with
        | TopLevelType(TypeDecl { name = name, typeParameters = _typeParameters, constructors = constructors, isRecord = _isRecord, derivingTraits = _derivingTraits }) ->
            Some(
                DeclaredTypeInterface(name = deepCopy(name), constructors = constructorNames(constructors))
            )
        | TopLevelTypeAlias(TypeAliasDecl { name = name, typeParameters = _typeParameters, target = _target }) ->
            Some(
                DeclaredTypeInterface(name = deepCopy(name), constructors = [])
            )
        | TopLevelZeroCostType(ZeroCostTypeDecl { name = name, typeParameters = _typeParameters, constructor = TypeConstructor { name = constructorName, parameters = _parameters, fieldNames = _fieldNames }, derivingTraits = _derivingTraits }) ->
            Some(
                DeclaredTypeInterface(name = deepCopy(name), constructors = [deepCopy(constructorName)])
            )
        | _ -> None

let recursive findDeclaredType name items =
    match items with
        | [] -> None
        | item :: rest ->
            match declaredTypeForItem(item) with
                | Some(declaration) ->
                    if declaration.name == name
                    then Some(declaration)
                    else findDeclaredType(name)(rest)
                | None -> findDeclaredType(name)(rest)

let exportKey item =
    match item with
        | ExportValue(name) -> "value:" + name
        | ExportType(name, _constructors) -> "type:" + name
        | ExportModule(name) -> "module:" + name

let addUniqueExport moduleName export reversed =
    if containsExport(export)(reversed)
    then
        match export with
            | ImportValueExport(name) -> Error(DuplicateModuleExport(moduleName)("value:" + name))
            | ImportTypeExport(name) -> Error(DuplicateModuleExport(moduleName)("type:" + name))
            | ImportConstructorExport(name) -> Error(DuplicateModuleExport(moduleName)("constructor:" + name))
            | ImportModuleExport(name) -> Error(DuplicateModuleExport(moduleName)("module:" + name))
    else Ok(export :: reversed)

let recursive addConstructorExports moduleName names declaredConstructors reversed =
    match names with
        | [] -> Ok(reversed)
        | name :: rest ->
            if containsText(name)(declaredConstructors)
            then
                match addUniqueExport(moduleName)(ImportConstructorExport(deepCopy(name)))(reversed) with
                    | Error(error) -> Error(error)
                    | Ok(next) -> addConstructorExports(moduleName)(rest)(declaredConstructors)(next)
            else Error(UnknownModuleExport(moduleName)(name))

let addTypeConstructors moduleName selection (declaration: DeclaredTypeInterface) reversed =
    match declaration with
        | DeclaredTypeInterface { name = _name, constructors = constructors } ->
            match selection with
                | ExportConstructorsHidden -> Ok(reversed)
                | ExportConstructorsAll -> addConstructorExports(moduleName)(constructors)(constructors)(reversed)
                | ExportConstructorsSelected(names) -> addConstructorExports(moduleName)(names)(constructors)(reversed)

let addTypeExport moduleName name selection declarations reversed =
    match findDeclaredType(name)(declarations) with
        | None -> Error(UnknownModuleExport(moduleName)(name))
        | Some(declaration) ->
            match addUniqueExport(moduleName)(ImportTypeExport(deepCopy(name)))(reversed) with
                | Error(error) -> Error(error)
                | Ok(withType) -> addTypeConstructors(moduleName)(selection)(declaration)(withType)

let addExplicitExport moduleName directModules declarations item reversed =
    match item with
        | ExportValue(name) ->
            if hasDeclaredValue(name)(declarations)
            then addUniqueExport(moduleName)(ImportValueExport(deepCopy(name)))(reversed)
            else Error(UnknownModuleExport(moduleName)(name))
        | ExportType(name, selection) -> addTypeExport(moduleName)(name)(selection)(declarations)(reversed)
        | ExportModule(name) ->
            if containsText(name)(directModules)
            then addUniqueExport(moduleName)(ImportModuleExport(deepCopy(name)))(reversed)
            else Error(UnknownModuleExport(moduleName)(name))

let recursive buildExplicitExports moduleName directModules declarations remaining keys reversed =
    match remaining with
        | [] -> Ok(reverseList(reversed))
        | item :: rest ->
            if containsText(exportKey(item))(keys)
            then Error(DuplicateModuleExport(moduleName)(exportKey(item)))
            else
                match addExplicitExport(moduleName)(directModules)(declarations)(item)(reversed) with
                    | Error(error) -> Error(error)
                    | Ok(next) ->
                        buildExplicitExports(
                            moduleName,
                            directModules,
                            declarations,
                            rest,
                            exportKey(item) :: keys,
                            next
                        )

let buildFromItems moduleName directModules items =
    match items with
        | [] -> Ok(ModuleImportInterface(name = deepCopy(moduleName), exports = []))
        | first :: rest ->
            match unspanTopLevel(first) with
                | TopLevelExport(declaration) ->
                    if hasExportDeclaration(rest)
                    then Error(InvalidExportDeclaration(moduleName))
                    else
                        match buildExplicitExports(moduleName)(directModules)(items)(declaration.items)([])([]) with
                            | Error(error) -> Error(error)
                            | Ok(exports) -> Ok(ModuleImportInterface(name = deepCopy(moduleName), exports = exports))
                | _ ->
                    if hasExportDeclaration(rest)
                    then Error(InvalidExportDeclaration(moduleName))
                    else
                        Ok(
                            ModuleImportInterface(name = deepCopy(moduleName), exports = collectCompatibilityExports(items))
                        )

let buildModuleInterface moduleName directModules program =
    match program with
        | ProgramSyntax { items = items, body = _body } -> buildFromItems(moduleName)(directModules)(items)
