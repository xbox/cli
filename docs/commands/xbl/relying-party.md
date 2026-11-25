---
title: Relying parties
description: List, get, create, and save a seller account's relying parties, which set the tokens XBOX services issue a game for your web services.
---

# Relying parties

A relying party sets the tokens XBOX services issue a game for one of your web services: their
audience, the claims they carry, how long they last, and the certificate they're encrypted to.
When a game asks for a token for a URL that its title's single sign-on endpoints map to a relying
party, XBOX services issue a token for that relying party. `xbox xbl relying-party` lists, gets,
creates, and saves the relying parties of a seller account. Reading them needs the Partner Center
**Relying parties** Read permission, and creating or saving them needs **Relying parties** Write.

Like every `xbl` command, these commands are Experimental: their options and output might change
or be removed. See [Experimental commands](../../cli-command-lifecycle.md).

| Command | Effect |
| --- | --- |
| `relying-party list` | List the account's relying parties. |
| `relying-party get <name>` | Get one relying party. |
| `relying-party create <name>` | Create a relying party, and save its settings to the account's draft. Asks first. |
| `relying-party save <name>` | Save changes to a relying party's settings in the account's draft. |

> [!CAUTION]
> `create` registers the relying party's name across XBOX services at once, before you publish.
> Nobody can rename, delete, or reuse it, and its business partner can't be changed.

