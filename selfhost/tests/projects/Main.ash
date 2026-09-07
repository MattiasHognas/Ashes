import ProjectCompilationPlanningTests
import ProjectDependencyGraphTests
import ProjectDiscoveryTests
import ProjectLockFileTests
import ProjectSourceEnumerationTests
Unit
|> ProjectDiscoveryTests.runProjectDiscoveryTests
|> ProjectSourceEnumerationTests.runProjectSourceEnumerationTests
|> ProjectCompilationPlanningTests.runProjectCompilationPlanningTests
|> (given (_) -> Ashes.IO.print("all self-hosted project discovery, source enumeration, and compilation planning tests passed"))
|> ProjectDependencyGraphTests.run
|> ProjectLockFileTests.run
