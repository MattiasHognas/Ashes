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
        | LetBindingSyntax { name = name } :: rest ->
            deepCopy(
                name
            ) :: bindingNames(
                rest
            )

let recursive valueExportsForBindings (bindings: List(LetBindingSyntax)) =
    match bindings with
        | [] -> []
        | LetBindingSyntax { name = name } :: rest ->
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
        | TopLevelLet(LetBindingSyntax { name = name }, _recursive) ->
            [ImportValueExport(
                deepCopy(name)
            )]
        | TopLevelRecursiveGroup(bindings) -> valueExportsForBindings(bindings)
        | TopLevelType(TypeDecl { name = name, constructors = constructors }) ->
            ImportTypeExport(
                deepCopy(name)
            ) :: constructorExports(constructors)
        | TopLevelTypeAlias(TypeAliasDecl { name = name, typeParameters = _typeParameters, target = _target }) ->
            [ImportTypeExport(
                deepCopy(name)
            )]
        | TopLevelZeroCostType(declaration) ->
            match declaration with
                | ZeroCostTypeDecl { name = name, constructor = TypeConstructor { name = constructorName } } ->
                    [ImportTypeExport(
                        deepCopy(name)
                    ), ImportConstructorExport(
                        deepCopy(constructorName)
                    )]
        | TopLevelTrait(TraitDecl { name = name }) ->
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
        | item :: rest ->
            rest
            |> collectCompatibilityExports
            |> appendExports(compatibilityExportsForItem(item))

let recursive compatibilityModuleExports names =
    match names with
        | [] -> []
        | name :: rest ->
            ImportModuleExport(deepCopy(name)) :: compatibilityModuleExports(rest)

let compatibilityExports directModules items =
    (let exports = collectCompatibilityExports(items)
    in
        directModules
        |> compatibilityModuleExports
        |> appendExports(exports))

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
                | TopLevelLet(LetBindingSyntax { name = bindingName }, _recursive) ->
                    if bindingName == name
                    then true
                    else hasDeclaredValue(name)(rest)
                | TopLevelRecursiveGroup(bindings) ->
                    if bindings
                    |> bindingNames
                    |> containsText(name)
                    then true
                    else hasDeclaredValue(name)(rest)
                | _ -> hasDeclaredValue(name)(rest)

let declaredTypeForItem item =
    match unspanTopLevel(item) with
        | TopLevelType(TypeDecl { name = name, constructors = constructors }) ->
            Some(
                DeclaredTypeInterface(name = deepCopy(name), constructors = constructorNames(constructors))
            )
        | TopLevelTypeAlias(TypeAliasDecl { name = name, typeParameters = _typeParameters, target = _target }) ->
            Some(
                DeclaredTypeInterface(name = deepCopy(name), constructors = [])
            )
        | TopLevelZeroCostType(declaration) ->
            match declaration with
                | ZeroCostTypeDecl { name = name, constructor = TypeConstructor { name = constructorName } } ->
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

let moduleInterface name exports = ModuleImportInterface(name = deepCopy(name), exports = exports)

let exportKey item =
    match item with
        | ExportValue(name) -> "value:" + name
        | ExportType(name, _constructors) -> "type:" + name
        | ExportModule(name) -> "module:" + name

let addUniqueExport moduleName export reversed =
    if containsExport(export)(reversed)
    then
        match export with
            | ImportValueExport(name) ->
                "value:" + name
                |> DuplicateModuleExport(moduleName)
                |> Error
            | ImportTypeExport(name) ->
                "type:" + name
                |> DuplicateModuleExport(moduleName)
                |> Error
            | ImportConstructorExport(name) ->
                "constructor:" + name
                |> DuplicateModuleExport(moduleName)
                |> Error
            | ImportModuleExport(name) ->
                "module:" + name
                |> DuplicateModuleExport(moduleName)
                |> Error
    else Ok(export :: reversed)

let recursive addConstructorExports moduleName names declaredConstructors reversed =
    match names with
        | [] -> Ok(reversed)
        | name :: rest ->
            if containsText(name)(declaredConstructors)
            then
                match addUniqueExport(moduleName)(name
                |> deepCopy
                |> ImportConstructorExport)(reversed) with
                    | Error(error) -> Error(error)
                    | Ok(next) -> addConstructorExports(moduleName)(rest)(declaredConstructors)(next)
            else
                name
                |> UnknownModuleExport(moduleName)
                |> Error

let addTypeConstructors moduleName selection (declaration: DeclaredTypeInterface) reversed =
    match declaration with
        | DeclaredTypeInterface { name = _name, constructors = constructors } ->
            match selection with
                | ExportConstructorsHidden -> Ok(reversed)
                | ExportConstructorsAll -> addConstructorExports(moduleName)(constructors)(constructors)(reversed)
                | ExportConstructorsSelected(names) -> addConstructorExports(moduleName)(names)(constructors)(reversed)

let addTypeExport moduleName name selection declarations reversed =
    match findDeclaredType(name)(declarations) with
        | None ->
            name
            |> UnknownModuleExport(moduleName)
            |> Error
        | Some(declaration) ->
            match addUniqueExport(moduleName)(name
            |> deepCopy
            |> ImportTypeExport)(reversed) with
                | Error(error) -> Error(error)
                | Ok(withType) -> addTypeConstructors(moduleName)(selection)(declaration)(withType)

let addExplicitExport moduleName directModules declarations item reversed =
    match item with
        | ExportValue(name) ->
            if hasDeclaredValue(name)(declarations)
            then
                addUniqueExport(moduleName)(name
                |> deepCopy
                |> ImportValueExport)(reversed)
            else
                name
                |> UnknownModuleExport(moduleName)
                |> Error
        | ExportType(name, selection) -> addTypeExport(moduleName)(name)(selection)(declarations)(reversed)
        | ExportModule(name) ->
            if containsText(name)(directModules)
            then
                addUniqueExport(moduleName)(name
                |> deepCopy
                |> ImportModuleExport)(reversed)
            else
                name
                |> UnknownModuleExport(moduleName)
                |> Error

let recursive buildExplicitExports moduleName directModules declarations remaining keys reversed =
    match remaining with
        | [] ->
            reversed
            |> reverseList
            |> Ok
        | item :: rest ->
            if containsText(exportKey(item))(keys)
            then
                item
                |> exportKey
                |> DuplicateModuleExport(moduleName)
                |> Error
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
        | [] ->
            directModules
            |> compatibilityModuleExports
            |> moduleInterface(moduleName)
            |> Ok
        | first :: rest ->
            match unspanTopLevel(first) with
                | TopLevelExport(declaration) ->
                    if hasExportDeclaration(rest)
                    then Error(InvalidExportDeclaration(moduleName))
                    else
                        match buildExplicitExports(moduleName)(directModules)(items)(declaration.items)([])([]) with
                            | Error(error) -> Error(error)
                            | Ok(exports) ->
                                exports
                                |> moduleInterface(moduleName)
                                |> Ok
                | _ ->
                    if hasExportDeclaration(rest)
                    then Error(InvalidExportDeclaration(moduleName))
                    else
                        items
                        |> compatibilityExports(directModules)
                        |> moduleInterface(moduleName)
                        |> Ok

let buildModuleInterface moduleName directModules program =
    match program with
        | ProgramSyntax { items = items, body = _body } -> buildFromItems(moduleName)(directModules)(items)
