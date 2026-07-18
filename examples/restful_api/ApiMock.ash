import Api as api
import Ashes.Http.Server as http
import Ashes.IO as io
import Ashes.Test as test
let seeded = "[{\"id\":1,\"title\":\"write spec\",\"done\":true},{\"id\":2,\"title\":\"ship API\",\"done\":false}]"

let request m target bodyText = http.requestFromLine(m + " " + target + " HTTP/1.1")("")(bodyText)

let mocked req =
    req
    |> api.route
    |> http.render

let check label expected actual =
    (let _ =
        test.assertEqual(http.render(expected))(actual)
    in io.writeLine("ok - " + label))

let listAll _ =
    ""
    |> request("GET")("/todos")
    |> mocked
    |> check("GET /todos returns the seeded collection")(http.json(200)(seeded))

let getOne _ =
    ""
    |> request("GET")("/todos/2")
    |> mocked
    |> check("GET /todos/2 returns one todo")(http.json(200)("{\"id\":2,\"title\":\"ship API\",\"done\":false}"))

let getMissing _ =
    ""
    |> request("GET")("/todos/99")
    |> mocked
    |> check("GET /todos/99 is 404")(http.json(404)("{\"error\":\"not found\"}"))

let createOne _ =
    "{\"title\":\"profit\"}"
    |> request("POST")("/todos")
    |> mocked
    |> check("POST /todos creates with the next id")(http.json(201)("{\"id\":3,\"title\":\"profit\",\"done\":false}"))

let createInvalid _ =
    "{}"
    |> request("POST")("/todos")
    |> mocked
    |> check("POST /todos without a title is 400")(http.json(400)("{\"error\":\"missing field: title\"}"))

let updateOne _ =
    "{\"title\":\"revise spec\",\"done\":false}"
    |> request("PUT")("/todos/1")
    |> mocked
    |> check("PUT /todos/1 replaces title and done")(http.json(200)("{\"id\":1,\"title\":\"revise spec\",\"done\":false}"))

let updateMissing _ =
    "{\"title\":\"ghost\"}"
    |> request("PUT")("/todos/99")
    |> mocked
    |> check("PUT /todos/99 is 404")(http.json(404)("{\"error\":\"not found\"}"))

let deleteOne _ =
    ""
    |> request("DELETE")("/todos/2")
    |> mocked
    |> check("DELETE /todos/2 is 204")(http.respond(204)("")(""))

let deleteMissing _ =
    ""
    |> request("DELETE")("/todos/99")
    |> mocked
    |> check("DELETE /todos/99 is 404")(http.json(404)("{\"error\":\"not found\"}"))

let invalidId _ =
    ""
    |> request("GET")("/todos/nope")
    |> mocked
    |> check("GET /todos/nope is 400")(http.json(400)("{\"error\":\"invalid id\"}"))

let invalidMethod _ =
    ""
    |> request("PATCH")("/todos")
    |> mocked
    |> check("PATCH /todos is 405")(http.json(405)("{\"error\":\"method not allowed\"}"))

let unknownRoute _ =
    ""
    |> request("GET")("/nope")
    |> mocked
    |> check("GET /nope is 404")(http.json(404)("{\"error\":\"not found\"}"))

let allPassed _ = "all tests passed"

let runTests _ =
    handle Unit
    |> listAll
    |> getOne
    |> getMissing
    |> createOne
    |> createInvalid
    |> updateOne
    |> updateMissing
    |> deleteOne
    |> deleteMissing
    |> invalidId
    |> invalidMethod
    |> unknownRoute
    |> allPassed with
        | Store.load(_u) -> resume(seeded)
        | Store.save(_text) -> resume(Unit)
        | return(r) -> r
