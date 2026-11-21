// expect: Some(7):operand:42|Some(7):operand:true
type Operand =
    | Operand

implement Show(Operand) =
    | show =
        given (_value) -> "operand"

type Observation =
    | node: Maybe(Int)
    | operand: Operand

let recursive report sites =
    match sites with
        | [] -> ""
        | (Observation { node = node, operand = operand }, resolved) :: rest -> Ashes.Trait.Show.show(node) + ":" + Ashes.Trait.Show.show(operand) + ":" + Ashes.Trait.Show.show(resolved) + report(rest)

let observation = Observation(node = Some(7), operand = Operand)

Ashes.IO.print(report([(observation, 42)]) + "|" + report([(observation, true)]))
