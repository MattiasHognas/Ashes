import CapturedEnvironmentInferenceTests
Unit
|> runCapturedEnvironmentInferenceTests
|> (given (_) -> Ashes.IO.print("captured environment inference: 25 cases passed"))
