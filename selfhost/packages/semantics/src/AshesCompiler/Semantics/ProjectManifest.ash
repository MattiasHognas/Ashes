import Ashes.Text
import Ashes.Text.Json
import Ashes.Internal.deepCopy as deepCopy
export (
    type ProjectDefaults(..),
    type ProjectDependencySource(..),
    type ProjectDependency(..),
    type ProjectManifest(..),
    type ProjectManifestError(..),
    value parseProjectManifest,
)

type ProjectDefaults =
    | optimize: Maybe(Bool)
    deriving {Eq, Show}

type ProjectDependencySource =
    | RegistryDependency(Str)
    | PathDependency(Str, Maybe(Str))
    deriving {Eq, Show}

type ProjectDependency =
    | name: Str
    | source: ProjectDependencySource
    deriving {Eq, Show}

type ProjectManifest =
    | entry: Str
    | name: Maybe(Str)
    | namespace: Maybe(Str)
    | sourceRoots: List(Str)
    | includeRoots: List(Str)
    | outDir: Str
    | target: Maybe(Str)
    | defaults: ProjectDefaults
    | dependencies: List(ProjectDependency)
    | devDependencies: List(ProjectDependency)
    deriving {Eq, Show}

type ProjectManifestError =
    | ProjectJsonParseError(Str)
    | ProjectManifestMustBeObject
    | MissingProjectEntry
    | InvalidProjectEntry(Str)
    deriving {Eq, Show}

type alias ManifestJson = Json(Bool, Int, Float, Str)

let recursive findField (key: Str) (json: ManifestJson) =
    match json with
        | JsonObjectEnd -> None
        | JsonObject(candidate, value, rest) ->
            if candidate == key
            then Some(value)
            else findField(key)(rest)
        | _ -> None

let optionalStringField (key: Str) (json: ManifestJson) =
    match findField(key)(json) with
        | Some(JsonStr(value)) -> Some(deepCopy(value))
        | _ -> None

let optionalBoolField (key: Str) (json: ManifestJson) =
    match findField(key)(json) with
        | Some(JsonBool(value)) -> Some(value)
        | _ -> None

let recursive collectStringArray (json: ManifestJson) =
    match json with
        | JsonArrayEnd -> []
        | JsonArray(JsonStr(value), rest) ->
            if Ashes.Text.trim(value) == ""
            then collectStringArray(rest)
            else deepCopy(value) :: collectStringArray(rest)
        | JsonArray(_value, rest) -> collectStringArray(rest)
        | _ -> []

let stringArrayField (key: Str) (json: ManifestJson) =
    match findField(key)(json) with
        | Some(value) -> collectStringArray(value)
        | None -> []

let sourceRootsField (json: ManifestJson) =
    match stringArrayField("sourceRoots")(json) with
        | [] -> ["."]
        | roots -> roots

let outDirField (json: ManifestJson) =
    match optionalStringField("outDir")(json) with
        | Some(value) ->
            if Ashes.Text.trim(value) == ""
            then "out"
            else value
        | None -> "out"

let defaultsField (json: ManifestJson) =
    match findField("defaults")(json) with
        | Some(value) -> ProjectDefaults(optimize = optionalBoolField("optimize")(value))
        | None -> ProjectDefaults(optimize = None)

let isAshExtension suffix =
    match suffix with
        | ".ash" -> true
        | ".asH" -> true
        | ".aSh" -> true
        | ".aSH" -> true
        | ".Ash" -> true
        | ".AsH" -> true
        | ".ASh" -> true
        | ".ASH" -> true
        | _ -> false

let hasAshExtension value =
    if Ashes.Text.length(value) < 4
    then false
    else isAshExtension(Ashes.Text.drop(value)(Ashes.Text.length(value) - 4))

let requiredEntry (json: ManifestJson) =
    match optionalStringField("entry")(json) with
        | None -> Error(MissingProjectEntry)
        | Some(value) ->
            if hasAshExtension(value)
            then Ok(value)
            else Error(InvalidProjectEntry(value))

let dependencyFromValue (name: Str) (value: ManifestJson) =
    match value with
        | JsonStr(constraint) -> Some(ProjectDependency(name = deepCopy(name), source = RegistryDependency(deepCopy(constraint))))
        | JsonObject(_key, _field, _rest) ->
            match optionalStringField("path")(value) with
                | Some(path) -> Some(ProjectDependency(name = deepCopy(name), source = PathDependency(path)(optionalStringField("namespace")(value))))
                | None -> None
        | _ -> None

let recursive dependenciesFromObject (json: ManifestJson) =
    match json with
        | JsonObjectEnd -> []
        | JsonObject(name, value, rest) ->
            match dependencyFromValue(name)(value) with
                | Some(dependency) -> dependency :: dependenciesFromObject(rest)
                | None -> dependenciesFromObject(rest)
        | _ -> []

let dependenciesField (key: Str) (json: ManifestJson) =
    match findField(key)(json) with
        | None -> []
        | Some(value) -> dependenciesFromObject(value)

let finishManifest (json: ManifestJson) (entry: Str) (dependencies: List(ProjectDependency)) = Ok(ProjectManifest(entry = entry, name = optionalStringField("name")(deepCopy(json)), namespace = optionalStringField("namespace")(deepCopy(json)), sourceRoots = sourceRootsField(deepCopy(json)), includeRoots = stringArrayField("include")(deepCopy(json)), outDir = outDirField(deepCopy(json)), target = optionalStringField("target")(deepCopy(json)), defaults = defaultsField(deepCopy(json)), dependencies = dependencies, devDependencies = dependenciesField("devDependencies")(json)))

let buildManifest (json: ManifestJson) (entry: Str) = finishManifest(json)(entry)(dependenciesField("dependencies")(deepCopy(json)))

let parseManifestObject (json: ManifestJson) =
    match requiredEntry(deepCopy(json)) with
        | Error(error) -> Error(error)
        | Ok(entry) -> buildManifest(json)(entry)

let parseProjectManifest (source: Str) =
    match Ashes.Text.Json.parse(source) with
        | Error(error) -> Error(ProjectJsonParseError(error))
        | Ok(JsonObjectEnd) -> parseManifestObject(JsonObjectEnd)
        | Ok(JsonObject(_key, _value, _rest) as object) -> parseManifestObject(object)
        | Ok(_value) -> Error(ProjectManifestMustBeObject)
