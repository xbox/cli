---
title: Audience
description: List, get, create, and update the audiences of a Partner Center seller.
---

# Audience

An audience is a named group of players or devices, such as testers, that a seller can target
content to. Partner Center calls these customer groups. The `audience` commands list and get a
seller's audiences, create known-user audiences, and rename one or replace its members. To let an
audience join a playtest, pass its `id` to `playtest create` or `playtest update`; see
[Choosing audiences](playtest.md#choosing-audiences).

`audience` is Experimental: its commands, options, and output might change or be removed. See
[Experimental commands](../cli-command-lifecycle.md). Listing and getting audiences needs Partner
Center's customer groups Read permission for the seller, and creating and updating them needs
Write.

## Commands

| Command | Description |
| --- | --- |
| `audience list` | List the seller's audiences. |
| `audience get <audience-id>` | Get one audience, with its members. |
| `audience create` | Create a known-user audience. |
| `audience update <audience-id>` | Rename a known-user audience or replace its complete member list. |

The CLI can't delete an audience or change its type; do that in Partner Center.

## Arguments and options

| Argument or option | Applies to | Required | Description |
| --- | --- | --- | --- |
| `<audience-id>` | `get`, `update` | Yes | Audience ID, exactly as `list` returns it, such as `1152921504606846976`. |
| `--name <text>` | `list` | No | List only audiences whose name contains this text, ignoring case. |
| `--name <text>` | `create`, `update` | `create` | Name of the audience, up to 200 characters. It can't be blank, and doesn't have to be unique. |
| `--members-only` | `get` | No | Write only the members, one per line, in the [members file](#members-files) format. Can't be combined with `--output`. |
| `--members-file <path>` | `create`, `update` | No | File listing the members. See [Members files](#members-files). |
| `--confirm` (`--yes`, `-y`) | `create`, `update` | No | Skip the confirmation prompt. Without it, the command asks in a terminal and fails otherwise. |
| `--dry-run` | `update` | No | Show the changes without making them. See [Previewing an update](#previewing-an-update). |
| `--seller-id <id>` | all | If not saved | Partner Center seller ID. See [Context](context.md). |
| `--base-url <url>` | all | No | Base URL for requests, overriding the `XboxPartner:BaseUrl` configuration key. |
| `--output <format>` | all | No | `json` (default), `json-compact`, or `table`. |

## Output

`audience get` writes one audience:

```json
{
  "name": "Nod Arena testers",
  "id": "1152921504606846976",
  "members": ["dev@nodpublishers.com"],
  "dnaTags": ["AppFlighting"],
  "sellerId": "1234567",
  "groupType": "User",
  "groupUsage": "ForPackageFlight, ForLimitedDistribution"
}
```

| Field | Description |
| --- | --- |
| `name` | The audience name, or `null`. |
| `id` | The audience ID. Pass it to other commands unchanged. |
| `members` | The stored members. `list` leaves this field out. |
| `dnaTags` | The audience's tags. |
| `sellerId` | The seller that owns the audience. |
| `groupType` | The audience type, such as `User`, `PreCreated`, or `XboxDevice`, or `null`. Only `User` audiences can be updated. |
| `groupUsage` | What the audience is used for, as comma-separated flags, or `null`. |

`audience list` writes an `audiences` array of the same objects, without `members`, and
`{"audiences":[]}` when there are none. `create` and `update` write the audience as `get` does.
`update --dry-run` writes the changes instead; see [Previewing an update](#previewing-an-update).

With `--output table`, every command shows `ID`, `NAME`, and `TYPE` columns, where `TYPE` is
`groupType`. `get`, `create`, and `update` add a row per member returned, with a `MEMBERS` column
holding the number of members returned and a `MEMBER` column holding the member:

```text
ID                   NAME               TYPE  MEMBERS  MEMBER
-------------------  -----------------  ----  -------  ---------------------
1152921504606846976  Nod Arena testers  User  2        dev@nodpublishers.com
1152921504606846976  Nod Arena testers  User  2        qa@nodpublishers.com
```

When the service returns no members, `get`, `create`, and `update` write one row, with `0` under
`MEMBERS` and `-` under `MEMBER`. The `list` table doesn't show members. `update --dry-run`
doesn't accept `--output table`.

`audience get --members-only` writes only the audience's members as text, one per line, and
nothing when it has none:

```text
dev@nodpublishers.com
tester@nodpublishers.com
```

## Creating an audience

```bash
xbox audience create --name "Nod Arena testers" --members-file testers.txt
```

Without `--members-file`, the audience has no members. Each run creates a new audience, even when
one with the same name exists.

## Updating an audience

Pass `--name`, `--members-file`, or both; what you leave out is kept. This renames an audience:

```bash
xbox audience update 1152921504606846976 --name "Nod Arena beta testers"
```

This replaces its members with the addresses in `testers.txt`:

```bash
xbox audience update 1152921504606846976 --members-file testers.txt
```

> [!WARNING]
> `--members-file` replaces the complete member list. Members not in the file are removed, and an
> empty file removes every member.

To add or remove one member:

1. Save the current members to `testers.txt`:

   ```bash
   xbox audience get 1152921504606846976 --members-only > testers.txt
   ```

2. Check that `testers.txt` lists the members you expect. If `get` failed, the file is empty, and
   passing it removes every member.
3. Add or remove lines in `testers.txt`.
4. Replace the members with the list in the file:

   ```bash
   xbox audience update 1152921504606846976 --members-file testers.txt
   ```

5. Check the result:

   ```bash
   xbox audience get 1152921504606846976 --members-only
   ```

The update isn't checked against other changes: if two people update an audience at once, the
last one wins.

### Previewing an update

In a terminal without `--confirm`, `update` reads the audience first and shows the rename and
the members it adds and removes before it asks:

```console
$ xbox audience update 1152921504606846976 --members-file testers.txt --seller-id 1234567
Changing audience 1152921504606846976 under seller 1234567:
  Replacing its 1 member with 2 members: 1 added, 0 removed.
    + tester@nodpublishers.com

Change this audience? [y/N]:
```

The prompt lists up to 10 added and 10 removed members. `--confirm` skips the preview along with
the question.

`--dry-run` reads the audience and writes the changes the update would make, without changing
the audience or asking:

```bash
xbox audience update 1152921504606846976 --name "Nod Arena beta testers" --members-file testers.txt --dry-run
```

```json
{
  "id": "1152921504606846976",
  "name": {
    "current": "Nod Arena testers",
    "new": "Nod Arena beta testers"
  },
  "members": {
    "currentCount": 1,
    "newCount": 2,
    "added": ["tester@nodpublishers.com"],
    "removed": []
  }
}
```

`name` appears only with `--name`, and `members` only with `--members-file`. `added` and
`removed` list every change. A dry run exits `0`, but the service doesn't check the update, so it
can still be rejected.

## Members files

A members file is UTF-8 or UTF-16 text with one email address per line, up to 1000 members:

```text
dev@nodpublishers.com
tester@nodpublishers.com
```

Blank lines and spaces around each line are ignored. A member listed more than once, even with
different capitalization, is sent once, as first written. Each line must be a plain email address,
without a name, angle brackets, or invisible characters. If a line isn't, `create` and `update`
send nothing and name the lines to fix, counting blank lines. For example, this file:

```text
dev@nodpublishers.com

Dev Lead <lead@nodpublishers.com>
```

fails with this error:

```text
The members file 'testers.txt' has lines to fix, so nothing was sent:
  line 3: not a plain email address
```

The service checks each member too, but a stored member isn't proof that it resolves to an account
that can see content targeted to the audience.

## Exit codes

| Exit code | Meaning |
| --- | --- |
| `0` | The command succeeded. |
| `1` | The command failed, and nothing changed. |
| `3` | `create` or `update` sent its request, but its outcome is unknown. Stderr has the correlation ID when the request reached the service. |

`create` and `update` send their request once. After exit `3`, check before you run the command
again:

- After `create`, run the `xbox audience list --name` command that stderr shows. A match is only a
  hint, because names don't have to be unique, and running `create` again can create a second
  audience.
- After `update`, run `xbox audience get <audience-id>` to check its name and members.

## Errors

Errors go to stderr as text, whatever `--output` is, with the correlation ID, guidance for the
HTTP status, and the service's error detail when there is one. `--verbose` adds the service's
response, which can include members. The wording might change between releases.

A 403 can mean a missing permission, that you don't belong to the seller, or that playtests
aren't enabled for it. On `list` and `get`, and when `update` reads the audience to show its
changes, a refusal can instead appear as a 502; check the error detail for the cause. Creating and
updating audiences is rolling out and might not be available to you yet; until it is, `create`
and `update` fail without changing anything.

## Examples

List and get audiences:

```bash
xbox audience list --name testers
xbox audience list --output table
xbox audience get 1152921504606846976 --seller-id 1234567
xbox audience get 1152921504606846976 --members-only
```

Create an audience without the confirmation prompt:

```bash
xbox audience create --name "Nod Arena testers" --members-file testers.txt --confirm
```

Preview replacing an audience's members:

```bash
xbox audience update 1152921504606846976 --members-file testers.txt --dry-run
```

Replace an audience's members and show the result as a table:

```bash
xbox audience update 1152921504606846976 --members-file testers.txt --output table
```
