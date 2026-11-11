# Contributing

- Build with `dotnet build AbpAi.slnx`.
- `dotnet test AbpAi.slnx` runs the unit tests and the host tests, which need PostgreSQL (see the README).
- Keep `AbpAi.Contracts` plain .NET, and no product's name in any package: the architecture test holds both, and
  `bash scripts/check-names.sh` (in CI, on the repository and on the packed packages) holds the name everywhere.
- A change to the tables comes with a migration in the sample (`dotnet ef migrations add` in `samples/AbpAi.SampleHost`),
  whose defaults keep what's there working as before.
- A change to a rule (aliases, limits, data rules) comes with a host test that shows it, and a line in CHANGELOG.md.
