---
title: Business partners
description: List, get, create, and rename a seller account's business partners, which own its relying parties and web services.
---

# Business partners

A business partner is an identity that owns a seller account's relying parties and web services.
Its type decides which claims its [relying parties](relying-party.md) can put in their tokens.
`xbox xbl business-partner` lists, gets, creates, and renames the business partners of a seller
account. Listing or getting them needs the Partner Center **Relying parties** Read or
**Business partners** Read permission, and creating or renaming one needs **Relying parties** Write
or **Business partners** Write.

Like every `xbl` command, these commands are Experimental: their options and output might change
or be removed. See [Experimental commands](../../cli-command-lifecycle.md).

| Command | Effect |
| --- | --- |
| `business-partner list` | List the account's business partners, the default one first. |
| `business-partner get <business-partner-id>` | Get one business partner. |
| `business-partner create` | Create a business partner in the account's draft. Asks first. |
| `business-partner save <business-partner-id>` | Rename a business partner in the account's draft. |

> [!WARNING]
> Nobody can delete a business partner or change its type, in the CLI or in Partner Center.

An account gets its default business partner with its first relying party, from
[`relying-party create`](relying-party.md). Until then, `list` returns none, and `create` fails.
`create` and `save` change the account's draft, which
[`xbl config publish --scope account`](../xbl.md#account-publishing) publishes.

## Options

| Option | Command | Required | Description |
| --- | --- | --- | --- |
| `<business-partner-id>` | `get`, `save` | Yes | The business partner's ID, as `list` shows it, such as `11111111-2222-3333-4444-555555555555`. |
| `--name <text>` | `create`, `save` | Yes | Its name. On `save`, its new name, the only setting a business partner can change. |
| `--type <type>` | `create` | No | Its type: one the account's business partners already have. From least to most privileged, the order Partner Center offers them in, the types are `Partner`, `LegacyPartner`, `LegacyAnonymized`, `Legacy`, and `Internal`. Defaults to the least privileged type the account's business partners have. |
| `--confirm` (`--yes`, `-y`) | `create` | No | Create without asking. |
| `--seller-id <id>` | all | No | Partner Center seller ID. Defaults as [Context](../context.md) describes, then to your only seller. |
| `--output <format>` | all | No | `json` (default), `json-compact`, or `table`. |

## Examples

List the account's business partners:

```bash
xbox xbl business-partner list --seller-id 1234567 --output table
```

Create a business partner for Nod Arena's services:

```bash
xbox xbl business-partner create --name "Nod Arena services" --seller-id 1234567
```

Rename it:

```bash
xbox xbl business-partner save 11111111-2222-3333-4444-555555555555 --name "Nod Arena backend" --seller-id 1234567
```

Then publish the account's configuration:

```bash
xbox xbl config publish --scope account --seller-id 1234567
```

## Output

`get`, `create`, and `save` write the business partner:

```json
{
  "businessPartnerId": "11111111-2222-3333-4444-555555555555",
  "name": "Nod Arena services",
  "type": "Partner",
  "isDefault": false
}
```

| Field | Value |
| --- | --- |
| `businessPartnerId` | Its ID. A default business partner without an ID of its own has the account's ID. |
| `name` | Its name, which is often empty for the default one. |
| `type` | Its type. |
| `isDefault` | Whether it's the account's default business partner, which `relying-party create` uses when you don't pass `--business-partner-id`. |

`list` writes them as an array, the default one first. They come from the
account's draft, so they include changes that aren't published yet. The table shows `ID`, `NAME`,
`TYPE`, and `DEFAULT`.

On stderr, `create` and `save` say how to publish the change:

```text
Saved to the account draft. Not published yet. Run 'xbox xbl config publish --scope account --seller-id 1234567' to publish it.
```

## Exit codes

| Exit code | Meaning |
| --- | --- |
| `0` | The command succeeded. |
| `1` | The command failed. For `create` and `save`, nothing changed: the request wasn't sent, or the service refused it. |
| `3` | `create` or `save` was sent, but its outcome is unknown. After `create`, check with the `business-partner list` command that stderr shows before you run it again: another `create` adds a second business partner. After `save`, running it again is safe. |

## Errors

A refused request exits `1` and writes a JSON error to stderr, as [Errors](../xbl.md#errors)
describes. These are the ones you're most likely to see:

| `code` | Cause |
| --- | --- |
| `business_partner_not_found` | The account has no business partner with that ID. `list` shows them. |
| `business_partner_change_not_allowed` | `--type` isn't a type the account's business partners already have. `detail` names them. |
| `no_business_partners` | `create` before the account has a business partner. Create its first relying party with [`relying-party create`](relying-party.md), which adds the default one. |
| `xbox_permission_denied` | You don't have the Partner Center permission the command needs: **Relying parties** Read or **Business partners** Read for `list` and `get`, and **Relying parties** Write or **Business partners** Write for `create` and `save`. |

`create` and `save` also fail with HTTP 409 when the account's business partners changed while they
ran. Run them again.
