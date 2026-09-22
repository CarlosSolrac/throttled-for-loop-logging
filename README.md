# throttled-logging-dotnet

Throttled `ILogger` library for .NET.

Instrument a long-running function — with or without an inner loop — through `ILogger`
dependency injection, and get entry/exit visibility plus live progress without emitting
one log line per iteration.

The public API is still being designed. See
[`docs/design/throttled-operation-logging.md`](docs/design/throttled-operation-logging.md).

## Layout

```
src/ThrottledLogging            the library      (net8.0; net10.0)
tests/ThrottledLogging.Tests    xUnit v3 tests   (net10.0)
docs/design                     design documents
```

## Getting started

Requires the **.NET 10 SDK** (see `global.json`).

```bash
dotnet restore
dotnet build
dotnet test
```

Tests run on [Microsoft.Testing.Platform](https://learn.microsoft.com/dotnet/core/testing/microsoft-testing-platform-intro),
which the .NET 10 SDK uses in place of VSTest; the opt-in lives in `global.json`.

To run a single test:

```bash
dotnet test --filter-method '*First_item_in_loop_is_always_logged*'
```

## Conventions

Set solution-wide in `Directory.Build.props` and `.editorconfig`:

- nullable reference types on, warnings as errors;
- XML documentation required on everything public (`CS1591` is an error);
- package versions centralised in `Directory.Packages.props`;
- 220-character line limit, file-scoped namespaces, 4-space indent.

## Licence

MIT — see [LICENSE](LICENSE).
