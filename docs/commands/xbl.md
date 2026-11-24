---
title: XBOX services configuration
description: Manage XBOX services configuration, including its state, documents, publishing, schemas, achievement images, bindings, global title storage files, web services, relying parties, and business partners.
---

# XBOX services configuration

The `xbl` command family is Experimental. Its CLI interface might change
or be removed based on feedback.

`xbox xbl` manages XBOX services configuration — what's in each draft and what's
published, configuration documents, publishing, and configuration schemas — as well as
achievement images, the bindings that make one game use another game's configuration, global
title storage files, the web services your own services use to call XBOX services, and a seller
account's relying parties and business partners. To list the sandboxes configuration commands
target, use [`xbox sandbox list`](sandbox.md).

These commands work with the same configuration that Partner Center edits. For what each
XBOX services feature does and how to configure it, see
[Portal configuration overview](https://docs.xbox.com/services/xbox-services/fundamentals/portal-config/live-portal-config-overview)
in the XBOX developer documentation. [XBOX services settings](../xbox-services-settings.md) says
which document holds each title setting, access policy, privilege, and single sign-on endpoint,
and how each goes live.

| Group | Commands | Purpose |
| --- | --- | --- |
| `config` | `list`, `status`, `diff`, `history`, `download`, `save`, `publish` | See what's in each configuration draft and what's published, and who changed what, download and save configuration documents, and publish a draft. |
| `schema` | `list`, `get`, `download` | List configuration schema types and versions, get one version, or download XSDs. |
| [`achievement-image`](xbl/achievement-image.md) | `upload`, `get`, `list`, `download` | Register, inspect, or download achievement images. |
| [`binding`](xbl/binding.md) | `get`, `create`, `delete` | Get, create, or delete the binding that makes a game use another game's configuration. |
| [`global-storage`](xbl/global-storage.md) | `get`, `list`, `download`, `upload`, `delete` | See a sandbox's global title storage usage, and list, download, upload, or delete the files every player reads there. |
| [`web-service`](xbl/web-service.md) | `list`, `get`, `create`, `update`, `delete`, `certificate create` | List, get, create, update, or delete the web services your own services use to call XBOX services, and create their certificates. |
| [`relying-party`](xbl/relying-party.md) | `list`, `get`, `create`, `save` | List, get, create, or save the relying parties that set the tokens XBOX services issue for your web services. |
| [`business-partner`](xbl/business-partner.md) | `list`, `get`, `create`, `save` | List, get, create, or rename the business partners that own a seller account's relying parties and web services. |

## Shared behavior

### Scope

`--scope` picks which configuration a `config` command works on. It defaults to `sandbox`.

| Scope | Configuration | Needs |
| --- | --- | --- |
| `sandbox` | One sandbox's, which goes live when that sandbox publishes. | A title and `--sandbox-id` |
| `title` | The title-wide configuration every sandbox uses, such as the embargo in `productdeveloper.xml`. | A title |
| `account` | The seller account's, such as relying parties. | Only the seller |

Stats system (`dataplatformconfig.xml`), privilege (`titlepriv.xml`), and single sign-on
(`nsal.xml`) configuration are title-wide, but each sandbox takes them only when that sandbox
publishes. See [Title-wide publishing](#title-wide-publishing).

The title-wide draft is kept in the sandbox `ALL`, and goes live through a publish to `HIDDEN`.
`--scope title` picks the right one for each command, and `--sandbox-id ALL` or
`--sandbox-id HIDDEN` names one directly:

- `config save` and `config download --view draft` with `ALL`, and `config publish` and
  `config download --view published` with `HIDDEN`, do what `--scope title` does.
- `config status` and `config diff` with `ALL` or `HIDDEN` do what `--scope title` does. See
  [`config list` and `config status`](#config-list-and-config-status).
- `config history` works on a sandbox only: XBOX services keep no history of the title configuration.
- Other uses work on that sandbox as named. For example, XBOX services refuse a publish to `ALL`.

`--scope account` works only with `config diff`, `config download`, `config publish`, `config save`, and `config status`. In `config list`,
`--scope` shows only that scope's rows.

Commands with `--scope account` need a Partner Center permission for the account:

| Command | Permission |
| --- | --- |
| `config status`, `config diff`, `config download` | **Relying parties** or **Business partners**, Read or Write |
| `config save` | **Relying parties** Write, or **Business partners** Write to save only `businesspartner.xml` |
| `config publish` | **Relying parties** or **Business partners** Write, or Read with `--validate-only` |

Without it, the command fails with HTTP 403 and the code `xbox_permission_denied`, which names the
permissions that allow the request. `config list` shows the account only with the permission
`config status` needs.

### Seller and sandbox

Every command except the `schema` commands takes `--seller-id`. Without it, a command uses
`XBOX_SELLER_ID` or the seller saved with [`xbox context set`](context.md), and then your only
seller; if you have several, pass `--seller-id`.

With `--scope sandbox`, `config status`, `config diff`, `config history`, `config download`, `config save`, and
`config publish` take `--sandbox-id` from `XBOX_SANDBOX_ID` or the saved `sandbox-id` when you leave it
out. `config save` and `config publish` say on stderr which sandbox they filled in. The other scopes
don't take `--sandbox-id`. The `global-storage` commands take it the same way, and `upload` and
`delete` say which sandbox they filled in. `web-service certificate create` doesn't: without
`--sandbox-id`, its certificate works in every sandbox.

### Title targeting

`config list`, and `config status`, `config diff`, `config history`, `config download`, `config save`, and `config publish` with
`--scope sandbox` or `--scope title`, and the [`global-storage`](xbl/global-storage.md) commands,
take the title's service configuration identifier (SCID)
with `--scid`, or its XBOX title ID with `--title-id`, but not both. With neither, they use
`XBOX_TITLE_ID` or the saved `title-id`; see [Context](context.md). `--scid` ignores both.

- `--title-id` is hexadecimal, as [`game get`](game.md) shows it: `00000001` through
  `7FFFFFFF`, with an optional `0x` prefix. For other title IDs, pass the SCID.
- Reads of a title bound to another title's configuration use the primary title's SCID.
- `config save` and `config publish` by a bound title's ID are refused, even with
  `--validate-only` (`bound_title_requires_scid`). The error names the primary title and
  SCID; rerun with that `--scid`. So are `global-storage upload` and `delete`, whose error
  names the SCID to pass.
- Saving or publishing by `--title-id` to configuration that other titles are bound to
  changes it for all of them. At a terminal, the CLI lists the bound titles and asks first;
  without one, it fails. `--allow-shared-configuration` skips the question. A write by
  `--scid` doesn't ask.
- `config save` and `config publish` write the seller and SCID they used to stderr, and so do
  `global-storage upload` and `delete` by `--title-id`. Read commands do with `--verbose`.

These commands save a title ID, then use it to read, download, and validate that title's
configuration in XDKS.1. The last one names the title with `--title-id` instead:

```powershell
xbox context set title-id 1234ABCD
xbox xbl config list --output table
xbox xbl config status --sandbox-id XDKS.1
xbox xbl config download --sandbox-id XDKS.1 --view draft --dest xbox-config
xbox xbl config save --sandbox-id XDKS.1 --file "xbox-config/*.xml" --validate-only
xbox xbl config status --title-id 1234ABCD --sandbox-id XDKS.1
```

### Output

Every `xbl` command accepts `--output table` as well as `json` (the default) and
`json-compact`, except `global-storage download`, `upload`, and `delete`, which write files. Each
command's options table lists its table columns.

### Errors

A failed request writes an error to stderr: a JSON error when the service returns one, and text
otherwise. Other lines, such as the command to check with, can come before or after it. The exit
codes are in the tables below. A 429 error includes `retryAfterSeconds`; see
[Rate limits](../cli-for-agents.md#rate-limits).

When XBOX services validation rejects the configuration, the error lists each message under
`validation`:

```json
{
  "status": 409,
  "title": "Validation failed.",
  "detail": "The configuration failed XBOX services validation. See validation for each message. MS-CV: ...",
  "code": "validation_failed",
  "validation": [
    {
      "severity": "Error",
      "documentName": "achievements2017.xml",
      "message": "XblLimited products do not support achievements or stats2013/stats2015 configuration. Please remove the unsupported configuration. Document failing validation: achievements2017.xml."
    }
  ]
}
```

A save or publish that would break Partner Center's rules for the title's stats system fails with
HTTP 409 and one of these codes. See
[Stats and achievements systems](../xbox-services-settings.md#stats-and-achievements-systems).

| `code` | Cause |
| --- | --- |
| `stats_system_change_not_allowed` | `config save --scope title` set a stats system Partner Center doesn't offer the title. `detail` names the ones it can use. |
| `stats_system_locked` | RETAIL has published achievements, which locks the stats system, and the save or publish would change it. `detail` names RETAIL's stats system. |
| `stats_system_check_failed` | The service couldn't read RETAIL's published stats system, so it couldn't check the change. Try again later. |

`config save` and `config publish` without `--validate-only` say whether a failed change
could have been applied:

| Exit code | Meaning |
| --- | --- |
| 1 | Nothing was changed: the request was stopped or failed before it was sent, or was rejected with a `4xx` other than `408`. |
| 3 | The outcome is unknown: Ctrl+C, a timeout, or a lost connection after the request was sent, any other error status, or a success response the CLI can't read. |

On exit 3, don't resubmit. For a save, stderr prints a read-only command to check with:
`config download --view draft`, to compare with the files you saved. For a publish, it prints
`config status`, with `--scope title` for a title-wide publish, or `--scope account` for an account
publish, which is safe to run again; see [Account publishing](#account-publishing).

---

## config

Inspect, download, save, and publish XBOX services configuration for one SCID. Each
configuration set has a **draft**, which `config save` changes, and a **published** version, which
`config publish` updates with every document in the draft. Players and test accounts see only what's published.

There are three kinds of configuration set, called [scopes](#scope): a sandbox's, the title's,
and the seller account's.

| Command | Description |
| --- | --- |
| `config list` | List the title's configuration sets and whether each draft has changes that aren't published. |
| `config status` | Show what's in one configuration set's draft, what's published, and its last publish. |
| `config diff` | Compare the draft, the published version, any version, or local files. |
| `config history` | List a sandbox's saves, publishes, and copies, newest first, and what each changed. |
| `config download` | Download and extract a document set. |
| `config save` | Validate documents, or save them to a draft. |
| `config publish` | Validate one configuration set, or publish it and wait for the publish to finish. |

### `config list` and `config status`

`config status` compares each document in a configuration set's draft with its published copy, by
content, and says what's changed:

| State | Meaning | What publishing does |
| --- | --- | --- |
| `draft-only` | The draft has the document and the published version doesn't. | Adds it. |
| `changed` | The draft's copy differs from the published copy. | Changes it. |
| `same` | Both copies are the same. | Nothing. |
| `published-only` | The published version has the document and the draft doesn't. | Nothing: publishing never removes a published document. On its own, it isn't a change to publish: the publish reports `AlreadyPublished`. |

Version numbers don't say whether the draft and the published version are the same: each version
number is unique within a SCID, across every sandbox and across draft and published versions.

The title's documents are saved once, in the title draft (`ALL`), and go live in two ways: a
title-wide publish, to `HIDDEN`, makes `productdeveloper`, `productsystem`, and
`globalaccesspolicies` live in every sandbox, and each sandbox's own publish makes
`dataplatformconfig`, `titlepriv`, and `nsal` live in that sandbox. Title documents have
`"scope": "title"`.

| Command | Lists | Compares with what's published by |
| --- | --- | --- |
| `config status --sandbox-id XDKS.1` | The sandbox's own documents, and `dataplatformconfig`, `titlepriv`, and `nsal` | A publish to `XDKS.1`: every document listed |
| `config status --scope title`, or `--sandbox-id ALL` or `HIDDEN` | Every title document, with where it goes live | A publish to `HIDDEN`: `productdeveloper`, `productsystem`, and `globalaccesspolicies` only |

The title's status reads only the title. It doesn't say which sandboxes have published
`dataplatformconfig`, `titlepriv`, and `nsal`; run `config status --sandbox-id` for each sandbox
you need.

#### What each change is

For a sandbox, each changed document lists the saves since the last publish that changed it,
newest first, under `changes`: a description such as
`1 achievements added. Total gamerscore: 1000.`, the version the save
created, when it was saved, who saved it, and the save's message. `descriptionStatus` says
whether the list is complete:

| `descriptionStatus` | Meaning |
| --- | --- |
| `ready` | Every save since the last publish is described. |
| `pending` | The draft's latest save is less than five minutes old and isn't described yet. Run `config status` again shortly. |
| `unavailable` | There's no description of this change, the latest save still isn't described after five minutes, descriptions couldn't be read, or the history doesn't reach back to the last publish (after a copy from another sandbox, or more than 100 saves). `changes` then lists only the saves it covers, and stderr says why. |

Descriptions cover a sandbox's draft against its own published version, so title documents, and
the title and account scopes, are always `unavailable`. They also cover only some document types,
such as achievements, leaderboards, stats, localization, presence, multiplayer session templates,
and access policies; other types, such as `sets.xml` (Set definitions), are always `unavailable`. The document's
state is complete either way. When descriptions can't be read, `config status` still succeeds and says so on stderr.

After a publish of an older version (`config publish --configset`), the descriptions can leave
out saves that publish didn't include, and still say `ready`. See [Known issues](../known-issues.md#xbl-config-status-and-config-diff-can-leave-out-changes-after-some-publishes).

#### `config status` output

`config status` returns one JSON object. This excerpt shows one changed document; a real
response lists every document:

```json
{
  "scope": "sandbox",
  "scid": "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
  "sandbox": "XDKS.1",
  "draft": { "version": 55, "time": "2026-10-29T21:40:12+00:00", "user": "1000000001", "userName": "Nod Developer", "message": "Add Win a race" },
  "published": { "version": 52, "time": "2026-10-27T18:05:00+00:00", "user": "1000000002", "userName": "Lucerne Developer", "message": null },
  "documents": [
    {
      "name": "achievements2017.xml",
      "feature": "Achievements",
      "scope": "sandbox",
      "state": "changed",
      "draftHash": "9C1E8A0B7D6F5E4C3B2A19087F6E5D4C3B2A1908",
      "publishedHash": "0A2B4C6D8E0F1A2B3C4D5E6F7A8B9C0D1E2F3A4B",
      "descriptionStatus": "ready",
      "changes": [
        {
          "description": "1 achievements added. Total gamerscore: 1000.",
          "version": 55,
          "time": "2026-10-29T21:40:12+00:00",
          "author": { "id": "1000000001", "name": "Nod Developer" },
          "message": "Add Win a race"
        }
      ]
    }
  ],
  "lastPublish": { "status": "Success", "jobId": null, "message": null },
  "hasChanges": true
}
```

| Field | Meaning |
| --- | --- |
| `draft`, `published` | The latest draft and published versions: `version` and `time`, and, for a sandbox, `user` (the developer ID of who saved or published it), `userName` (their name), and the save's `message` when the change history has them. Null when never saved, or never published. |
| `titleDraft` | For a sandbox, the title draft's `version` and `time`: the sandbox's publish takes its `dataplatformconfig`, `titlepriv`, and `nsal`. Left out for the other scopes, and when the title draft was never saved. |
| `documents` | The documents the publish compares, or for the title, every title document. Sandbox documents come first, then title documents. `feature` names the feature the document configures, such as `Achievements` for `achievements2017.xml`, or repeats the document's name for a type the CLI doesn't know. `draftHash` is null for `published-only`, and `publishedHash` for `draft-only`. Documents that aren't `same` also have `descriptionStatus` and `changes`. |
| `lastPublish` | The latest publish: `status` (`Running`, `Success`, or `Failed`), `jobId`, and `message`. For the title scope, it's the latest title-wide publish, to `HIDDEN`. Null before the first publish, and always for the account scope. |
| `hasChanges` | Whether a compared document is `draft-only` or `changed`: whether there's anything to publish. |

In the title's status, each document also has `publishesWith`, which says where it goes live:
`HIDDEN` for a title-wide publish, or `sandbox` for each sandbox's own publish. The `HIDDEN`
documents come first, then the `sandbox` ones, each group sorted by `feature`. A `sandbox`
document isn't compared, so its `state` and `publishedHash` are null.

With `--output table`, stderr has the published and draft versions and the last publish, and
stdout has a row for each change that isn't published, with the columns `SCOPE`, `FEATURE`,
`STATE`, `CHANGE`, and `SAVED BY`. Documents that are the same are left out, and a document
whose changes aren't described has one row with `-` for its change:

```text
Published: version 52. Last publish: Success.
Draft: version 55. Title draft: version 60 (ALL).
2 documents differ for a publish to XDKS.1.
SCOPE    FEATURE                         STATE    CHANGE                                         SAVED BY
-------  ------------------------------  -------  ---------------------------------------------  -------------
sandbox  Achievements                    changed  1 achievements added. Total gamerscore: 1000.  Nod Developer
title    Stats and achievements systems  changed  -                                              -
```

`STATE` says `added` for `draft-only` and `not in draft` for `published-only`. The count of
documents that differ leaves out `published-only` documents, and when they're the only difference,
stderr says `Nothing to publish to <sandbox>.` In the title's table,
every title document has a row, and `CHANGE` says where it goes live:

```text
Published: version 58 (HIDDEN). Last publish: Success.
Draft: version 60 (ALL).
1 document differs for a publish to HIDDEN.
3 documents publish with each sandbox. Check them with config status --sandbox-id <sandbox>.
SCOPE  FEATURE                         STATE    CHANGE                       SAVED BY
-----  ------------------------------  -------  ---------------------------  --------
title  Business partner                same     Publishes through HIDDEN     -
title  Global access policies          same     Publishes through HIDDEN     -
title  Title names and platforms       changed  Publishes through HIDDEN     -
title  Privileges                      -        Publishes with each sandbox  -
title  Single sign-on endpoints        -        Publishes with each sandbox  -
title  Stats and achievements systems  -        Publishes with each sandbox  -
```

`config publish` waits for the publish to finish by default. After `--no-wait`, or when its wait
ends early, run `config status` until `lastPublish.status` is `Success` or `Failed`. The account
has no `lastPublish`; see [Account publishing](#account-publishing).

#### `config list` output

`config list` returns a JSON array with one object for each sandbox the developer can reach,
then the title, then the seller account. Each object is that set's `config status` without
its documents: `scope`, `sandbox` (null for the title and the account), `draft`, `published`,
`lastPublish`, and `hasChanges`. The title's `hasChanges` covers only what a publish to `HIDDEN`
makes live. With `--output table`:

```text
SCOPE    SANDBOX  CHANGES  DRAFT  PUBLISHED  PUBLISHED AT           LAST PUBLISH
-------  -------  -------  -----  ---------  ---------------------  ------------
sandbox  XDKS.1   Yes      55     52         2026-10-27 18:05:00 Z  Success
title             No       60     58         2026-10-20 12:00:00 Z  Success
account           No       7      6          2026-09-01 08:00:00 Z
```

`config list` reads the sandboxes, then each sandbox's last publish and the title's and the account's status. If a request fails, it prints nothing on stdout. The one exception is the account:
when the account's configuration can't be read (HTTP 403), such as without the
[permission](#scope) to read it, its row is left out and stderr
says so. When a request fails, including one that times out or loses its connection, stderr also gets the
`x-ms-correlation-id` of the request.

#### `config list` options

| Option | Required | Description |
| --- | --- | --- |
| `--scid <guid>` | Or title | SCID of the title's configuration. |
| `--title-id <hex-id>` | Or SCID | XBOX title ID. See [Title targeting](#title-targeting). |
| `--scope <sandbox\|title>` | No | Show only each sandbox's rows, or only the title's row. Defaults to every row, including the seller account's. |
| `--seller-id <id>` | No | Partner Center seller ID. |
| `--output <format>` | No | `json` (default), `json-compact`, or `table`. |

#### `config status` options

| Option | Required | Description |
| --- | --- | --- |
| `--scope <sandbox\|title\|account>` | No | [Scope](#scope). Defaults to `sandbox`. |
| `--scid <guid>` | Or title, for `sandbox` and `title` | SCID of the title's configuration. Not allowed with `--scope account`. |
| `--title-id <hex-id>` | Or SCID, for `sandbox` and `title` | XBOX title ID. See [Title targeting](#title-targeting). Not allowed with `--scope account`. |
| `--sandbox-id <id>` | For `sandbox` | Case-sensitive sandbox ID. Defaults to `XBOX_SANDBOX_ID`, then the saved sandbox ID. `ALL` and `HIDDEN` are the same as `--scope title`. For `HISTORY`, use `--scope title`. |
| `--seller-id <id>` | No | Partner Center seller ID. |
| `--output <format>` | No | `json` (default), `json-compact`, or `table`. |

#### `config list` and `config status` examples

```bash
# Which configuration sets have changes that aren't published
xbox xbl config list --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --output table

# What's in one sandbox's draft, and what's published
xbox xbl config status --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --sandbox-id XDKS.1 --output table

# Every title document, where each goes live, and what a publish to HIDDEN would change
xbox xbl config status --scope title --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --output table

# The seller account's configuration, as compact JSON
xbox xbl config status --scope account --output json-compact
```

### `config diff`

`config diff` compares two configuration states document by document, by content, like
`git diff`:

```text
xbox xbl config diff [<old-version> [<new-version>]] [--file <path>]... [--feature <name>]... [--lines]
```

| To compare | Run |
| --- | --- |
| The draft with the published version: what publishing would change | `config diff` |
| The draft with version 52 | `config diff 52` |
| Version 52 with version 55 | `config diff 52 55` |
| Your local files with the draft | `config diff --file "nod-arena/*.xml"` |
| Your local files with version 52 | `config diff 52 --file "nod-arena/*.xml"` |

The new side defaults to the draft, or your `--file` files. The old side defaults to the layer
below it: published under the draft, and the draft under your files. Line diffs mark old lines `-`
and new lines `+`. The exit code is 0 whenever the comparison runs.

Both sides are versions of one configuration set: the sandbox that `--sandbox-id` names, or the
title or account. A version number names one of that set's draft or published versions. To compare
another sandbox's versions, name it in both steps:

```bash
xbox xbl config status --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --sandbox-id CERT
xbox xbl config diff 52 --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --sandbox-id CERT
```

A version that isn't the set's exits 1 and says how to find its version numbers.

#### What's compared

Each side holds the documents [`config status`](#config-list-and-config-status) lists for its
scope. With `--file`, only the files' documents are compared, by file name, and a sandbox's
internal `xms` documents and title-wide documents, such as `productdeveloper`, are left out. With `--scope title`, `dataplatformconfig`, `titlepriv`, and `nsal` are left out, and so are a sandbox's documents among your files, such as `achievements2017.xml`. A sandbox's earlier draft versions don't hold the title
documents `dataplatformconfig`, `titlepriv`, and `nsal`, so those aren't compared against them.
`notCompared` lists what's left out, and stderr says why.

A document's `state` is `added` (only the new side has it), `removed` (only the old side has
it), `changed`, or `same`. Documents compare by their bytes. `--lines` reads each as UTF-8
unless it starts with a byte order mark, whatever its XML declaration says. When the bytes
differ but the lines are the same, such as in line endings or encoding, the line diff says so; a missing final line break is
marked `\ No newline at end of file`, as in `git diff`.

The draft compared with the published version also says what each change is, with `descriptionStatus` and
`changes`, as [`config status`](#what-each-change-is) does.

#### `config diff` output

`config diff` returns one JSON object. This excerpt, from `config diff 48 --lines`, shows one
changed document:

```json
{
  "scope": "sandbox",
  "scid": "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
  "old": { "view": "published", "sandbox": "XDKS.1", "version": 48 },
  "new": { "view": "draft", "sandbox": "XDKS.1", "version": 55, "userName": "Nod Developer" },
  "hasDifferences": true,
  "features": [
    {
      "name": "Achievements",
      "documents": [
        {
          "name": "achievements2017.xml",
          "scope": "sandbox",
          "state": "changed",
          "oldHash": "0A2B4C6D8E0F1A2B3C4D5E6F7A8B9C0D1E2F3A4B",
          "newHash": "9C1E8A0B7D6F5E4C3B2A19087F6E5D4C3B2A1908",
          "lines": ["--- achievements2017.xml (XDKS.1 published version 48)", "+++ achievements2017.xml (XDKS.1 draft version 55)", "@@ -1,3 +1,4 @@", " <Achievements>", "   <Achievement Id=\"1\" />", "+  <Achievement Id=\"7\" />", " </Achievements>"]
        }
      ]
    }
  ],
  "notCompared": []
}
```

| Field | Meaning |
| --- | --- |
| `old`, `new` | Each side's `view` (`draft`, `published`, or `files`), `sandbox` for the sandbox scope, and `version`. The current draft and published versions also have `time`, `user`, `userName`, and `message`, as in `config status`; local files have `files`, their paths. |
| `features` | The compared documents, by feature, such as `Achievements`. A document of a type the CLI doesn't know is named by its file name. |
| `documents` | Each document's `name`, `scope`, `state`, and `oldHash` and `newHash` (uppercase hex SHA-1 of its bytes, null on the side without it). With `--lines`, each document that differs has `lines`, its unified diff. |
| `notCompared` | Documents one side holds that the comparison leaves out, such as title documents against a sandbox's earlier draft; stderr says why. |

With `--output table`, stdout has a row for each change, or each differing document whose
changes aren't described, with the columns `SCOPE`, `FEATURE`, `DOCUMENT`, `STATE`, and
`CHANGE`, and stderr names both sides. With `--lines`, the line diffs follow the table, with
control characters removed.

```text
Old: XDKS.1 published version 52, published 2026-10-27 18:05:00 Z by Lucerne Developer.
New: XDKS.1 draft version 55, saved 2026-10-29 21:40:12 Z by Nod Developer.
2 documents differ.
1 document is the same.
SCOPE    FEATURE          DOCUMENT              STATE    CHANGE
-------  ---------------  --------------------  -------  ---------------------------------------------
sandbox  Achievements     achievements2017.xml  changed  1 achievements added. Total gamerscore: 1000.
sandbox  Set definitions  sets.xml              added
```

#### `config diff` options

| Option | Required | Description |
| --- | --- | --- |
| `<old-version>` | No | The old side's version number. Defaults to the published version, or to the draft with `--file`. |
| `<new-version>` | No | The new side's version number. Defaults to the draft. Can't be used with `--file`. |
| `--scope <sandbox\|title\|account>` | No | [Scope](#scope): which configuration set both sides are in. Defaults to `sandbox`. |
| `--scid <guid>` | Or title, for `sandbox` and `title` | SCID of the title's configuration. Not allowed with `--scope account`. |
| `--title-id <hex-id>` | Or SCID, for `sandbox` and `title` | XBOX title ID. See [Title targeting](#title-targeting). Not allowed with `--scope account`. |
| `--sandbox-id <id>` | For `sandbox` | Case-sensitive sandbox ID: the sandbox whose draft, published version, and numbered versions are compared. Defaults to `XBOX_SANDBOX_ID`, then the saved sandbox ID. `ALL` and `HIDDEN` are the same as `--scope title`. For `HISTORY`, use `--scope title`. |
| `--file <path>` | No | A local file or filename glob pattern, as for `config save`. Repeat for more. The files are the new side. |
| `--feature <name>` | No | Compare only this feature: its name as the output shows it, a document name, or a document type such as `achievements2017`. Ignores case; repeat for more. |
| `--lines` | No | Add each differing document's line diff. |
| `--seller-id <id>` | No | Partner Center seller ID. |
| `--output <format>` | No | `json` (default), `json-compact`, or `table`. |

#### Examples

```bash
# What publishing would change
xbox xbl config diff --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --sandbox-id XDKS.1 --output table

# Your edited files against the draft, with line diffs, before you save them
xbox xbl config diff --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --sandbox-id XDKS.1 --file "xbox-config/*.xml" --lines
```

### `config history`

`config history` lists a sandbox's saves, publishes, and copies, newest first: who made each one,
when, the save's message, and what it changed in each feature. It's the history Partner Center shows.

```text
xbox xbl config history --sandbox-id <id> [--action <save|publish|copy>] [--feature <name>]... [--limit <count>]
```

It reads every page of the history, unless `--limit` stops it sooner.

XBOX services keep a history of each sandbox's configuration only. `--scope title`, and
`--sandbox-id ALL`, `HIDDEN`, or `HISTORY`, fail and point to `config status --scope title`, which
shows the title configuration's state. `--scope account` isn't accepted.

XBOX services describe only some features: achievements, stat rules, leaderboards, featured stats,
localized strings, rich presence, multiplayer sessions, SmartMatch hoppers, custom invite strings,
title storage, Game DVR, tournaments, access policies, and certification exceptions. A save that
changed only other features, such as `sets.xml`, has an empty `documents`
list. XBOX services add a save to the history a few seconds after it. When the draft's latest save is
less than five minutes old and isn't in the history yet, stderr says so; run `config history` again shortly.

#### `config history` output

`config history` returns one JSON object. This excerpt shows two entries:

```json
{
  "scid": "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
  "sandbox": "XDKS.1",
  "entries": [
    {
      "action": "save",
      "version": 55,
      "sandbox": "XDKS.1",
      "time": "2026-10-29T21:40:12+00:00",
      "author": { "id": "1000000001", "name": "Nod Developer" },
      "message": "Add Win a race",
      "documents": [
        {
          "documentType": "achievements2017",
          "feature": "Achievements",
          "description": "1 achievements added. Total gamerscore: 1000."
        }
      ]
    },
    {
      "action": "publish",
      "version": 52,
      "sandbox": "XDKS.1",
      "time": "2026-10-27T18:05:00+00:00",
      "author": { "id": "1000000002", "name": "Lucerne Developer" },
      "message": null,
      "documents": [
        {
          "documentType": "achievements2017",
          "feature": "Achievements",
          "description": "12 achievements. Total gamerscore: 1000."
        }
      ]
    }
  ]
}
```

| Field | Meaning |
| --- | --- |
| `scid`, `sandbox` | The sandbox the history is of. |
| `entries` | Its saves, publishes, and copies, newest first by `version`. |
| `action` | `save`, `publish`, `copy` (another sandbox's draft copied into this one), or `other`. |
| `version` | The version it created: a draft version for a save or copy, the published version for a publish. |
| `sandbox` | The sandbox it changed. Null when the service can't tell, such as for some older saves. |
| `time` | When it happened, in UTC. |
| `author` | `id`, the Partner Center user ID of the developer who made it, or an XBOX service's ID, and `name`, the developer's name. `name` is null when it isn't known, as for an XBOX service. |
| `message` | The save's message. Null for publishes and copies, saves without one, and saves whose message the service can't read, such as some older saves. |
| `documents` | Each feature it describes: `documentType`, `feature`, and `description`. For a save, what the save changed; for a publish, what the feature held when it was published. Empty for a copy. |

With `--output table`, stdout has a row for each feature an entry describes, or one row for an
entry that describes none, with the columns `VERSION`, `ACTION`, `SANDBOX`, `TIME`, `AUTHOR`,
`FEATURE`, and `CHANGE`. Messages are only in the JSON.

```text
VERSION  ACTION   SANDBOX  TIME                   AUTHOR             FEATURE       CHANGE
-------  -------  -------  ---------------------  -----------------  ------------  ---------------------------------------------
55       save     XDKS.1   2026-10-29 21:40:12 Z  Nod Developer      Achievements  1 achievements added. Total gamerscore: 1000.
55       save     XDKS.1   2026-10-29 21:40:12 Z  Nod Developer      Leaderboards  Leaderboard FastestLap has been added.
54       save     XDKS.1   2026-10-28 10:00:00 Z  Nod Developer      Leaderboards  Leaderboard BestLap has been added.
52       publish  XDKS.1   2026-10-27 18:05:00 Z  Lucerne Developer  Achievements  12 achievements. Total gamerscore: 1000.
```

If a request fails, `config history` prints nothing on stdout.

#### `config history` options

| Option | Required | Description |
| --- | --- | --- |
| `--scid <guid>` | Or title | SCID of the title's configuration. |
| `--title-id <hex-id>` | Or SCID | XBOX title ID. See [Title targeting](#title-targeting). |
| `--sandbox-id <id>` | Yes | Case-sensitive sandbox ID. Defaults to `XBOX_SANDBOX_ID`, then the saved sandbox ID. `ALL`, `HIDDEN`, and `HISTORY` hold the title configuration, which has no history. |
| `--action <save\|publish\|copy>` | No | List only saves, publishes, or copies. Defaults to all of them. |
| `--feature <name>` | No | List only entries about this feature: its name as the output shows it, a document name, or a document type such as `achievements2017`. Ignores case; repeat for more. Entries keep only the matching features. A feature XBOX services don't describe matches nothing, and stderr says so. A publish matches when the feature is in what it published, even if the publish didn't change it. Partner Center's history of a feature lists only the saves that changed it; to list the same entries, add `--action save`. |
| `--limit <count>` | No | List at most this many of the newest matching entries. Defaults to every entry. |
| `--seller-id <id>` | No | Partner Center seller ID. |
| `--output <format>` | No | `json` (default), `json-compact`, or `table`. |

#### `config history` examples

```bash
# The last 10 saves, publishes, and copies in one sandbox
xbox xbl config history --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --sandbox-id XDKS.1 --limit 10 --output table

# Who changed achievements, and when
xbox xbl config history --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --sandbox-id XDKS.1 --action save --feature achievements2017 --output table

# Who published, and when
xbox xbl config history --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --sandbox-id XDKS.1 --action publish
```

To see exactly what a save changed, compare its version with the same sandbox's previous save or
copy. Skip publishes: a publish's version holds the draft it published, which can be older. In the
table above, that's save 54 before save 55:

```bash
xbox xbl config diff 54 55 --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --sandbox-id XDKS.1 --lines
```

### `config download` and `config save`

Download, validate, and save XBOX services configuration document sets. [`--scope`](#scope)
picks the set: a sandbox's (the default, which needs a SCID or title and `--sandbox-id`), the
title-wide set (`--scope title`, which needs a SCID or title), or the account's
(`--scope account`, which takes no SCID, title, or sandbox).

`config download` saves the document set's ETag next to the documents, as `<sandbox>.etag`,
`Title.etag` for the title-wide set, or `Account.etag` for the account's; on Windows the file
is hidden. `config save` reads the ETag file next to the first file `--file` matches, and
rewrites it after a save. If the configuration changed since you downloaded it, the save
fails; pass `--force` to save over those changes, or `--etag` to supply an ETag yourself.

`config save` doesn't ask before it saves, and `--confirm` doesn't apply to it. Run it with
`--validate-only` first.

#### `config download` options

| Option | Required | Description |
| --- | --- | --- |
| `--view <draft\|published>` | Yes | `draft`, the working configuration that `config save` changes, or `published`, the live configuration that players and test accounts see. Partner Center uses the same two names. |
| `--dest <directory>` | Yes | Directory where documents and the ETag file are saved. |
| `--scope <sandbox\|title\|account>` | No | [Scope](#scope). Defaults to `sandbox`. |
| `--scid <guid>` | Or title for `sandbox` and `title` | SCID of the title's configuration. |
| `--title-id <hex-id>` | Or SCID for `sandbox` and `title` | XBOX title ID. See [Title targeting](#title-targeting). |
| `--sandbox-id <id>` | For `sandbox` | Case-sensitive sandbox ID. |
| `--version <number>` | No | Positive historical document-set version. Defaults to the latest version. |
| `--document <filename>` | No | Document filename to download. Repeat for more. Defaults to all documents. |
| `--seller-id <id>` | No | Partner Center seller ID. |
| `--output <format>` | No | `json` (default), `json-compact`, or `table` with `SCOPE`, `SANDBOX`, `VIEW`, `VERSION`, `FILES`, and `DESTINATION` columns. |

#### `config save` options

| Option | Required | Description |
| --- | --- | --- |
| `--file <path-or-glob>` | Yes | Local file or quoted filename glob. Repeat `--file` for more, one path or pattern each: `--file a.xml --file b.xml`. Each file's name is its document name. A value that matches no file fails the command. |
| `--scope <sandbox\|title\|account>` | No | [Scope](#scope). Defaults to `sandbox`. |
| `--scid <guid>` | Or title for `sandbox` and `title` | SCID of the title's configuration. |
| `--title-id <hex-id>` | Or SCID for `sandbox` and `title` | XBOX title ID. See [Title targeting](#title-targeting). |
| `--sandbox-id <id>` | For `sandbox` | Case-sensitive sandbox ID. |
| `--validate-only` | No | Validate without saving. |
| `--allow-shared-configuration` | No | Save by `--title-id` to configuration other titles are bound to, without asking. See [Title targeting](#title-targeting). |
| `--etag <value>` | No | ETag to save against. Defaults to the saved ETag file. |
| `--force` | No | Save over changes made since the saved ETag. |
| `--message <text>` | No | Save message. |
| `--seller-id <id>` | No | Partner Center seller ID. |
| `--output <format>` | No | `json` (default), `json-compact`, or `table` with `SCOPE`, `SANDBOX`, `VALIDATE ONLY`, `CAN COMMIT`, `COMMITTED`, `FILES`, and `ETAG FILE` columns. |

A save puts the documents in the draft. Players and test accounts see them only after
[`config publish`](#config-publish). After a save, stderr says where the documents went and
what to run next:

```text
Saved to the XDKS.1 draft. Not published yet. Run 'xbox xbl config publish --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --sandbox-id XDKS.1' to publish it.
```

The suggested command keeps the save's seller and its sign-in and profile options, such as
`--profile`. A save to `ALL` points to `config status --scope title`, which lists every title
document and where it goes live, and says to check `dataplatformconfig`, `titlepriv`, and `nsal` in
each sandbox, because each sandbox's own publish makes them live.

When XBOX services can't save the documents (`CanCommit` false with `--validate-only`,
otherwise `Committed` false), the command still writes the result to stdout, writes each
validation message to stderr, leaves the ETag file unchanged, and exits 1:

```text
XBOX services can't save these documents:
Error: achievements2017.xml: The root namespace doesn't match the document type. (NamespaceDocTypeMismatch)
```

Documents that match the latest draft aren't a failure: nothing is saved,
`ValidationInfo` reports `FilesNotChangedCommit`, and the command exits 0.

A save to the account also follows Partner Center's rules for relying parties and business
partners, which XBOX services don't check. A save that breaks one fails with HTTP 400 and one of
these codes, even with `--validate-only`:

| `code` | Cause |
| --- | --- |
| `relying_party_change_not_allowed` | A relying party document isn't one of the account's relying parties, or it changes the relying party's name or business partner. The account ID and the default business partner's ID count as the same business partner. |
| `business_partner_not_found` | A relying party document names a business partner the account doesn't have. |
| `business_partner_change_not_allowed` | `businesspartner.xml` changes more than business partners' names, such as adding or removing one, or the account has no business partners yet. `detail` names the rule. |

To add relying parties and business partners, use [`relying-party create`](xbl/relying-party.md)
and [`business-partner create`](xbl/business-partner.md).

#### `config download` and `config save` examples

Download the latest draft sandbox documents into `xbox-config`:

```bash
xbox xbl config download --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --sandbox-id SANDBOX1 --view draft --dest xbox-config
```

After you edit the documents, validate them all, or only the two you changed:

```bash
xbox xbl config save --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --sandbox-id SANDBOX1 --file "xbox-config/*.xml" --validate-only

xbox xbl config save --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --sandbox-id SANDBOX1 --file xbox-config/achievements2017.xml --file xbox-config/localization.xml --validate-only
```

Save them with a message:

```bash
xbox xbl config save --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --sandbox-id SANDBOX1 --file "xbox-config/*.xml" --message "Update achievements"
```

Download one document as it was in published version 7:

```bash
xbox xbl config download --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --sandbox-id SANDBOX1 --view published --version 7 --document achievements.xml --dest xbox-config
```

Download the account's draft documents:

```bash
xbox xbl config download --scope account --view draft --dest xbox-config-account --output table
```

Download the title-wide draft:

```bash
xbox xbl config download --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --scope title --view draft --dest xbox-config-title
```

After you edit the embargo in `productdeveloper.xml`, validate it:

```bash
xbox xbl config save --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --scope title --file xbox-config-title/productdeveloper.xml --validate-only
```

### `config publish`

Publish a working version of XBOX services configuration to a sandbox's published
configuration, or publish the title-wide configuration with `--scope title`, or the seller
account's with `--scope account`. A publish runs as a
job on the service, and `config publish` [waits for it to finish](#waiting-and-exit-codes).

#### Source selection

`--sandbox-id` is the destination. The source is a working version of the source sandbox,
which defaults to the destination:

| Scenario | Effect |
| --- | --- |
| `--source-sandbox-id` omitted or equal to `--sandbox-id` | Publishes the sandbox's latest working version. |
| `--source-sandbox-id` differs from `--sandbox-id` | Publishes the source sandbox's latest working version to the destination, and [replaces the destination's global title storage files](#cross-sandbox-publishing-replaces-global-title-storage-files). |
| `--configset <number>` | Publishes that working version of the source sandbox. |

When the source sandbox has unpublished changes, its latest working version isn't what's
live there. A cross-sandbox publish without `--configset` then warns, and names the version
that's live:

```text
Warning: XDKS.1 has unpublished changes. This publish uses its latest working version, 53; what's live in XDKS.1 is working version 52. To publish that instead, add --configset 52.
```

To promote exactly what's live in the source, run with `--validate-only` first and add the
`--configset` it suggests. In the JSON output, `SourceVersion` is the working version used,
`sourceHasUnpublishedChanges` says whether it differs from what's live in the source, and
`sourceLiveVersion` is the version that's live there, when one is found.

#### Title-wide publishing

`config publish --scope title`, or `--sandbox-id HIDDEN`, publishes the latest title-wide draft, so
it takes no `--source-sandbox-id` or `--configset`. To see which version goes live, run
`config status --scope title` first and read `draft.version`. The publish's `SourceVersion`
is a different number, and doesn't match it.

Stats system (`dataplatformconfig.xml`), privilege (`titlepriv.xml`), and single sign-on
(`nsal.xml`) changes are saved with the title-wide configuration, but each sandbox takes them
only when that sandbox publishes. After a title-wide publish starts, the CLI checks which
published sandboxes don't have the latest of those changes yet, and writes the publish to run
for each of them on stderr:

```text
Single sign-on changes in this publish go live in each sandbox only when that sandbox publishes.
Next: xbox xbl config publish --title-id 1234ABCD --seller-id 1234567 --sandbox-id XDKS.1
Next: xbox xbl config publish --title-id 1234ABCD --seller-id 1234567 --sandbox-id RETAIL
```

If the CLI can't check, it writes one line without the commands: `Stats system, privilege, and
single sign-on changes in this publish go live in each sandbox only when that sandbox publishes.` A publish to RETAIL asks
first, as described in [Confirmation](#confirmation).

#### Account publishing

`config publish --scope account` publishes the seller account's configuration, such as its
relying parties and business partners, which [`relying-party`](xbl/relying-party.md),
[`business-partner`](xbl/business-partner.md), and `config save --scope account` save to the
account's draft. It needs a Partner Center permission, which [Scope](#scope) lists.

It asks first, as described in [Confirmation](#confirmation), and always publishes the account's
latest draft, so it takes no `--configset`. When nothing changed since the account's last publish,
no publish starts, and `status` is `AlreadyPublished`; the account has no `--republish`.

XBOX services don't report how an account publish ends. Instead, the command checks the account's
published version, `published.version` in
[`config status --scope account`](#config-list-and-config-status), every 10 seconds, until it's
greater than `previousPublishedVersion`, the published version before the publish. Any publish of
the account raises it, so the rise means only that this publish or another one, such as a
teammate's, committed. The command then exits 0, adds `publishedVersion`, the new version, to the
result, and leaves `status` at `Running`:

```json
{
  "status": "Running",
  "jobId": "11111111-2222-3333-4444-555555555555",
  "sourceVersion": 8,
  "previousPublishedVersion": 6,
  "validation": [],
  "publishedVersion": 9
}
```

The draft is live when `config status --scope account` shows `hasChanges` as `false`. XBOX services
start using the published configuration once it reaches them, which the CLI can't see.

A publish that fails leaves the published version unchanged, so the wait ends with
[exit code 4](#waiting-and-exit-codes) when `--timeout` passes. Then, or after exit 3, stderr says
how to tell whether the draft is live, and that running the publish again is safe, because it
publishes only what isn't live yet:

```text
Publish job 11111111-2222-3333-4444-555555555555 didn't land within 5 minutes. It might still be running, or it might have failed; XBOX services don't report which.
Check with: xbox xbl config status --scope account --seller-id 1234567
The draft is live if hasChanges is false. If not, running the publish again is safe, because it publishes only what isn't live yet.
```

A save since the publish keeps `hasChanges` `true`, and running the publish again then publishes
that save too.

#### Confirmation

`config publish` asks before it publishes in these cases:

| Publish | Why it asks |
| --- | --- |
| `--sandbox-id RETAIL` | Players see the change when the publish finishes. |
| `--sandbox-id CERT` or `CERT.DEBUG` | XBOX certification tests in these sandboxes. |
| `--scope title` | The title-wide configuration applies to every sandbox, RETAIL included. |
| `--scope account` | The account's configuration applies to the whole seller account, in every sandbox, RETAIL included. |
| `--source-sandbox-id` differs from `--sandbox-id` | First replaces the destination's working documents that have the same names, and its [global title storage files](#cross-sandbox-publishing-replaces-global-title-storage-files). |

At a terminal it describes the publish and asks. Without a terminal it refuses and exits 1.
`--confirm` (`--yes`, `-y`) skips the question, and `--validate-only` never asks. See
[Safety and destructive operations](../safety.md).

Before it asks about a RETAIL publish, it validates the publish and lists the
[achievements it adds](#achievements-a-retail-publish-adds). If you answer yes, it publishes
the working version it validated, so what goes live is what was listed:

```console
$ xbox xbl config publish --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --source-sandbox-id CERT --sandbox-id RETAIL
This publish adds 2 achievements to RETAIL: 1, 2. Once they're live in RETAIL, achievements can't be removed, and their unlock rules and gamerscore can't change.
This publishes working version 52 of CERT's configuration to RETAIL for SCID aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee.
RETAIL is live: players see the change when the publish finishes.
Publishing from CERT first replaces RETAIL's working documents that have the same names.
Warning: publishing from CERT to RETAIL replaces RETAIL's global title storage files with CERT's. Files with the same path are overwritten, files only CERT holds are added, and files only RETAIL holds are deleted. Deleted files can't be recovered.

Publish to RETAIL? [y/N]:
```

When that validation fails, it publishes nothing and doesn't ask. So at a terminal, a RETAIL
publish by `--title-id` to configuration other titles are bound to needs
`--allow-shared-configuration`. With `--confirm` there's no list, so run `--validate-only`
first to see it.

#### Achievements a RETAIL publish adds

Once achievements are live in RETAIL, they can't be removed, and their unlock rules and
gamerscore can't change. So a RETAIL publish says which achievement IDs it adds that RETAIL
doesn't have yet:

- With `--validate-only`, the list goes to stderr, and the JSON output carries it in
  `achievementChanges.added`.
- At a terminal without `--validate-only`, the list comes before the question, as shown
  in [Confirmation](#confirmation).

```console
$ xbox xbl config publish --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --source-sandbox-id CERT --sandbox-id RETAIL --validate-only
This publish would add 2 achievements to RETAIL: 1, 2. Once they're live in RETAIL, achievements can't be removed, and their unlock rules and gamerscore can't change.
```

A publish that adds none says so. Other sandboxes don't list achievements.

#### Republishing

`config publish` publishes only changes. When nothing changed since the destination's last
successful publish, no publish starts, and the result's `Status` is `AlreadyPublished`. After
a publish fails, running `config publish` again retries it.

Sometimes XBOX locks RETAIL for a product, and a RETAIL to RETAIL publish then fails with
`retail_locked`. To publish anyway, add `--republish`: it forces the selected
configuration, by default RETAIL's latest working configuration, onto RETAIL. Publishing to
RETAIL from another sandbox, such as CERT, also clears the lock, but it replaces RETAIL's
working documents with that sandbox's.

`--republish` publishes in full, even when nothing changed. It can't be combined with
`--validate-only`.

#### Diagnostics

Validation diagnostics from XBOX services, such as the warning for a first RETAIL publish
without an embargo, go to stderr, one per line. The JSON output keeps them in
`ValidationResult`, or in `validation` for an account publish.

```text
XBOX services warning (FirstRetailPublishWithoutEffectiveEmbargo): This is the first time these Xbox Gameplay Settings will be published to RETAIL, and no embargo is configured.
```

When validation fails, `config publish` exits 1 and lists each message under `validation`, as
shown in [Errors](#errors).

#### Cross-sandbox publishing replaces global title storage files

> [!CAUTION]
> Publishing from one sandbox to another makes the destination's global title storage files
> an exact copy of the source's. Files only the destination has are deleted, and can't be
> recovered.

Global files are the files every player of the game reads, such as news feeds and maps:

| File | Source | Destination before | Destination after |
| --- | --- | --- | --- |
| `news.json` | Present | Present | Overwritten with the source's copy |
| `tuning.cfg` | Present | — | Added |
| `maps.bin` | — | Present | **Deleted** |

The copy takes whatever the source holds when the publish runs. Players' own data isn't
touched, and a same-sandbox publish leaves global files alone. To keep a destination file,
add it to the source sandbox before publishing. To see each sandbox's files, run
[`xbl global-storage list`](xbl/global-storage.md).

Every cross-sandbox publish writes this warning to stderr, including with `--validate-only`,
and includes it in the [confirmation](#confirmation) question:

```text
Warning: publishing from SANDBOX1 to SANDBOX2 replaces SANDBOX2's global title storage files with SANDBOX1's. Files with the same path are overwritten, files only SANDBOX1 holds are added, and files only SANDBOX2 holds are deleted. Deleted files can't be recovered.
```

#### Waiting and exit codes

Once the publish starts, the command writes its job ID to stderr, and checks the destination's
last publish, as [`config status`](#config-list-and-config-status) reports it, every 10 seconds
until the job finishes. When it finishes, stdout is the publish result with `Status` set to
`Success` or `Failed`, and `StatusMessage` set to the service's message about it.

`--timeout` sets how long to wait: 5 minutes unless you pass it, and at most 7 days. Ctrl+C
stops waiting, and the publish continues on the service. With `--no-wait`, the command writes
the result as soon as the publish starts, with `Status` set to `Running`. Either way, stderr
gives the `config status` command that checks the publish:

```text
Publish job 11111111-2222-3333-4444-555555555555 didn't finish within 5 minutes. Publishing continues on the service. Check it with: xbox xbl config status --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --sandbox-id SANDBOX1
```

| Exit code | Meaning |
| --- | --- |
| 0 | Published. Also for a validation, for `AlreadyPublished`, and for a publish started with `--no-wait`. For an account publish, the account's published version rose, from this publish or another one; see [Account publishing](#account-publishing). |
| 1 | Not published, for example because validation failed or you didn't confirm. |
| 2 | The publish job finished with a status other than `Success`. |
| 3 | The publish was sent, but its outcome is unknown. Check `config status` before publishing again. See [Errors](#errors). |
| 4 | The publish started, but waiting ended before it finished, or another publish to the sandbox started after it, so how it ended can't be read. For an account publish, also one that failed; see [Account publishing](#account-publishing). Check `config status` before publishing again. |

On exit 3 or 4, stdout is empty.

#### `config publish` options

| Option | Alias | Required | Description |
| --- | --- | --- | --- |
| `--scid <guid>` | — | Or title, for `sandbox` and `title` | SCID of the title's configuration. Not allowed with `--scope account`. |
| `--title-id <hex-id>` | — | Or SCID, for `sandbox` and `title` | XBOX title ID. See [Title targeting](#title-targeting). Not allowed with `--scope account`. |
| `--scope <sandbox\|title\|account>` | — | No | [Scope](#scope). Defaults to `sandbox`. `title` publishes the title-wide configuration, and `account` the seller account's; see [Title-wide publishing](#title-wide-publishing) and [Account publishing](#account-publishing). |
| `--sandbox-id <id>` | — | For `sandbox` | Destination sandbox ID. Defaults to `XBOX_SANDBOX_ID`, then the saved sandbox ID. |
| `--source-sandbox-id <id>` | — | No | Source sandbox ID. Defaults to `--sandbox-id`. Not allowed with `--scope title`, `--scope account`, or `--sandbox-id HIDDEN`. |
| `--validate-only` | — | No | Validate without publishing. For RETAIL, also lists the [achievements the publish adds](#achievements-a-retail-publish-adds). |
| `--allow-shared-configuration` | — | No | Publish by `--title-id` to configuration other titles are bound to, without asking. See [Title targeting](#title-targeting). Not allowed with `--scope account`. |
| `--configset <number>` | — | No | Positive working version of the source sandbox. Defaults to its latest. Not allowed with `--scope title`, `--scope account`, or `--sandbox-id HIDDEN`. |
| `--republish` | — | No | Publish in full even when nothing changed, or onto a RETAIL locked after a migration. See [Republishing](#republishing). Not allowed with `--scope account`. |
| `--confirm` | `--yes`, `-y` | No | Publish without asking. See [Confirmation](#confirmation). |
| `--no-wait` | — | No | Return as soon as the publish starts. Not allowed with `--timeout` or `--validate-only`. |
| `--timeout <duration>` | — | No | How long to wait for the publish to finish, such as `90s`, `10m`, `2h`, or `1d`. Defaults to `5m`; at most `7d`. Not allowed with `--validate-only`. |
| `--seller-id <id>` | — | No | Partner Center seller ID. |
| `--output <format>` | `-o` | No | `json` (default), `json-compact`, or `table` with `STATUS`, `SOURCE`, `DESTINATION`, `VALIDATE ONLY`, and `SOURCE VERSION` columns. |

#### `config publish` examples

Validate a publish to SANDBOX1 without publishing:

```bash
xbox xbl config publish --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --sandbox-id SANDBOX1 --validate-only
```

Publish SANDBOX1's latest working configuration, so it's live in SANDBOX1:

```bash
xbox xbl config publish --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --sandbox-id SANDBOX1
```

Publish SANDBOX1's latest working version to SANDBOX2. This also replaces SANDBOX2's global
title storage files with SANDBOX1's:

```bash
xbox xbl config publish --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --source-sandbox-id SANDBOX1 --sandbox-id SANDBOX2
```

Publish working version 42 of SANDBOX1 to SANDBOX2:

```bash
xbox xbl config publish --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --source-sandbox-id SANDBOX1 --sandbox-id SANDBOX2 --configset 42
```

Validate a publish from CERT to RETAIL, which lists the achievements it adds:

```bash
xbox xbl config publish --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --source-sandbox-id CERT --sandbox-id RETAIL --validate-only
```

Then pass the output's `SourceVersion` as `--configset` to publish the version you validated,
without a prompt:

```bash
xbox xbl config publish --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --source-sandbox-id CERT --sandbox-id RETAIL --configset 52 --confirm
```

Validate a title-wide publish without publishing:

```bash
xbox xbl config publish --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --scope title --validate-only
```

Validate a publish of the seller account's configuration without publishing:

```bash
xbox xbl config publish --scope account --seller-id 1234567 --validate-only
```

Publish the seller account's latest working configuration:

```bash
xbox xbl config publish --scope account --seller-id 1234567
```

Publish SANDBOX1 again although nothing changed:

```bash
xbox xbl config publish --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --sandbox-id SANDBOX1 --republish
```

Start a publish to SANDBOX2 without waiting, then check it:

```bash
xbox xbl config publish --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --source-sandbox-id SANDBOX1 --sandbox-id SANDBOX2 --no-wait
xbox xbl config status --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --sandbox-id SANDBOX2 --output table
```

---

## schema

Discover and download XBOX services configuration document schemas.

| Command | Description |
| --- | --- |
| `schema list` | List every schema type. |
| `schema list --type <type>` | List versions for one schema type. |
| `schema get --type <type>` | Get one version of a schema type: its type, version, and namespace. Defaults to the latest version. |
| `schema download --dest <directory>` | Save XSDs into a directory: one version, every version of one type, or every version of every type. |
| `schema download --type <type> --version <n> --dest -` | Write one XSD to stdout. |

### `schema list` options

| Option | Required | Description |
| --- | --- | --- |
| `--type <type>` | No | Schema type whose versions to list. Omit it to list all types. |
| `--output <format>` | No | `json` (default), `json-compact`, or `table` with `TYPE`, `VERSION`, `NAMESPACE`, `PATH`, and `BYTES` columns. |

### `schema get` options

| Option | Required | Description |
| --- | --- | --- |
| `--type <type>` | Yes | Schema type. |
| `--version <number>` | No | Positive schema version. Defaults to the type's latest version. |
| `--output <format>` | No | `json` (default), `json-compact`, or `table` with `TYPE`, `VERSION`, and `NAMESPACE` columns. |

`schema get` writes one JSON object:

```json
{
  "type": "achievements2017",
  "version": 1,
  "namespace": "http://config.mgt.xboxlive.com/schema/achievements2017/1"
}
```

### `schema download` options

| Option | Required | Description |
| --- | --- | --- |
| `--dest <directory>` | Yes | Directory for saved XSD files, or `-` to write one XSD to stdout. `-` requires both `--type` and `--version`. |
| `--type <type>` | No | Schema type. Omit it to download every type. |
| `--version <number>` | No | Positive schema version. Requires `--type`. Omit it to download every version. |
| `--output <format>` | No | `json` (default), `json-compact`, or `table` with `TYPE`, `VERSION`, `NAMESPACE`, `PATH`, and `BYTES` columns. |

Each schema is saved as `<type>_<version>.xsd`, with the type in lowercase, and the command
prints the saved paths. With `--dest -`, it writes the raw XSD to stdout instead, and
`--output` doesn't apply.

### `schema` examples

```bash
# List every schema type
xbox xbl schema list

# List versions of one type, as a table
xbox xbl schema list --type achievements2017 --output table

# Get the latest version of one type
xbox xbl schema get --type achievements2017

# Redirect one XSD straight to a file
xbox xbl schema download --type achievements2017 --version 1 --dest - > achievements2017_1.xsd

# Save one XSD into a directory
xbox xbl schema download --type achievements2017 --version 1 --dest schemas

# Download every version of one schema type
xbox xbl schema download --type achievements2017 --dest schemas

# Download every version of every schema type
xbox xbl schema download --dest schemas
```

---

## A typical configuration round trip

1. Find the sandboxes you can reach:

   ```bash
   xbox sandbox list --output table
   ```

2. See which drafts have changes that aren't published:

   ```bash
   xbox xbl config list --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --output table
   ```

3. Download the draft document set, which also saves `SANDBOX1.etag`:

   ```bash
   xbox xbl config download --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --sandbox-id SANDBOX1 --view draft --dest xbox-config
   ```

4. Edit the documents in `xbox-config`, then validate them:

   ```bash
   xbox xbl config save --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --sandbox-id SANDBOX1 --file "xbox-config/*.xml" --validate-only
   ```

5. Save them:

   ```bash
   xbox xbl config save --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --sandbox-id SANDBOX1 --file "xbox-config/*.xml" --message "Update configuration"
   ```

6. Publish them in SANDBOX1. The command waits until the publish finishes:

   ```bash
   xbox xbl config publish --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --sandbox-id SANDBOX1
   ```
