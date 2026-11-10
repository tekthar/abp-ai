# Contributing

- Build with `dotnet build AbpAi.slnx`.
- `dotnet test AbpAi.slnx` runs the unit tests and the host tests, which need PostgreSQL (see the README).
- Keep `AbpAi.Contracts` plain .NET, and no product's name in any package: the architecture test holds both.
- A change to a rule (aliases, limits, data rules) comes with a host test that shows it, and a line in CHANGELOG.md.
