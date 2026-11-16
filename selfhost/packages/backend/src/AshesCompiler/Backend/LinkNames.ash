// The companion of the link map: one line per IR function of a program, naming where the
// function came from, so a code label in the link map can be traced back to source. Each line is
// `<label>\t<origin kind>\t<source function name>\t<parent label>`, with `-` for a missing field,
// in the program's function order with the entry function first.
import AshesCompiler.Semantics.Ir
import AshesCompiler.Semantics.IrOrigins
import Ashes.Collection.List.reverse as reverseList
export (
    value linkNamesText,
)

let sourceNameText (source: Maybe(SourceFunctionOrigin)) =
    match source with
        | Some(SourceFunctionOrigin { functionQualifiedName = Some(qualified) }) when qualified != "" -> qualified
        | Some(SourceFunctionOrigin { functionSourceName = name }) when name != "" -> name
        | _ -> "-"

let parentLabelText (parent: Maybe(Str)) =
    match parent with
        | Some(label) when label != "" -> label
        | _ -> "-"

let linkNamesLine (function_: IrFunction) =
    match function_ with
        | IrFunction { label = label, origin = None } -> label + "\t-\t-\t-\n"
        | IrFunction { label = label, origin = Some(IrFunctionOrigin { originKind = kind, sourceOrigin = source, parentGeneratedLabel = parent }) } -> label + "\t" + Ashes.Trait.Show.show(kind) + "\t" + sourceNameText(source) + "\t" + parentLabelText(parent) + "\n"

let recursive linkNamesLines (functions: List(IrFunction)) (acc: List(Str)) =
    match functions with
        | [] -> reverseList(acc)
        | function_ :: rest -> linkNamesLines(rest)(linkNamesLine(function_) :: acc)

let linkNamesText (program: IrProgram) =
    match program with
        | IrProgram { entryFunction = entry, functions = functions } ->
            []
            |> linkNamesLines(entry :: functions)
            |> Ashes.Text.join("")
