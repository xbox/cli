---
title: Store listing
description: Get the product name, description, and product features of a game's Store listing in Partner Center, one language at a time.
---

# Store listing

A game's Store listing is the text and images the Microsoft Store shows on the game's product
page, which you write in Partner Center for each language. `store-listing get` reads one
language's text: the product name, the description, and the product features.

The command reads the listing on the game's Main branch as it is in Partner Center now. The
Microsoft Store shows your changes only after you publish a submission, so the two can differ.

The command reads a game of one seller, from `--seller-id` or your saved seller; see
[Context](context.md). If you belong to several sellers, see
[this known issue](../known-issues.md#xbox-game-commands-ignore---seller-id): Partner Center reads
the game from the seller you last selected there.

The command is Experimental: its options and output might change. See
[Experimental commands](../cli-command-lifecycle.md).

## Commands

| Command | Description |
| --- | --- |
| `store-listing get` | Get a game's Store listing text in one language. |

## Options

| Option | Required | Description |
| --- | --- | --- |
| `--store-id <id>` | One of the two | The game's Store ID, such as `9N0000000000`. |
| `--xbox-title-id <id>` | One of the two | The game's XBOX title ID, not its PlayFab title ID, such as `1234ABCD`. If it matches several games, the error lists their Store IDs to pass instead. |
| `--language <code>` | Yes | The listing's language code, such as `en-us` or `zh-hans-cn`, in any case. It must match the listing's code exactly: `en` doesn't read an `en-us` listing, nor the reverse. |
| `--seller-id <id>` | If not saved | Partner Center seller ID. See [Context](context.md). |
| `--base-url <url>` | No | Base URL for requests, overriding the `XboxPartner:BaseUrl` configuration key. |
| `--output <format>` | No | `json` (default) or `json-compact`. The command refuses `table`. |

## Reading a listing

```bash
xbox store-listing get --store-id 9N0000000000 --language en-us
```

The command writes the listing as a JSON object:

```json
{
  "storeId": "9N0000000000",
  "language": "en-us",
  "title": "Nod Arena",
  "description": "Race your friends around the arena.",
  "features": [
    "Up to eight players online",
    "Twelve tracks"
  ]
}
```

`title` is the product name, and `features` lists the product features in order.

Text that isn't set is empty: `""`, or `[]` for `features`. The command still succeeds, and names
the empty fields on stderr:

```text
Not set in the en-us listing: description, features.
```

A game with no listing in the language fails instead; see [Errors](#errors).

## Errors

Failures go to stderr with the correlation ID and exit `1`. A failure the service returns includes
its HTTP status, and `--verbose` adds the service's response.

| Status | Meaning |
| --- | --- |
| 404 | With `Game 9N0000000000 has no Store listing in en-us.`, the game has no listing in that language code; check that `--language` matches the listing's code exactly. Otherwise, Partner Center has no game with that Store ID in your seller: check `--store-id`, and if you belong to several sellers, see [this known issue](../known-issues.md#xbox-game-commands-ignore---seller-id). |
| 422 | The game doesn't use Partner Center's [modular publishing](https://docs.xbox.com/publishing/game-publishing/concepts/modular-submission), so the command can't read its listing. Read it in Partner Center. |
| 502 | The listing couldn't be read. Partner Center fails this way for a language code it doesn't support, so check `--language`. Otherwise, try again later. |

For other failures, the command shows the service's message.
