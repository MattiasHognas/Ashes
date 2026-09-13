// expect: Eq:equal Ord:compare Eq:equal 2000
type Method =
    | name: Str
    | ty: Int
    | impl: Str

type Impl =
    | traitName: Str
    | methods: List(Method)

type Env =
    | impls: List(Impl)

let methodNameOf (t: Str) =
    match t with
        | "Eq" -> "equal"
        | "Ord" -> "compare"
        | "Show" -> "show"
        | "Hash" -> "hash"
        | "Default" -> "default"
        | "Add" -> "add"
        | "Subtract" -> "subtract"
        | "Multiply" -> "multiply"
        | "Divide" -> "divide"
        | "Remainder" -> "remainder"
        | "Negate" -> "negate"
        | "Not" -> "not"
        | "BitAnd" -> "bitAnd"
        | "BitOr" -> "bitOr"
        | "BitXor" -> "bitXor"
        | "ShiftLeft" -> "shiftLeft"
        | "ShiftRight" -> "shiftRight"
        | "BitwiseNot" -> "bitwiseNot"
        | _ -> ""

let addTraitImplementation (t: Str) (methods: List(Method)) (env: Env) =
    match env with
        | Env { impls = impls } -> Env(impls = Impl(traitName = t, methods = methods) :: impls)

let addImpl (t: Str) (head: Int) (env: Env) =
    (let methodName = methodNameOf(t)
    in
        let methodType = head + 1
        in
            let implementation = "__x_" + t + "_" + methodName + "_" + Ashes.Text.fromInt(head)
            in addTraitImplementation(t)([Method(name = methodName, ty = methodType, impl = implementation)])(env))

let buildEnv unit =
    Env(impls = [])
    |> addImpl("Eq")(1)
    |> addImpl("Ord")(2)
    |> addImpl("Eq")(3)

let standardEnv = buildEnv(Unit)

let recursive churn (n: Int) (acc: List(Str)) =
    if n == 0
    then acc
    else churn(n - 1)("let " + Ashes.Text.fromInt(n) + " in x" :: acc)

let recursive firstNames (impls: List(Impl)) =
    match impls with
        | [] -> ""
        | Impl { traitName = t, methods = Method { name = name } :: _ } :: rest -> t + ":" + name + " " + firstNames(rest)
        | _ :: rest -> firstNames(rest)

let noise = churn(2000)([])

match standardEnv with
    | Env { impls = impls } ->
        Ashes.IO.print(firstNames(impls) + Ashes.Text.fromInt(Ashes.Collection.List.length(noise)))
