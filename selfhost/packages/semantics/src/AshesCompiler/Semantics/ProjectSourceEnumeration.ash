import Ashes.Collection.List.append as appendList
import Ashes.Collection.List.reverse as reverseList
import Ashes.IO.Path
import Ashes.Internal.deepCopy as deepCopy
export (
    type ProjectSourceEnumerationError(..),
    value enumerateProjectSourceFiles,
)

type ProjectSourceEnumerationError =
    | ProjectSourceRootReadError(Str, Str)
    deriving {Eq, Show}

type SourceEnumerationTask =
    | SourceRoot(Str)
    | SourceEntry(Str)

let recursive containsPath path paths =
    match paths with
        | [] -> false
        | candidate :: rest ->
            if candidate == path
            then true
            else containsPath(path)(rest)

let recursive entryTasks style directory names =
    match names with
        | [] -> []
        | name :: rest -> SourceEntry(join(style)(deepCopy(directory))(name)) :: entryTasks(style)(directory)(rest)

let recursive rootTasks style roots =
    match roots with
        | [] -> []
        | root :: rest -> SourceRoot(normalize(style)(root)) :: rootTasks(style)(rest)

let recursive enumerateTasks style tasks reversed =
    match tasks with
        | [] -> Ok(reversed)
        | SourceRoot(root) :: rest ->
            match Ashes.IO.Directory.entries(deepCopy(root)) with
                | Error(error) -> Error(ProjectSourceRootReadError(root)(error))
                | Ok(names) -> enumerateTasks(style)(appendList(entryTasks(style)(root)(names))(rest))(reversed)
        | SourceEntry(path) :: rest ->
            if extension(style)(deepCopy(path)) == ".ash"
            then enumerateTasks(style)(rest)(path :: reversed)
            else
                match Ashes.IO.Directory.entries(deepCopy(path)) with
                    | Error(_error) -> enumerateTasks(style)(rest)(reversed)
                    | Ok(names) -> enumerateTasks(style)(appendList(entryTasks(style)(path)(names))(rest))(reversed)

let recursive uniquePaths paths seen =
    match paths with
        | [] -> []
        | path :: rest ->
            if containsPath(deepCopy(path))(deepCopy(seen))
            then uniquePaths(rest)(seen)
            else deepCopy(path) :: uniquePaths(rest)(path :: seen)

let enumerateProjectSourceFiles style roots =
    match enumerateTasks(style)(rootTasks(style)(roots))([]) with
        | Error(error) -> Error(error)
        | Ok(reversed) -> Ok(uniquePaths(reverseList(reversed))([]))
