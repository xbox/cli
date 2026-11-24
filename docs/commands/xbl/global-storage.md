---
title: Global title storage
description: Get usage, and list, download, upload, and delete the global title storage files every player of an XBOX game reads in a sandbox.
---

# Global title storage

Global title storage holds the files every player of a game reads in an XBOX sandbox, such as a
news feed, a map, or tuning values. `xbox xbl global-storage` shows how much of its quota a
sandbox uses, and lists, downloads, uploads, and deletes its files, one at a time. Reading the
files needs Partner Center permission to read the game's XBOX services configuration, and
uploading or deleting them needs permission to change it.

A game reads these files only when its sandbox's published `titlestorage.xml` has
`<StorageType ownerOnly="false">global</StorageType>`, which Partner Center saves when you select
**Active** for **Global** on the **Title Storage** page. To turn it on with the CLI, add that
element to the sandbox's `titlestorage.xml` with
[`xbl config save`](../xbl.md#config-download-and-config-save), then publish the sandbox with
[`xbl config publish`](../xbl.md#config-publish).

Like every `xbl` command, these commands are Experimental: their options and output might change
or be removed. See [Experimental commands](../../cli-command-lifecycle.md).

| Command | Effect |
| --- | --- |
| `global-storage get` | Show how much of its quota a sandbox's files use. |
| `global-storage list` | List a sandbox's files. |
| `global-storage download` | Download one file. |
| `global-storage upload` | Upload one file of at most 4 MiB. |
| `global-storage delete` | Delete one file. Asks first. |

> [!WARNING]
> An upload or delete reaches every player in the sandbox at once, with no publish first. A
> deleted file can't be recovered.

`RETAIL` is read-only: `upload` and `delete` fail there. To change `RETAIL`'s files, change a
development sandbox's, then publish that sandbox to `RETAIL` with
[`xbl config publish`](../xbl.md#cross-sandbox-publishing-replaces-global-title-storage-files),
which makes `RETAIL`'s files a copy of its files.

## Options

| Option | Command | Required | Description |
| --- | --- | --- | --- |
| `--scid <guid>` or `--title-id <id>` | all | Yes, one of them | The game's service configuration identifier (SCID), or its XBOX title ID, which the CLI looks up first. See [Title targeting](../xbl.md#title-targeting). |
| `--sandbox-id <id>` | all | Yes | A sandbox games run in, such as `XDKS.1` or `RETAIL`. `ALL`, `HIDDEN`, and `HISTORY` hold only configuration, so they fail. |
| `--seller-id <id>` | all | No | Partner Center seller ID. Defaults to your only seller. |
| `--path <path>` | all but `get` | For `download`, `upload`, and `delete` | The file's path, such as `news/today.json`. For `list`, a folder, such as `news/`, to list only the files in it. |
| `--file-type <type>` | `download`, `upload`, `delete` | Yes | The file's type: `binary`, `json`, or `config`. |
| `--file <file>` | `upload` | Yes | The local file to upload, at most 4 MiB, or `-` to read it from stdin. |
| `--overwrite` | `upload` | No | Replace the file at `--path`, whatever its type. Without it, `upload` fails when the path holds a file. |
| `--dest <path>` | `download` | No | Where to save the file: a file, a directory to save it in under its name, or `-` for stdout. Defaults to its name, in the current directory. An existing file is replaced. |
| `--confirm`, `--yes`, `-y` | `delete` | Outside an interactive terminal | Delete without asking. |
| `--output <format>` | all | No | `json` (default) or `json-compact`, and `table` for `get` and `list`. |

Without `--scid` or `--title-id`, `--sandbox-id`, or `--seller-id`, a command uses the value set
as [Context](../context.md) describes.

A path is folders of letters, digits, and `_`, each followed by `/`, then a file name of letters,
digits, `_`, `-`, and `.`. A path holds one file, whatever its type, so `download` and `delete`
need the type `list` shows. XBOX services check a `json` or `config` file's content when it's
uploaded.

## Examples

See how much of its quota XDKS.1 uses, and list its files:

```bash
xbox xbl global-storage get --title-id 1234ABCD --sandbox-id XDKS.1 --output table
xbox xbl global-storage list --title-id 1234ABCD --sandbox-id XDKS.1 --output table
```

Upload `today.json` as the news feed the game reads:

```bash
xbox xbl global-storage upload --title-id 1234ABCD --sandbox-id XDKS.1 --file today.json --path news/today.json --file-type json
```

Replace it with a new version:

```bash
xbox xbl global-storage upload --title-id 1234ABCD --sandbox-id XDKS.1 --file today.json --path news/today.json --file-type json --overwrite
```

Download it to `today.json` in the current directory, or write it to stdout:

```bash
xbox xbl global-storage download --title-id 1234ABCD --sandbox-id XDKS.1 --path news/today.json --file-type json
xbox xbl global-storage download --title-id 1234ABCD --sandbox-id XDKS.1 --path news/today.json --file-type json --dest -
```

Delete it:

```bash
xbox xbl global-storage delete --title-id 1234ABCD --sandbox-id XDKS.1 --path news/today.json --file-type json
```

## Output

`get` writes the sandbox's usage:

```json
{
  "usedBytes": 9437696,
  "quotaBytes": 268435456
}
```

`usedBytes` is the space the sandbox's files use, and `quotaBytes` its quota: once the files use
that much, uploads fail. Its table shows `USED BYTES` and `QUOTA BYTES`.

`list` writes an array of files, or `[]` when there are none:

```json
[
  {
    "path": "maps/nod_arena.bin",
    "type": "binary",
    "size": 9437184,
    "etag": "\"0x8DDF1A2B3C4D5E6\"",
    "clientFileTime": "2026-10-28T17:02:11+00:00"
  },
  {
    "path": "news/today.json",
    "type": "json",
    "size": 512,
    "etag": "\"0x8DDF1A2B3C4D5E7\""
  }
]
```

`size` is in bytes, and `clientFileTime` is when the file was uploaded, when it's known. The
table shows `PATH`, `TYPE`, `SIZE`, and `UPLOADED`.

`upload`, `download`, and `delete` are file operations, like `cp` and `rm`. They write nothing to
stdout, except the file with `download --dest -`, and report what they did on stderr:

| Command | stderr on success |
| --- | --- |
| `upload` | `Uploading '<file>' to 'news/today.json'.` before it sends the file, then `Uploaded '<file>' to 'news/today.json' (json, 512 bytes).` |
| `download` | `Downloaded 'news/today.json' to '<path>' (512 bytes).` |
| `delete` | `Deleted 'news/today.json' (json).` |

These messages might change between releases. These three commands don't accept
`--output table`. A download that fails leaves a file already at the destination as it was.

## Exit codes

| Exit code | Meaning |
| --- | --- |
| `0` | The command succeeded. |
| `1` | The command failed. For `upload` and `delete`, nothing changed: the request wasn't sent, or the service refused it. |
| `3` | `upload` or `delete` was sent, but its outcome is unknown. Before you run it again, check the file with the `global-storage list` command that stderr shows. |

## Errors

A refused request exits `1` and writes a JSON error to stderr, as [Errors](../xbl.md#errors)
describes. These are the ones you're most likely to see:

| `code` | Cause |
| --- | --- |
| `global_storage_file_not_found` | A `download` or `delete` of a path that holds no file of that `--file-type`; `delete` deletes nothing. `list` shows each file's path and type. |
| `global_storage_file_exists` | The path already holds a file. Pass `--overwrite` to replace it. |
| `global_storage_retail_read_only` | An upload or delete in `RETAIL`. Change a development sandbox's files, then publish it to `RETAIL`. |
| `global_storage_quota_exceeded` | The sandbox's files have reached its quota. Delete files it doesn't need. |
| `xbox_access_denied` | You don't have permission for the game's XBOX services configuration, or for the sandbox. |
| `downstream_request_rejected` | XBOX services refused the request, such as a `json` file that isn't valid JSON. `detail` says why. |
