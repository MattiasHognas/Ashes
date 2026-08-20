import Ashes.IO.Path
import Ashes.Text.Json
import Ashes.Internal.deepCopy as deepCopy
export (
    type LockedPackage(..),
    type ProjectLockFile(..),
    type ProjectLockFileError(..),
    value cachePathFor,
    value lockFilePath,
    value parseProjectLockFile,
)

type LockedPackage =
    | namespace: Str
    | version: Str
    | source: Str
    | hash: Str
    | dependencies: List(Str)
    deriving {Eq, Show}

type ProjectLockFile =
    | version: Int
    | packages: List(LockedPackage)
    deriving {Eq, Show}

type ProjectLockFileError =
    | ProjectLockJsonParseError(Str)
    | ProjectLockFileMustBeObject
    | InvalidProjectLockVersion
    | UnsupportedProjectLockVersion(Int)
    | InvalidProjectLockPackages
    | InvalidLockedPackage(Str)
    deriving {Eq, Show}

type alias LockJson = Json(Bool, Int, Float, Str)

let recursive hashKeyAfterColon remaining =
    match Ashes.Text.unconsText(remaining) with
        | None -> None
        | Some((head, tail)) ->
            if head == ":"
            then Some(tail)
            else hashKeyAfterColon(tail)

let hashKey hash =
    match hashKeyAfterColon(hash) with
        | Some(key) -> key
        | None -> hash

let cachePathFor style cacheRoot (package: LockedPackage) =
    match package with
        | LockedPackage { namespace = namespace, version = version, source = _source, hash = hash, dependencies = _dependencies } ->
            hash
            |> hashKey
            |> join(style)(join(style)(join(style)(join(style)(cacheRoot)("pkg"))(namespace))(version))

let lockFilePath style projectFilePath =
    match projectFilePath
    |> deepCopy
    |> extension(style) with
        | suffix ->
            normalize(style)(Ashes.Text.take(deepCopy(projectFilePath))(Ashes.Text.length(projectFilePath) - Ashes.Text.length(suffix)) + ".lock")

let recursive findLockField (key: Str) (json: LockJson) =
    match json with
        | JsonObjectEnd -> None
        | JsonObject(candidate, value, rest) ->
            if candidate == key
            then Some(value)
            else findLockField(key)(rest)
        | _ -> None

let requiredPackageString key json =
    match findLockField(key)(json) with
        | Some(JsonStr(value)) ->
            value
            |> deepCopy
            |> Ok
        | _ -> Error(InvalidLockedPackage(key))

let prependLockedDependency value result =
    match result with
        | Error(error) -> Error(error)
        | Ok(names) -> Ok(deepCopy(value) :: names)

let recursive lockedDependencyNames json =
    match json with
        | JsonArrayEnd -> Ok([])
        | JsonArray(JsonStr(value), rest) ->
            rest
            |> lockedDependencyNames
            |> prependLockedDependency(value)
        | _ -> Error(InvalidLockedPackage("dependencies"))

let lockedDependencies json =
    match findLockField("dependencies")(json) with
        | Some(value) -> lockedDependencyNames(value)
        | None -> Ok([])

let lockedPackage json =
    match (json
    |> deepCopy
    |> requiredPackageString("namespace"), json
    |> deepCopy
    |> requiredPackageString("version"), json
    |> deepCopy
    |> requiredPackageString("source"), json
    |> deepCopy
    |> requiredPackageString("hash"), lockedDependencies(json)) with
        | (Error(error), _, _, _, _) -> Error(error)
        | (_, Error(error), _, _, _) -> Error(error)
        | (_, _, Error(error), _, _) -> Error(error)
        | (_, _, _, Error(error), _) -> Error(error)
        | (_, _, _, _, Error(error)) -> Error(error)
        | (Ok(namespace), Ok(version), Ok(source), Ok(hash), Ok(dependencies)) -> Ok(LockedPackage(namespace = namespace, version = version, source = source, hash = hash, dependencies = dependencies))

let prependLockedPackage packageResult packagesResult =
    match (packageResult, packagesResult) with
        | (Error(error), _) -> Error(error)
        | (_, Error(error)) -> Error(error)
        | (Ok(package), Ok(packages)) -> Ok(package :: packages)

let recursive lockedPackages json =
    match json with
        | JsonArrayEnd -> Ok([])
        | JsonArray(value, rest) ->
            rest
            |> lockedPackages
            |> prependLockedPackage(value
            |> deepCopy
            |> lockedPackage)
        | _ -> Error(InvalidProjectLockPackages)

let lockVersion json =
    match findLockField("version")(json) with
        | None -> Ok(1)
        | Some(JsonInt(1)) -> Ok(1)
        | Some(JsonInt(version)) -> Error(UnsupportedProjectLockVersion(version))
        | Some(_) -> Error(InvalidProjectLockVersion)

let lockPackages json =
    match findLockField("package")(json) with
        | None -> Ok([])
        | Some(value) -> lockedPackages(value)

let projectLockFile json =
    match (json
    |> deepCopy
    |> lockVersion, lockPackages(json)) with
        | (Error(error), _) -> Error(error)
        | (_, Error(error)) -> Error(error)
        | (Ok(version), Ok(packages)) -> Ok(ProjectLockFile(version = version, packages = packages))

let parseProjectLockFile source =
    match Ashes.Text.Json.parse(source) with
        | Error(error) -> Error(ProjectLockJsonParseError(error))
        | Ok(JsonObjectEnd) -> projectLockFile(JsonObjectEnd)
        | Ok(JsonObject(_key, _value, _rest) as object) -> projectLockFile(object)
        | Ok(_) -> Error(ProjectLockFileMustBeObject)
