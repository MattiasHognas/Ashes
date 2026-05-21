// Requires a HTTPS/1.1 endpoint with a certificate trusted by OpenSSL.
// Example local target: openssl s_server -accept 8080 -cert cert.pem -key key.pem -www
Ashes.IO.print(match Ashes.Async.run(async
    await Ashes.Http.get("https://localhost:8080/")) with
    | Ok(body) -> body
    | Error(err) -> err)
