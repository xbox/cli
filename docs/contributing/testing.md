---
title: Testing and coverage
description: Build and test the CLI, measure its code coverage against the project's bar, and run its end-to-end tests.
---

# Testing and coverage

Run the XBOX CLI's tests before you suggest a change, and keep its code coverage at
or above the [coverage bar](#coverage-bar). To set up a build, see
[Building from source](building-from-source.md).

## Build and test locally

From the repository root:

```bash
dotnet build
dotnet test
```

This runs the xUnit unit tests in `tests/XboxDevCLI.UnitTest/`, the [fuzz tests](fuzzing.md),
and the offline tests of the end-to-end harness in `tests/XboxDevCLI.EndToEndTests/`. The
harness's
[live tests](#live-end-to-end-tests) and the
[tests that run as your own account](#end-to-end-tests-with-your-own-account) skip until you
set them up.

Tests that redirect `Console.Out` or `Console.Error` must join the
`[Collection("ConsoleRedirect")]` xUnit collection, because console streams are shared by the
whole process and these tests can't run in parallel.

## Live end-to-end tests

The end-to-end harness runs a published CLI executable as a service principal against real
services. Its live tests skip unless `E2E_GITHUB_ENVIRONMENT` is set, and they're meant to run
in a GitHub Actions job bound to an environment that supplies these variables:

| Variable | Meaning |
|----------|---------|
| `E2E_GITHUB_ENVIRONMENT` | The environment's name. Setting it opts in to the live tests. |
| `E2E_CLI_PATH` | Directory holding the published `xbox` executable and `candidate-defaults.json`, a copy of the same commit's `src/XboxDevCLI/Resources/defaults.json`. |
| `E2E_AUTHENTICATION_MODE` | Optional. How the CLI signs in: `githuboidc`, the default, or `azurecli`, which uses an Azure CLI already signed in as the service principal. |
| `XBOX_AUTHENTICATION__CLIENTID` | The service principal's app ID. |
| `XBOX_AUTHENTICATION__TENANTID` | The service principal's tenant ID. |
| `XBOX_AZUREAD__SCOPES` | Optional. Overrides the CLI's built-in `AzureAd:Scopes`; the scope in effect must be the expected audience followed by `/.default`. |
| `E2E_EXPECTED_AUDIENCE` | The expected audience of the API access token, not of the OpenID Connect (OIDC) assertion. |
| `E2E_ALLOWED_HOST_SUFFIX` | One DNS domain suffix, such as `nodpublishers.com`, that both endpoints must be subdomains of. |
| `E2E_PLAYFAB_BASE_URL_TEMPLATE` | The PlayFab HTTPS origin, optionally containing `{0}` for the title ID. |
| `E2E_PLAYFAB_TITLE_ID` | The PlayFab title that holds the read-only fixture. |
| `E2E_XBOX_PARTNER_BASE_URL` | The HTTPS origin for commands such as `xbox game` and `xbox seller`, which the `XboxPartner:BaseUrl` setting holds. |
| `E2E_SELLER_ID` | The seller for the game-list scenario. |

In `githuboidc` mode, live jobs need the `id-token: write` permission. In `azurecli` mode,
`AZURE_CONFIG_DIR` must name the signed-in Azure CLI's configuration directory, because the
harness gives the CLI a temporary home directory; `auth status` must still report the
configured client and tenant IDs. When the live tests are opted in, missing or
inconsistent configuration fails the run rather than skipping it. The harness checks the
variables before it signs in, and passes them to the CLI as `XBOX_` variables; it doesn't pass
on other `XBOX_*` or `E2E_*` variables from your environment, and it turns off telemetry.

Two PlayFab scenarios run, `xbox playfab api /Admin/GetTitleData` and
`xbox playfab title-data get foo`. Both read `E2E_PLAYFAB_TITLE_ID`, so before a run, that title
needs:

- Title data with key `foo` and string value `bar`.
- A grant of title **Admin** to the environment's service principal, made by a studio
  administrator with
  [`xbox playfab service-principal set`](../commands/playfab/service-principal.md). Grant
  nothing at studio level and no other title.

The `seller list` and `game list` scenarios are turned off in code, because the service those
commands call doesn't accept service principals yet.

The environment's configuration decides which endpoints and identity the tests use, so
protect access to it and to the jobs that use its identity.

### Run in your own fork

1. Create a GitHub environment in your fork, such as `e2e-nod-arena`. Set its deployment
   branch restrictions and required reviewers before you give it access to an identity.
2. Follow [GitHub Actions authentication setup](../authentication.md#set-up-github-actions)
   to set up a Microsoft Entra ID app registration and a federated identity credential for
   your fork's environment. Use the OIDC subject issued for your repository and environment,
   and grant only the API permissions the scenarios need.
3. Set every variable in the table except `E2E_GITHUB_ENVIRONMENT`, `E2E_CLI_PATH`, and
   `E2E_AUTHENTICATION_MODE` as environment **variables**. Only `XBOX_AZUREAD__SCOPES` is
   optional, even though the `seller list` and `game list` scenarios are turned off. Each
   endpoint must be an HTTPS origin with no path, trailing slash, credentials, query, fragment,
   or nondefault port. Prepare the title's `foo` fixture and the service principal's grant as
   described earlier.
4. Copy the [manual workflow example](examples/e2e.yml) to `.github/workflows/e2e.yml`
   in your fork, and commit it to your default branch so GitHub enables manual dispatch.
5. In **Actions**, select **E2E (manual)**, choose a trusted branch, and enter the exact
   environment name. Approve the deployment if your environment requires it.

The workflow builds the CLI and harness in a job without the identity, and the job bound to
the environment runs the live tests on that build. Approving a run trusts both the harness and
the CLI executable with the environment's identity, so review both before you approve it.

## End-to-end tests with your own account

The end-to-end tests in `tests/XboxDevCLI.E2ETest/` run an `xbox` executable against production
Partner Center and XBOX services, as the user account it's signed in with rather than a service
principal. By default, they run only commands that read or that the service refuses, and
`xbl config save` and `xbl config publish` with `--validate-only` only where XBOX services store
nothing: a save of documents just downloaded, unchanged, or of one that can't be parsed, and a
publish of a sandbox to itself, for a game with XBOX services, or of the account;
[tests that write](#tests-that-write) are opt-in. Until their settings are set, `dotnet test` skips
them.

To run them with your own account:

1. Build the CLI, and sign in with the executable you'll test. In bash or zsh:

   ```bash
   dotnet build
   export XBOXDEVCLI_E2E_CLI="$PWD/src/XboxDevCLI/bin/Debug/net10.0/xbox"
   "$XBOXDEVCLI_E2E_CLI" auth login
   ```

   In PowerShell:

   ```powershell
   dotnet build
   $env:XBOXDEVCLI_E2E_CLI = "$PWD/src/XboxDevCLI/bin/Debug/net10.0/xbox.exe"
   & $env:XBOXDEVCLI_E2E_CLI auth login
   ```

   On macOS or Linux, leave off `.exe`.

2. Set the other `XBOXDEVCLI_E2E_*` environment variables that
   `tests/XboxDevCLI.E2ETest/E2ESettings.cs` lists. They name the account you signed in with; a
   seller, game, development sandbox, achievement image, playtest of the game, audience with
   members, and test account batch that the tests read, so each must already exist; and a keyword
   that, among the seller's games, only that game's name contains.
3. Run the tests:

   ```bash
   dotnet test tests/XboxDevCLI.E2ETest
   ```

Two of the tests read every page of the seller's games, so they take longer for a seller with many
games. To make missing settings fail the tests instead of skipping them, set
`XBOXDEVCLI_E2E_REQUIRED=true`.

`MsaTests` run as a personal Microsoft account (MSA) that's the only user of its seller, with the
settings that `tests/XboxDevCLI.E2ETest/MsaSettings.cs` lists. They also check that each kind of
command is refused for a seller the account doesn't belong to. Until their settings are set,
they're skipped, or fail with `XBOXDEVCLI_E2E_REQUIRED=true`. To leave them out:

```bash
dotnet test tests/XboxDevCLI.E2ETest --filter 'FullyQualifiedName!~MsaTests'
```

### Tests that write

Some tests write to production. They run only when you also set
`XBOXDEVCLI_E2E_WRITE=true`, and the account needs permission to change the game's playtests and
XBOX services configuration, and the seller's test accounts:

- One downloads the sandbox's documents and saves them unchanged. XBOX services store nothing for a
  save that changes nothing, and the test checks that.
- One creates a private draft playtest of the game, changes its description, and deletes it. The
  playtest is named `XboxDevCLI e2e lifecycle` and the UTC time it was made, has no audiences, and
  is never published. Before it creates one, the test deletes the game's drafts with that name that
  are more than an hour old, which a run that stopped early can leave, and fails if it can't delete
  one. If the test fails and can't delete its own playtest, its output says so; delete the playtest
  with [`xbox playtest delete`](../commands/playtest.md#deleting-a-playtest).
- One uploads a `json` file to the sandbox's global title storage, downloads it, checks that
  uploading it again without `--overwrite` fails, and deletes it. Players in the sandbox can read
  the file until then. It's named `xboxdevcli_e2e/lifecycle-`, the UTC time it was made, and
  `.json`. Like the playtest test, it first deletes such files more than an hour old, and fails if
  it can't. If it can't delete its own file, its output says so; delete the file with
  [`xbox xbl global-storage delete`](../commands/xbl/global-storage.md).
- One gives the test account batch a tag it already has, which changes nothing.

## Code coverage

Measure coverage with the repository's `coverage.runsettings`, which leaves out the generated
code under `src/XboxDevCLI/PlayFab/Generated/`:

```bash
dotnet test --collect:"XPlat Code Coverage" --settings coverage.runsettings
```

## Coverage bar

Changes keep coverage at or above these levels:

| Measure | Minimum |
|---------|---------|
| Overall project line coverage | 70% |
| Coverage of changed lines | 80% |

To check the lines you changed, run the unit tests with coverage, then compare the report
with `main` using [diff-cover](https://github.com/Bachmann1234/diff_cover):

In bash or zsh:

```bash
dotnet test tests/XboxDevCLI.UnitTest/XboxDevCLI.UnitTest.csproj --collect:"XPlat Code Coverage" --settings coverage.runsettings --results-directory TestResults
pip install diff-cover
diff-cover "$(ls -t $(find TestResults -name coverage.cobertura.xml) | head -n 1)" --compare-branch origin/main --fail-under 80
```

In PowerShell:

```powershell
dotnet test tests/XboxDevCLI.UnitTest/XboxDevCLI.UnitTest.csproj --collect:"XPlat Code Coverage" --settings coverage.runsettings --results-directory TestResults
pip install diff-cover
diff-cover (Get-ChildItem TestResults -Recurse -Filter coverage.cobertura.xml | Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName --compare-branch origin/main --fail-under 80
```

## Single-file publish safety

Releases are single-file executables, so don't use `Assembly.Location` to find files at run
time. It returns an empty string in a single-file app, and the publish fails on the IL3000
warning it raises. Use `AppContext.BaseDirectory` instead.
[Building from source](building-from-source.md#single-file-publishing) shows how to publish one.

## Fuzz testing

The property-based fuzz tests are in `tests/XboxDevCLI.FuzzTest/`. See
[Fuzz testing](fuzzing.md).
