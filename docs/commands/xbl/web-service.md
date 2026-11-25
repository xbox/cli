---
title: Web services
description: List, get, create, update, and delete the web services your own services use to call XBOX services, and create their certificates.
---

# Web services

A web service is the identity your own service, such as a game server or a stats backend, uses to
call XBOX services, and a certificate is how it signs in. `xbox xbl web-service` lists, gets,
creates, updates, and deletes the web services of a Partner Center seller, and creates their
certificates. Reading them needs the **Relying parties** and **Business partners** read
permissions in Partner Center, and changing them or creating a certificate needs their write
permissions. For what a web service can do, see
[Setting up web services at Partner Center](https://docs.xbox.com/services/xbox-services/fundamentals/s2s-auth-calls/custom-service-config/web-services/live-web-services)
in the XBOX developer documentation.

Like every `xbl` command, these commands are Experimental: their options and output might change
or be removed. See [Experimental commands](../../cli-command-lifecycle.md).

| Command | Effect |
| --- | --- |
| `web-service list` | List the seller's web services. |
| `web-service get` | Get one web service. |
| `web-service create` | Create a web service. |
| `web-service update` | Rename a web service, or change its access. |
| `web-service delete` | Delete a web service. Shows its name and business partner, and asks first. |
| `web-service certificate create` | Create a certificate for a web service, and save it with its private key. Asks first. |

> [!WARNING]
> Keep a certificate's private key secret. Whoever has it can call XBOX services as the web
> service until the certificate expires, about 18 months after it's created, even if you delete
> the web service. Neither the CLI nor Partner Center can revoke a certificate: if its key leaks,
> ask your developer account manager (DAM) to have XBOX revoke it. That takes about 20 minutes,
> and tokens already issued with the certificate keep working for up to two weeks.

`create` makes the web service for the seller's default business partner. For another business
partner, create the web service and its certificates in Partner Center.

A delete can't be undone, and it takes away none of the web service's access: certificates created
for it keep working, with that access, until they expire. To stop using a web service, keep it:
remove it from your games' [access policies](../../xbox-services-settings.md#access-policies), and
turn off its telemetry and app channels access with `update`. XBOX services take that access away
in the background, which deleting the web service stops. Kept or deleted, a web service keeps its
access to content in each of the seller's sandboxes. A game whose access policies name a deleted
web service fails its next configuration validation until you remove it from them.

## Options

| Option | Command | Required | Description |
| --- | --- | --- | --- |
| `<web-service-id>` | `get`, `update`, `delete` | Yes | The web service's ID, a GUID that `list` shows. |
| `--name <name>` | `create`, `update` | For `create` | The web service's name, 1 to 100 characters, such as the host your service runs on. It can't contain `<`, `>`, `&`, `'`, or `"`. |
| `--telemetry-access` | `create`, `update` | No | Let the web service read telemetry for every game of the seller. For `update`, pass `true` or `false`; without it, the access stays as it is. |
| `--app-channels-access` | `create`, `update` | No | Let the web service publish app channels to consoles, as a media provider. For `update`, pass `true` or `false`. |
| `--web-service-id <id>` | `certificate create` | Yes | The ID of the web service the certificate is for. |
| `--dest <file>` | `certificate create` | Yes | The new file to save the certificate in. See [Creating a certificate](#creating-a-certificate). |
| `--sandbox-id <id>` | `certificate create` | No | Limit the certificate to one sandbox, such as `XDKS.1`, `RETAIL`, `CERT`, or `CERT.DEBUG`. Defaults to every sandbox: `XBOX_SANDBOX_ID` and the sandbox saved with `context set` aren't used. |
| `--password-file <file>` | `certificate create` | No | A file that holds the password for a `.pfx` or `.p12` file, or `-` to read it from piped stdin. A single trailing line break is removed. Without `--password-file`, the CLI asks for the password at the terminal. |
| `--public-key-file <file>` | `certificate create` | No | Use your own key. See [Using your own key](#using-your-own-key). |
| `--confirm`, `--yes`, `-y` | `delete`, `certificate create` | Outside an interactive terminal | Go ahead without asking. |
| `--seller-id <id>` | all | No | Partner Center seller ID. Defaults as [Context](../context.md) describes, then to your only seller. |
| `--output <format>` | all | No | `json` (default), `json-compact`, or `table`. |

## Examples

List the seller's web services:

```bash
xbox xbl web-service list --output table
```

Create a web service that can read the seller's game telemetry:

```bash
xbox xbl web-service create --name "Nod Arena stats" --telemetry-access --seller-id 1234567
```

Turn off its telemetry access:

```bash
xbox xbl web-service update 11111111-2222-3333-4444-555555555555 --telemetry-access false
```

Delete another web service:

```bash
xbox xbl web-service delete 22222222-3333-4444-5555-666666666666
```

## Creating a certificate

`certificate create` makes a 2048-bit RSA key pair on your computer, sends only the public key,
and saves the certificate it gets back with the private key in `--dest`. The file's extension
picks its format:

| `--dest` | Holds |
| --- | --- |
| `.pfx` or `.p12` | The certificate and its private key, protected by a password. The CLI asks for the password twice, or reads it from `--password-file`. |
| `.pem` | The certificate, then its private key, unencrypted. On macOS and Linux only. |

On macOS and Linux, only you can read the file. On Windows, a new file gets its folder's
permissions, so the CLI refuses `.pem` there and saves the key only in a `.pfx` or `.p12` file.
The CLI never replaces a file, so `--dest` must be a new file, and its folders can't be symbolic
links or junctions. Each run creates another certificate, and several can be in use at once, so
to renew one, create another before it expires.

If the web service's business partner is in the seller account's draft but not published, the
certificate can't sign in to XBOX services: requests for XBOX services security tokens (XSTS
tokens) with it fail with `business_partner_required`. When you see that error, run
`xbl config status --scope account`. If it shows `hasChanges` as `true`, review the draft with
`xbl config diff --scope account --lines`, because a publish makes every change in it live, then
publish it with [`xbl config publish --scope account`](../xbl.md#account-publishing).

Create a certificate for a web service, valid in every sandbox:

```bash
xbox xbl web-service certificate create --web-service-id 11111111-2222-3333-4444-555555555555 --dest nod-arena-stats.pfx
```

On macOS or Linux, create one that works only in XDKS.1, saved as PEM:

```bash
xbox xbl web-service certificate create --web-service-id 11111111-2222-3333-4444-555555555555 --dest nod-arena-stats.pem --sandbox-id XDKS.1
```

### Using your own key

With `--public-key-file`, the CLI makes no key. It sends your 2048-bit RSA public key and saves
only the certificate, as PEM, in a `.pem`, `.cer`, or `.crt` file. Pass the public key in PEM,
as `openssl rsa -in key.pem -pubout` writes it, or as the base64 key blob Partner Center's
PowerShell script prints. A private key is refused, so it's never sent.

```bash
xbox xbl web-service certificate create --web-service-id 11111111-2222-3333-4444-555555555555 --public-key-file nod-arena-stats-public.pem --dest nod-arena-stats.cer
```

## Output

`list` writes an array of web services, or `[]` when there are none:

```json
[
  {
    "webServiceId": "11111111-2222-3333-4444-555555555555",
    "name": "Nod Arena stats",
    "businessPartnerId": "22222222-3333-4444-5555-666666666666",
    "telemetryAccess": true,
    "appChannelsAccess": false
  }
]
```

`businessPartnerId` is the business partner the web service acts for. The seller account's own ID
there means the seller's default business partner, which `create` always uses; `certificate create`
refuses a web service with any other (`business_partner_not_supported`). `get`, `create`, and
`update` write one web service the same way, and `delete` writes it as it was. The table shows
`WEB SERVICE ID`, `NAME`, `BUSINESS PARTNER`, `TELEMETRY`, and `APP CHANNELS`.

`certificate create` writes what it saved:

```json
{
  "webServiceId": "11111111-2222-3333-4444-555555555555",
  "sandbox": null,
  "thumbprint": "0123456789ABCDEF0123456789ABCDEF01234567",
  "notAfter": "2028-05-21T00:09:15Z",
  "file": "D:\\NodArena\\nod-arena-stats.pfx"
}
```

`sandbox` is the sandbox the certificate is limited to, or `null` for every sandbox. `thumbprint`
is its SHA-1 thumbprint, and `notAfter` is when it expires, in UTC. The table shows `THUMBPRINT`,
`EXPIRES`, and `SANDBOX`.

## Exit codes

| Exit code | Meaning |
| --- | --- |
| `0` | The command succeeded. |
| `1` | The command failed. For a change, nothing changed: you didn't confirm, the request wasn't sent, or the service refused it. |
| `3` | A change was sent, but its outcome is unknown. Before you run `create`, `update`, or `delete` again, check with the `list` or `get` command that stderr shows. After `certificate create`, run it again: a certificate created then can't be retrieved, and no file is left. |

## Errors

A refused request exits `1` and writes a JSON error to stderr, as [Errors](../xbl.md#errors)
describes. These are the ones you're most likely to see:

| `code` | Cause |
| --- | --- |
| `web_service_not_found` | The seller has no web service with that ID. |
| `business_partner_not_supported` | The web service acts for a business partner other than the seller's default one. Create its certificates in Partner Center. |
| `sandbox_not_found` | `--sandbox-id` isn't `RETAIL`, `CERT`, `CERT.DEBUG`, or one of the seller's sandboxes. No certificate was created. |
| `xbox_permission_denied` | You don't have the Partner Center permissions the command needs. `detail` names them. |
| `xbox_access_denied` | You don't have permission to read the seller's web services. |
| `identity_override_not_authorized` | You don't belong to that seller. Find yours with `xbox seller list`. |