`create` and `save` change the account's draft. Players' tokens change only after you publish it
with [`xbl config publish --scope account`](../xbl.md#account-publishing). For how a web service
uses relying parties, see
[Setting up web services at Partner Center](https://docs.xbox.com/services/xbox-services/fundamentals/s2s-auth-calls/custom-service-config/web-services/live-web-services)
in the XBOX developer documentation. To map the URLs your game calls to a relying party, see
[Single sign-on endpoints](../../xbox-services-settings.md#single-sign-on-endpoints).

## Make the encryption certificate

Your service decrypts its tokens with the certificate's private key, which stays with your service.
`create` and `save` take only the public certificate, DER or PEM. They refuse a file that holds a
private key, such as a `.pfx`, before they send anything. For example, OpenSSL writes a new private
key to `rp-key.pem`, protected by a pass phrase it asks for, and its public certificate to `rp.cer`:

```bash
openssl req -x509 -newkey rsa:2048 -sha256 -days 730 -subj "/CN=api.nodpublishers.com" -keyout rp-key.pem -out rp.cer
```

## Options

| Option | Command | Required | Description |
| --- | --- | --- | --- |
| `<name>` | `get`, `create`, `save` | Yes | The relying party's name, such as `rp://api.nodpublishers.com/`. `rp://` and the trailing `/` are optional. For `create`, a host name such as `api.nodpublishers.com`, which becomes `rp://api.nodpublishers.com/`. It doesn't have to be your service's address. |
| `--certificate-file <path>` | `create`, `save` | For `create` | The public certificate that its tokens are encrypted to. On `save`, it replaces the current one. |
| `--business-partner-id <id>` | `create` | No | The [business partner](business-partner.md) that owns it. Defaults to the account's default business partner. |
| `--lifetime-hours <hours>` | `create`, `save` | No | How long its tokens last, from 1 to 720 hours. `create` defaults to 16, as Partner Center does. |
| `--jku-enabled <true\|false>` | `create`, `save` | No | Whether its tokens carry a `jku` header, the address of the keys XBOX services sign them with, so your service follows key changes. `create` defaults to `true`, as Partner Center does. Partner Center strongly recommends it; otherwise your service must update the token signing certificate by hand. |
| `--claim <uri>` | `create`, `save` | No | A claim type URI its tokens carry. Repeat for more. See [Claims](#claims). |
| `--confirm` (`--yes`, `-y`) | `create` | No | Create without asking. |
| `--seller-id <id>` | all | No | Partner Center seller ID. Defaults as [Context](../context.md) describes, then to your only seller. |
| `--output <format>` | all | No | `json` (default), `json-compact`, or `table`. |

`save` changes only the settings you pass, and needs at least one of them.

### Claims

A claim is a claim type URI, such as `http://schemas.microsoft.com/xbox/2011/07/claims/user/gamertag`,
as `relying-party get` lists them. XBOX services refuse a claim the business partner's type doesn't
allow. The claims every relying party carries, such as the user's age group, are always added.

With `--claim`, `create` gives the relying party only the claims you pass. Without it, `create` gives
it the claims Partner Center selects for its business partner's type, which
[`business-partner list`](business-partner.md) shows:

| Business partner type | Claims |
| --- | --- |
| `Partner` | Delegation token (dlt), Device debug (ddm), Device type (dty), Gamertag (gtg), Privileges (prv), Sandbox ID (sbx), Title ID (tid), and Partner XBOX user ID (ptx) |
| `LegacyAnonymized` | The `Partner` ones, and Partner device ID (pdi) |
| `Legacy` | The `Partner` ones, Device ID (did), and XBOX user ID (xid) |
| `Internal` or `LegacyPartner` | None |

Partner Center doesn't recommend the User Pairwise ID (upi) claim,
`http://schemas.microsoft.com/xbox/2013/03/claims/user/pwid`. Use the Partner XBOX user ID (ptx)
claim, `http://schemas.microsoft.com/xbox/2011/07/claims/user/pxuid`, instead.

> [!WARNING]
> On `save`, the claims you pass replace all of the relying party's claims. A claim you don't pass
> again is removed, so pass every claim it should carry.

For example, if `relying-party get` lists only the delegation token claim besides the ones every
relying party carries, this keeps it and adds the gamertag claim:

```bash
xbox xbl relying-party save rp://api.nodpublishers.com/ --claim http://schemas.microsoft.com/xbox/2011/07/claims/delegationtoken --claim http://schemas.microsoft.com/xbox/2011/07/claims/user/gamertag --seller-id 1234567
```

To keep only the claims every relying party carries, pass one of them, such as
`--claim http://schemas.microsoft.com/xbox/2011/07/claims/user/agegroup`.

## Examples

List the account's relying parties:

```bash
xbox xbl relying-party list --seller-id 1234567 --output table
```

Create a relying party for `api.nodpublishers.com`, with tokens encrypted to `rp.cer`:

```bash
xbox xbl relying-party create api.nodpublishers.com --certificate-file rp.cer --seller-id 1234567
```

Change its tokens' lifetime to 4 hours:

```bash
xbox xbl relying-party save rp://api.nodpublishers.com/ --lifetime-hours 4 --seller-id 1234567
```

Replace its certificate:

```bash
xbox xbl relying-party save rp://api.nodpublishers.com/ --certificate-file rp-2028.cer --seller-id 1234567
```

Then publish the account's configuration, so players' tokens change:

```bash
xbox xbl config publish --scope account --seller-id 1234567
```

## Output

`get`, `create`, and `save` write the relying party:

```json
{
  "name": "rp://api.nodpublishers.com/",
  "fileName": "relyingparty-22222222-3333-4444-5555-666666666666.xml",
  "businessPartnerId": "11111111-2222-3333-4444-555555555555",
  "lifetimeInHours": 16,
  "encryptionType": "PublicKeyEncryption",
  "jkuEnabled": true,
  "claims": [
    "http://schemas.microsoft.com/xbox/2011/07/claims/user/agegroup",
    "http://schemas.microsoft.com/xbox/2011/07/claims/user/gamertag"
  ],
  "encryptionCertificate": {
    "thumbprint": "9F1C2D3E4F5A6B7C8D9E0F1A2B3C4D5E6F7A8B9C",
    "subject": "CN=api.nodpublishers.com",
    "notBefore": "2026-11-20T16:31:40+00:00",
    "notAfter": "2028-11-19T16:31:40+00:00"
  }
}
```

| Field | Value |
| --- | --- |
| `name` | Its name, the audience of its tokens. |
| `fileName` | Its document in the account's configuration, which [`xbl config download --scope account`](../xbl.md#config-download-and-config-save) downloads. |
| `businessPartnerId` | The business partner that owns it. |
| `lifetimeInHours` | How long its tokens last. |
| `encryptionType` | How its tokens are encrypted, such as `PublicKeyEncryption`, which `create` sets. `null` means its settings were never saved, so XBOX services don't issue its tokens; see [Errors](#errors). |
| `jkuEnabled` | Whether its tokens carry a `jku` header. |
| `claims` | The claim type URIs its tokens carry. |
| `encryptionCertificate` | The certificate its tokens are encrypted to: its SHA-1 `thumbprint`, its `subject`, and when it's valid, from `notBefore` to `notAfter`. The certificate itself isn't returned. |

`list` writes them as an array, by name. They come from the account's draft, so
they include changes that aren't published yet; [`config status --scope account`](../xbl.md#config-list-and-config-status)
shows what isn't. The table shows `NAME`, `BUSINESS PARTNER ID`, `LIFETIME HOURS`, and
`CERTIFICATE EXPIRES`, the date the certificate expires.

On stderr, `create` and `save` say how to publish the change:

```text
Saved to the account draft. Not published yet. Run 'xbox xbl config publish --scope account --seller-id 1234567' to publish it.
```

Before that line, they warn when the relying party's tokens don't carry a `jku` header, or when it
carries the upi claim, as Partner Center's relying party page does.

## Exit codes

| Exit code | Meaning |
| --- | --- |
| `0` | The command succeeded. |
| `1` | The command failed. For `create` and `save`, nothing changed: the request wasn't sent, or the service refused it. The exception is `relying_party_settings_not_saved`; see [Errors](#errors). |
| `3` | `create` or `save` was sent, but its outcome is unknown. Check the relying party with the `relying-party get` command that stderr shows. Running the same command again is safe. |

## Errors

A refused request exits `1` and writes a JSON error to stderr, as [Errors](../xbl.md#errors)
describes. These are the ones you're most likely to see:

| `code` | Cause |
| --- | --- |
| `relying_party_not_found` | The account has no relying party with that name. `list` shows their names. |
| `relying_party_exists` | `create` of a name the account already has. Change its settings with `save`. If its settings were never saved, run `create` again with the business partner that `detail` names as `--business-partner-id`. |
| `relying_party_name_taken` | Another account has the name. Names are unique across XBOX services, so choose another. |
| `relying_party_settings_not_saved` | `create` registered the name, but its settings weren't saved, so XBOX services don't issue its tokens. Fix what `detail` or `validation` names, then run `create` again with the same name and business partner. |
| `business_partner_not_found` | `--business-partner-id` names none of the account's business partners. [`business-partner list`](business-partner.md) lists them. |
| `validation_failed` | XBOX services refused the settings, such as a claim the business partner's type doesn't allow. `validation` lists each message. |
| `xbox_access_denied` | You don't have the Partner Center **Relying parties** Read permission, which `list` and `get` need. |
| `xbox_permission_denied` | You don't have the Partner Center **Relying parties** Write permission, which `create` and `save` need. |

A `save` also fails with HTTP 409 when the relying party changed while it ran. Run it again.
