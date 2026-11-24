---
title: Bindings
description: Get, create, and delete the binding that makes an XBOX game use another game's XBOX services configuration.
---

# Bindings

A binding makes one game, the secondary game, use the XBOX services configuration of another
game, the primary game. `xbox xbl binding` gets, creates, and deletes the binding of the game that
`--store-id` names. For how binding works and which games you can bind, see
[Game Binding](https://docs.xbox.com/services/xbox-services/fundamentals/game-binding/game-binding-overview)
in the XBOX developer documentation, which also recommends that a binding include at most one
Windows PC game.

Like every `xbl` command, these commands are Experimental: their options and output might change
or be removed. See [Experimental commands](../../cli-command-lifecycle.md).

| Command | Effect |
| --- | --- |
| `binding get` | Get a game's binding. |
| `binding create` | Bind a game to a primary game, and wait until it's bound. |
| `binding delete` | Unbind a game, and wait until it's unbound. |

> [!WARNING]
> Once a game is bound, its builds must use the primary game's XBOX title ID: `binding create`
> turns off the game's own XBOX services configuration and gives the game the primary game's title
> ID. `binding delete` restores the game's own configuration and title ID, so its builds must use
> its own title ID again.

> [!WARNING]
> Before you bind a game, check both games for title-wide changes that were saved but not
> published, such as an embargo in `productdeveloper.xml`. `binding create` publishes both games'
> title-wide configuration, as a [title-wide publish](../xbl.md#title-wide-publishing) does, so those
> changes go live in every sandbox, RETAIL included, and unbinding doesn't undo that.
> `binding delete` publishes the game's own title-wide configuration the same way.

`create` and `delete` say what they change and publish, and ask before they change anything.
Without a terminal, they refuse unless you pass `--confirm` (`--yes`, `-y`). See
[Safety and destructive operations](../../safety.md).

To check both games before `create`, get the game's own XBOX title ID, the `titleId` that
`binding get` writes:

```bash
xbox xbl binding get --store-id 9N0000000000
```

Then see what a title-wide publish would change for each game, which
[`config status`](../xbl.md#config-list-and-config-status) lists. Pass that `titleId`, then the
primary game's XBOX title ID:

```bash
xbox xbl config status --scope title --title-id 12345678 --output table
xbox xbl config status --scope title --title-id 1234ABCD --output table
```

## Options

| Option | Command | Required | Description |
| --- | --- | --- | --- |
| `--store-id <id>` | all | Yes | The secondary game's Store ID, such as `9N0000000000`. `get` also takes a primary game's. |
| `--primary-title-id <id>` | `create` | Yes | The primary game's XBOX title ID, not its PlayFab title ID, such as `1234ABCD`. |
| `--no-wait` | `create`, `delete` | No | Return as soon as the bind or unbind starts. Can't be combined with `--timeout`. |
| `--timeout <duration>` | `create`, `delete` | No | How long to wait, such as `90s`, `10m`, or `2h`. Defaults to `5m`; at most `7d`. |
| `--confirm` (`--yes`, `-y`) | `create`, `delete` | No | Bind or unbind without asking first. |
| `--seller-id <id>` | all | No | Partner Center seller ID. Defaults as [Context](../context.md) describes, then to your only seller. |
| `--output <format>` | all | No | `json` (default), `json-compact`, or `table`. |

## Examples

Check whether a game is bound:

```bash
xbox xbl binding get --store-id 9N0000000000 --output table
```

Bind the game to the game whose XBOX title ID is `1234ABCD`, and wait until it's bound:

```bash
xbox xbl binding create --store-id 9N0000000000 --primary-title-id 1234ABCD
```

Unbind the game, and wait until it's unbound:

```bash
xbox xbl binding delete --store-id 9N0000000000
```

## Output

Each command writes the game's binding. A bound game's looks like this:

```json
{
  "storeId": "9N0000000000",
  "titleId": "12345678",
  "primaryTitleId": "1234ABCD",
  "boundStoreIds": [],
  "status": "Success",
  "statusMessage": null
}
```

| Field | Value |
| --- | --- |
| `storeId` | The game's Store ID. |
| `titleId` | The game's own XBOX title ID. |
| `primaryTitleId` | The primary game's XBOX title ID, or `null` when the game isn't bound. |
| `boundStoreIds` | The Store IDs of the games bound to this one, when it's a primary game. |
| `status` | The last bind or unbind: `Running`, `Success`, or `Failed`, or `null` when the game isn't bound. |
| `statusMessage` | Why the last bind or unbind failed. |

The table shows `STORE ID`, `TITLE ID`, `PRIMARY TITLE ID`, `STATUS`, and `BOUND GAMES`, the
Store IDs of the games bound to a primary game.

## Waiting for a bind or unbind

`create` waits until the bind finishes, when `status` isn't `Running`. `delete` waits until the
game shows unbound, when `primaryTitleId` is `null`. XBOX services finish the unbind after that,
and until they do, they refuse to bind the game. That refusal changes nothing, so wait and run
`create` again. A failure in that last part of the unbind isn't reported. Both commands then write
the binding. Ctrl+C stops waiting, but the bind or unbind continues. With `--no-wait`, the command
writes the binding as soon as the bind or unbind starts; check it later with `binding get`.

| Exit code | Meaning |
| --- | --- |
| `0` | The game is bound or unbound. With `--no-wait`, the bind or unbind started. |
| `1` | Nothing changed: you didn't confirm, the request wasn't sent, or the service refused it. |
| `2` | The bind or unbind failed. `statusMessage` says why. For `create`, it can also mean that the game isn't bound to the primary game when the wait ends, maybe because of another change to its binding. Check it with the `binding get` command that stderr shows before you run `create` again. |
| `3` | The request was sent, but its outcome is unknown. Before you run the command again, check the game with the `binding get` command that stderr shows. |
| `4` | The bind or unbind started, but the wait ended before it finished. Check it with the `binding get` command that stderr shows. |

## Errors

A refused request exits `1` and writes a JSON error to stderr, as [Errors](../xbl.md#errors)
describes. Its `detail` says why. The service refuses to bind a game to itself, to bind a game
that other games are bound to, and to bind a game that's already bound to a different primary
game; unbind it first. It also refuses games that binding doesn't support, such as games without
XBOX services or games in different Partner Center accounts, and a bind or unbind while another
is running.
