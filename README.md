## Using dotnet cli
1. Run unit test
``` bash
▶ dotnet test --collect:"XPlat Code Coverage"
```

2. Generate report
``` bash
dotnet tool install -g dotnet-reportgenerator-globaltool

reportgenerator \  -reports:"TestResults/**/coverage.cobertura.xml" \
  -targetdir:"coveragereport" \
  -reporttypes:Html
```
