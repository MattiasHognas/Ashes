import ProjectCompilationPlanningTests
import ProjectDependencyGraphTests
import ProjectDiscoveryTests
import ProjectLockFileTests
import ProjectSourceEnumerationTests
import ProjectStitchingTests
Unit
|> ProjectDiscoveryTests.runProjectDiscoveryTests
|> ProjectSourceEnumerationTests.runProjectSourceEnumerationTests
|> ProjectCompilationPlanningTests.runProjectCompilationPlanningTests
|> ProjectStitchingTests.runProjectStitchingTests
|> (given (_) -> Ashes.IO.print("all self-hosted project discovery, source enumeration, compilation planning, and stitching tests passed"))
|> ProjectDependencyGraphTests.run
|> ProjectLockFileTests.run
