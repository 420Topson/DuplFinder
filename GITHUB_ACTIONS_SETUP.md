# GitHub Actions setup

This project contains `.github/workflows/dotnet.yml`.

Workflow steps:

1. checkout repository
2. setup .NET 8 SDK
3. restore
4. build Release
5. run Windows smoke test
6. publish self-contained single-file win-x64 EXE
7. upload artifact `DuplicateFinder-win-x64-single-exe`

Expected repo root layout:

```text
DuplicateFinder.csproj
Program.cs
Models/
Services/
Utils/
scripts/smoke-test.ps1
.github/workflows/dotnet.yml
```

If you keep the project in `src/DuplicateFinder.Cli`, update `PROJECT_PATH` and the smoke-test path in the workflow.
