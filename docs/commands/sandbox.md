---
title: Sandbox
description: List, get, and create the XBOX sandboxes available to the signed-in developer.
---

# Sandbox

An XBOX sandbox is an isolated environment in a Partner Center account, for Microsoft Store
products and XBOX services. Global sandboxes such as `RETAIL` are managed by Microsoft;
development sandboxes such as `ABCDEF.1` belong to your account. See
[Sandboxes](https://learn.microsoft.com/gaming/game-publishing/concepts/sandboxes) in the Game
Publishing Guide. To switch the sandbox a Windows PC uses, see [PC](pc.md).

Each command acts on one seller, from `--seller-id` or your saved seller; see
[Context](context.md). If that isn't the seller you last selected in Partner Center, see
[this known issue](../known-issues.md#xbox-sandbox-commands-show-only-global-sandboxes).

## Commands

| Command | Description |
| --- | --- |
| `sandbox list` | List the seller's sandboxes and the global sandboxes, as Partner Center's Sandboxes page shows them. |
| `sandbox get <sandbox-id>` | Get one sandbox by ID or [number](#sandbox-numbers), including a global or Isolated one: its ID, type, owning seller, and whether it's global. |
| `sandbox create <sandbox-id>` | Create a Shared development sandbox, then wait for its creation job. With `--free-number` instead of an ID, it uses the seller's [lowest free number](#choosing-a-sandbox-number). |

## Arguments and options

| Argument or option | Applies to | Required | Description |
| --- | --- | --- | --- |
| `<sandbox-id>` | `get`, `create` | Yes, unless `create` has `--free-number` | Sandbox ID, such as `ABCDEF.1`, or sandbox number, such as `1`. Case doesn't matter. See [Sandbox IDs](#sandbox-ids). |
| `--free-number` | `create` | No | Use the seller's [lowest free sandbox number](#choosing-a-sandbox-number) instead of a sandbox ID. |
| `--owned` | `list` | No | List only the seller's own sandboxes, leaving out global sandboxes such as `RETAIL`. |
| `--type <type>` | `list` | No | List only `shared` or `isolated` sandboxes. |
| `--seller-id <id>` | all | If not saved | Partner Center seller ID. |
| `--base-url <url>` | all | No | Base URL for requests, overriding the `XboxPartner:BaseUrl` configuration key. |
| `--confirm`, `--yes`, `-y` | `create` | No | Create the sandbox without the confirmation prompt. |
| `--output <format>` | all | No | `json` (default) or `json-compact`; `table` on `list` and `get` only. |

Pass a `sandboxId` from `list` to other commands, such as the [`xbl`](xbl.md) commands'
`--sandbox-id` option.

### Sandbox IDs

A sandbox ID is letters and digits with an optional period-separated suffix, up to 64
characters. The CLI converts it to upper case.

- `get` accepts any sandbox ID, including global ones such as `RETAIL` and `CERT.DEBUG`.
- `create` needs 4 to 60 letters or digits, a period, and a number from `0` to `999` without a
  leading zero, such as `ABCDEF.1`. The suffix `.58` is reserved for Isolated sandboxes, and
  `UNDEFINED` and the global sandbox names can't be used.

### Sandbox numbers

A seller's sandbox IDs are the seller's account moniker followed by a number, such as
`ABCDEF.1`. `get` and `create` also accept the number alone: when the seller's moniker is
`ABCDEF`, `xbox sandbox get 1 --seller-id 1234567` reads `ABCDEF.1`.

- A number is `0` to `999` without a leading zero. `create` refuses `58`, which is reserved for
  Isolated sandboxes.
- The command reads the moniker of the seller it acts on, then checks the full ID as if you'd
  typed it. `create` writes the full ID to stderr and names it in the confirmation prompt; `get`
  writes it with `--verbose`.
- If the moniker can't be read, the command exits `1` without reading or creating a sandbox. When
  the request is refused because you aren't signed in or can't access the seller, check your
  sign-in and `--seller-id`. Otherwise, pass the full sandbox ID instead; `sandbox list` shows the
  seller's sandbox IDs, which start with its moniker.

### Choosing a sandbox number

With `--free-number`, `create` reads the seller's moniker, lists the seller's sandboxes, and uses
the lowest number from `0` to `999` that no listed sandbox has, skipping `58`. When `ABCDEF.0` and
`ABCDEF.2` exist, it creates `ABCDEF.1`. It writes the chosen ID to stderr and names it in the
confirmation prompt.

- If the moniker can't be read or the sandboxes can't be listed, the command exits `1` without
  creating a sandbox.
- Listing and creating aren't one step, so another create can take the number first. The
  command never tries another number itself.

## Output

`sandbox get` writes one sandbox:

```json
{ "sandboxId": "ABCDEF.58", "type": "Isolated", "sellerId": "1234567", "global": false }
```

| Field | Description |
| --- | --- |
| `sandboxId` | The sandbox ID. |
| `type` | `Shared`, `Isolated`, or `Unknown`. `create` makes only Shared sandboxes. |
| `sellerId` | The owning seller, or `null` for a global sandbox. |
| `global` | `true` for a global sandbox, such as `RETAIL`. |

`sandbox list` writes a `sandboxes` array of the same objects:

```json
{
  "sandboxes": [
    { "sandboxId": "RETAIL", "type": "Shared", "sellerId": null, "global": true },
    { "sandboxId": "ABCDEF.1", "type": "Shared", "sellerId": "1234567", "global": false }
  ]
}
```

`list --output table` has `SANDBOX`, `TYPE`, and `GLOBAL` columns, and `get --output table` has
`SANDBOX ID`, `TYPE`, and `SELLER ID`. `create` refuses `--output table`.

With `--owned` or `--type`, `list` keeps only the sandboxes that match, and with both, only those
that match both, in the same shape. When none match, `sandboxes` is empty and the exit code is `0`.

## Creating a sandbox

Neither the CLI nor Partner Center can delete a sandbox. If you belong to several sellers,
check [the known issue](../known-issues.md#xbox-sandbox-commands-show-only-global-sandboxes)
first: the sandbox can be created in another seller.

```bash
xbox sandbox create ABCDEF.1 --seller-id 1234567
```

`sandbox create` asks before it creates the sandbox; without a terminal, it needs `--confirm`.

The command sends the request once and waits up to two minutes for the creation job, retrying
brief failures reading the job, such as throttling or a dropped connection. When the
job succeeds, it writes the new sandbox in the same shape as `sandbox get`. The job ID,
correlation ID, and what to check next go to stderr. When the exit code is `3` or `4`, check with
`sandbox get <sandbox-id>`, and don't run the command again: the sandbox can still be created after
the command exits. When the exit code is `2` with `--free-number`, run the command again with the
sandbox ID it chose instead of `--free-number`, so it can't create a second sandbox.

| Exit code | Meaning |
| --- | --- |
| `0` | The sandbox was created. |
| `1` | The sandbox wasn't created: the input or confirmation was refused, the request wasn't sent, or the service rejected it. Fix the problem and run the command again. |
| `2` | The creation job failed, such as when the sandbox already exists. The job, with its `errors`, is written to stdout. |
| `3` | The request was sent, but its outcome is unknown. Check with `sandbox get`. |
| `4` | The job was accepted, but the wait ended before the sandbox was confirmed. Nothing is written to stdout. Check with `sandbox get`. |

## Errors

A failure writes text to stderr, whatever `--output` is: the correlation ID, guidance for the
HTTP status, and the service's error detail when there is one. `--verbose` adds the response
body. The wording might change between releases.

## Examples

List sandboxes, and get three of them:

```bash
xbox sandbox list --output table
xbox sandbox get ABCDEF.1
xbox sandbox get RETAIL
xbox sandbox get 1 --seller-id 1234567
```

List only your own Shared development sandboxes:

```bash
xbox sandbox list --owned --type shared --output table
```

Create a sandbox without the confirmation prompt:

```bash
xbox sandbox create ABCDEF.2 --seller-id 1234567 --confirm --output json-compact
```

Create sandbox number 3 in the seller's account, which is `ABCDEF.3` when the seller's moniker is
`ABCDEF`:

```bash
xbox sandbox create 3 --seller-id 1234567
```

Create a sandbox with the seller's lowest free number:

```bash
xbox sandbox create --free-number --seller-id 1234567
```
