---
title: XBOX services settings
description: Which configuration document holds each XBOX services title setting, access policy, privilege, and single sign-on endpoint, which scope it's in, how it goes live, and how to change it.
---

# XBOX services settings

Title settings, access policies, privileges, and single sign-on endpoints are XML documents
in a title's XBOX services configuration. Partner Center edits the same documents. With the
XBOX CLI, you download them with [`xbox xbl config`](commands/xbl.md), edit the
XML, save it, and publish it.

This page says where each setting lives and how a change goes live. For what each feature
does, see
[Portal configuration overview](https://docs.xbox.com/services/xbox-services/fundamentals/portal-config/live-portal-config-overview).
For every element and value a document accepts, download its schema with
`xbox xbl schema download --type <type> --dest schemas`. The `xbl` commands are
Experimental, so their names and options might change.

## Find a setting

| Setting | Document | Scope | Goes live with |
| --- | --- | --- | --- |
| [Title names](#title-names) | `productdeveloper.xml` | Title | [The title-wide publish](#change-a-title-setting) |
| [Platforms](#platforms) | `productdeveloper.xml` | Title | The title-wide publish |
| [Embargo](#embargo) | `productdeveloper.xml` | Title | The title-wide publish |
| [Multiple points of presence](#multiple-points-of-presence) | `productdeveloper.xml` | Title | The title-wide publish |
| [Business partner](#business-partner) | `productsystem.xml` | Title | The title-wide publish |
| [Stats and achievements systems](#stats-and-achievements-systems) | `dataplatformconfig.xml` | Title | [Each sandbox's next publish](#change-a-title-setting) |
| [Privileges](#privileges) | `titlepriv.xml` | Title | Each sandbox's next publish |
| [Single sign-on endpoints](#single-sign-on-endpoints) | `nsal.xml` | Title | Each sandbox's next publish |
| [Access policies](#access-policies) | `accesspolicies.xml` | Sandbox | [That sandbox's publish](#change-a-setting-in-one-sandbox) |
| [Cross-title multiplayer](#cross-title-multiplayer) | `crosstitlemultiplayer.xml` and `accesspolicies.xml` | Sandbox | That sandbox's publish |

## How a change goes live

A **sandbox** setting applies to one title in one sandbox. A **title** setting applies to
one title in every sandbox, RETAIL included. Every scope has a **draft**, which
`config save` changes and your team and Partner Center see at once, and a **published** version,
which `config publish` changes and players and test accounts see.

The **title draft** holds every title setting. It goes live in two ways:

```text
  save to the title draft ──▶ title draft
                                │
                                ├─ title-wide publish ──────────▶ live in every sandbox at once
                                │    (productdeveloper.xml, productsystem.xml,
                                │     globalaccesspolicies.xml)
                                │
                                └─ any publish of a sandbox ────▶ live in that sandbox
                                     (dataplatformconfig.xml, titlepriv.xml, nsal.xml)
```

Each publish takes the whole draft, including teammates' unpublished changes.
`config save --file` sends only the files you name, so the other documents keep their draft
content. If a publish reports `AlreadyPublished`, none of the documents that publish takes
changed since its last publish; see [Republishing](commands/xbl.md#republishing) before you
use `--republish`.

`--scid` takes the title's service configuration ID (SCID). `config save` and
`config publish` change a real title, so agents need the user's approval first. See
[Safety and destructive operations](safety.md).

## Change a setting in one sandbox

This example lets Nod Rally read Nod Arena's leaderboards in sandbox XDKS.1. Cross-title
multiplayer follows the same steps with [two files](#cross-title-multiplayer).

1. Download the sandbox's draft:

   ```bash
   xbox xbl config download --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --sandbox-id XDKS.1 --view draft --dest nod-arena
   ```

2. Edit `nod-arena/accesspolicies.xml`. See [Access policies](#access-policies).

3. Validate the change:

   ```bash
   xbox xbl config save --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --sandbox-id XDKS.1 --file nod-arena/accesspolicies.xml --validate-only
   ```

4. If validation reports no errors, save it:

   ```bash
   xbox xbl config save --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --sandbox-id XDKS.1 --file nod-arena/accesspolicies.xml --message "Let Nod Rally read leaderboards"
   ```

5. Publish the sandbox:

   ```bash
   xbox xbl config publish --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --sandbox-id XDKS.1
   ```

## Change a title setting

Title settings follow the same steps against the title draft, with `--scope title`. This
example ends the embargo in `productdeveloper.xml`.

> [!WARNING]
> Once you save a title setting, anyone's next publish, from the CLI or Partner Center, can make
> it live in any sandbox, RETAIL included. You don't choose which.

1. Download the title draft:

   ```bash
   xbox xbl config download --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --scope title --view draft --dest nod-arena-title
   ```

2. Edit the embargo in `nod-arena-title/productdeveloper.xml`. See [Embargo](#embargo).

3. Validate the change:

   ```bash
   xbox xbl config save --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --scope title --file nod-arena-title/productdeveloper.xml --validate-only
   ```

4. If validation reports no errors, save it:

   ```bash
   xbox xbl config save --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --scope title --file nod-arena-title/productdeveloper.xml --message "End the embargo"
   ```

To see every document in the title draft, including teammates' saves, where each goes live,
and what a title-wide publish would change:

```bash
xbox xbl config status --scope title --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --output table
```

How the change goes live depends on the document:

- **The title-wide publish** makes `productdeveloper.xml`, `productsystem.xml`, and
  `globalaccesspolicies.xml` live in every sandbox at once. The same `config status --scope title`
  compares them with what's published, and reports the publish's result in `lastPublish`.

  > [!WARNING]
  > The title-wide publish reaches RETAIL players as soon as it finishes. It publishes the
  > title draft's copies of those three documents, including teammates' unpublished edits,
  > so review the title draft's status first.

  To publish them, which asks first:

  ```bash
  xbox xbl config publish --scope title --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee
  ```

- **Each sandbox's next publish** makes `dataplatformconfig.xml`, `titlepriv.xml`, and
  `nsal.xml` live in that sandbox. A sandbox's `config status` lists these documents with
  `"scope": "title"`, so it shows which title changes its next publish takes.

  After you save, check what the sandbox you test in will take:

  ```bash
  xbox xbl config status --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --sandbox-id XDKS.1
  ```

  Then publish it:

  ```bash
  xbox xbl config publish --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --sandbox-id XDKS.1
  ```

## Title settings

### Title names

Excerpt of `productdeveloper.xml`:

```xml
<DevDisplayLocale>en-US</DevDisplayLocale>
<LocalizedTitleNames>
  <LocalizedValue locale="en-US">Nod Arena</LocalizedValue>
  <LocalizedValue locale="fr-FR">Nod Arène</LocalizedValue>
</LocalizedTitleNames>
```

Each locale appears once with a non-empty name, and the `DevDisplayLocale` locale must keep
its name. These names are separate from the game's Store listing.

### Platforms

Excerpt of `productdeveloper.xml`:

```xml
<Platforms>
  <Platform>XboxOne</Platform>
  <Platform>Scarlett</Platform>
  <Platform>WindowsOneCore</Platform>
</Platforms>
```

Partner Center offers `XboxOne`, `Scarlett` (XBOX Series X|S), `WindowsOneCore` (PC),
`WindowsOneCoreMobile`, `iOS`, and `Android`, and keeps other values already in the
document, so leave values you don't recognize in place. At least one platform is required.

### Embargo

Excerpt of `productdeveloper.xml`:

```xml
<XboxLiveEmbargoDate>2027-03-01T17:00:00Z</XboxLiveEmbargoDate>
<EmbargoInvitesEnabled>true</EmbargoInvitesEnabled>
```

An embargo keeps the title's XBOX services configuration from being publicly visible in
RETAIL until a UTC date, so you can publish to RETAIL before launch.
`EmbargoInvitesEnabled` allows multiplayer invites during it. To remove an embargo, set the
date to `0001-01-01T00:00:00Z` and `EmbargoInvitesEnabled` to `false`, as Partner Center
does. Titles whose `XboxLiveTier` in `productsystem.xml` is `Open` can't set one.

### Multiple points of presence

Excerpt of `productdeveloper.xml`:

```xml
<MultiplePointsOfPresenceEnabled>false</MultiplePointsOfPresenceEnabled>
```

Multiple points of presence lets a player be signed in to the title on several devices at
once. Partner Center doesn't recommend it for games, because it limits achievements and
multiplayer. `Open`-tier titles can't turn it on.

### Business partner

Excerpt of `productsystem.xml`:

```xml
<BusinessPartnerId>11111111-2222-3333-4444-555555555555</BusinessPartnerId>
```

It must be one of the seller account's business partners, which
[`xbox xbl business-partner list`](commands/xbl/business-partner.md) lists. XBOX sets every other
field in `productsystem.xml`, and changing one fails validation.

### Stats and achievements systems

`dataplatformconfig.xml`:

```xml
<DataPlatformConfig xmlns="http://config.mgt.xboxlive.com/schema/dataplatformconfig/1">
  <StatSystem>StatsLeaderboards2017</StatSystem>
  <AchievementSystem skipRetailPublish="false">Achievements2017</AchievementSystem>
</DataPlatformConfig>
```

`skipRetailPublish="false"` publishes achievements to RETAIL. New titles use the values
shown.

Partner Center offers each title two stats systems:

| Partner Center option | `StatSystem` |
| --- | --- |
| **Event-Based Stats** | `UserStats2013`, or `UserStats2015` for a title already on it |
| **Title-Managed Stats** | `StatsLeaderboards2017` |

Partner Center warns that a title can't use Sets, which `sets.xml` defines, after it switches to
Title-Managed Stats.

A `config save --scope title` that sets a stats system Partner Center doesn't offer the title
fails with HTTP 409 and the code `stats_system_change_not_allowed`. That's `UserStats2015` for a
title that isn't on it, and `UserStats2013` for a title on `UserStats2015`. `detail` names the
ones the title can use.

> [!WARNING]
> Decide before RETAIL publishes achievements. After that, `AchievementSystem`,
> `skipRetailPublish`, and `StatSystem` can't change, and achievements published to RETAIL
> can't be rolled back.

A `config save --scope title` that changes a locked `StatSystem` fails with HTTP 409 and the code
`stats_system_locked`, and `detail` names RETAIL's stats system. So does every publish except
the title-wide one, development sandboxes included, while the title draft holds a different
`StatSystem`. To publish again, save `dataplatformconfig.xml` with RETAIL's `StatSystem`.

A `config save --scope title` of `dataplatformconfig.xml`, or a publish other than the title-wide
one, fails with HTTP 409 and the code `stats_system_check_failed` when the service can't read
RETAIL's published `dataplatformconfig.xml` to check the stats system. Nothing changed. Try again
later.

- `Achievements2013` doesn't work with `StatsLeaderboards2017`.
- `skipRetailPublish="true"` doesn't work with the `XboxOne`, `Scarlett`, or
  `WindowsOneCore` platforms.
- After you switch stats systems, a sandbox that still holds stats documents for the old
  system fails to publish until you remove them.
- Titles with `IsXblLimited` set in `productsystem.xml` can't use achievements or the
  `UserStats2013` and `UserStats2015` stats systems.

## Privileges

A privilege controls what the title's own players can do, such as broadcast it. The IDs are
the XBOX Game Development Kit (GDK)
[`XUserPrivilege`](https://learn.microsoft.com/gaming/gdk/docs/reference/system/xuser/enums/xuserprivilege)
values:

| ID | GDK name | Partner Center label |
| --- | --- | --- |
| 190 | `Broadcast` | Broadcasting |
| 189 | `Sessions` | Single-player experiences in shared environments |
| 254 | `Multiplayer` | Online multiplayer sessions with live players |

Excerpt of `titlepriv.xml` that stops players broadcasting the title in every sandbox:

```xml
<DefaultPrivileges>
  <Restrictions>
    <Restriction Privilege="190" />
  </Restrictions>
</DefaultPrivileges>
```

- `DefaultPrivileges` applies to every sandbox. A `SandboxPrivileges` entry under
  `SandboxOverrides` applies to the sandbox in its `SandboxName`.
- Version 2 of the document (namespace ending `titlepriv/2`) writes privileges as numbers
  and is what Partner Center writes. Version 1 writes names, such as
  `Privilege="Broadcast"`. Edit the version your file uses.

## Single sign-on endpoints

Single sign-on lets your game sign players in to your own web service. When the game calls
`XUserGetTokenAndSignatureAsync` for a URL, XBOX matches it against the title's endpoints
and returns a token for that endpoint's relying party. The relying party belongs to the
seller account; create it with [`xbox xbl relying-party create`](commands/xbl/relying-party.md) or on
Partner Center's **Relying parties** page.

Excerpt of `nsal.xml`:

```xml
<DefaultNsal>
  <Endpoint>
    <Scheme>https</Scheme>
    <Host>api.nodpublishers.com</Host>
    <Path>/arena/</Path>
    <HostType>FQDN</HostType>
    <RelyingParty TokenType="JWT">rp://api.nodpublishers.com/</RelyingParty>
  </Endpoint>
</DefaultNsal>
```

- Add endpoints to `DefaultNsal`. New titles use only that list; `CertificationNsal`,
  `RetailNsal`, and `Sandboxes` are older per-sandbox lists.
- `HostType` is `FQDN` (fully qualified domain name), or `WILDCARD` when `Host` contains `*`.
- `RelyingParty` is the relying party's name, as `xbox xbl relying-party list` shows it.
  Partner Center writes `TokenType="JWT"` (JSON Web Token).
- Partner Center allows at most 50 endpoints in a list.

## Access policies

An access policy lets another game, app, or service use this title's XBOX services data in
one sandbox. Each `AccessPolicy` names a feature, each `Permission` a level of access
(`FullAccess` or `ReadOnly`), and each `Principal` who gets it. `THIS` is the title itself.

Excerpt of `accesspolicies.xml` that lets Nod Rally read Nod Arena's leaderboards:

```xml
<AccessPolicy Scope="Leaderboards">
  <Permission Type="FullAccess">
    <Principal Type="Product">THIS</Principal>
  </Permission>
  <Permission Type="ReadOnly">
    <Principal Type="Product">Nod.Rally_abc123</Principal>
  </Permission>
</AccessPolicy>
```

- A game principal is its package family name. For your own games,
  `xbox game get --store-id <store-id>` returns it as `packageFamilyName`.
- A web service principal is `Type="Service"`, with the web service's ID, which
  [`xbox xbl web-service list`](commands/xbl/web-service.md) shows.
- Each feature appears once, each permission type once per feature, and each principal
  under one permission per feature. Every permission lists at least one principal.
- Keep `THIS` in every policy, as Partner Center does, so the title keeps access to its own
  data.
- Features include `Achievements`, `GlobalStorage`, `Leaderboards`, `Multiplayer.Runtime`,
  `Presence`, `Stats`, and `UniversalStorage`.

## Cross-title multiplayer

Cross-title multiplayer lets players of another title join this title's multiplayer
sessions. It takes two entries in the host title's sandbox, saved together; with only the
first, the other title's players are refused when they join:

1. The other title's ID in `crosstitlemultiplayer.xml`, in **decimal**. `xbox game get`
   shows it in hexadecimal as `xboxTitleId`. To convert it, in bash or zsh:

   ```bash
   printf '%d\n' 0x1234ABCD
   ```

   In PowerShell:

   ```powershell
   [Convert]::ToInt64('1234ABCD', 16)
   ```

2. A `Multiplayer.Runtime` grant to the other game in `accesspolicies.xml`.

This lets Nod Rally (title ID `1234ABCD`, package family name `Nod.Rally_abc123`) join Nod
Arena's sessions:

```xml
<CrossTitleMultiplayer xmlns="http://config.mgt.xboxlive.com/schema/crosstitlemultiplayer/1">
  <ChildTitleIds>
    <TitleId>305441741</TitleId>
  </ChildTitleIds>
</CrossTitleMultiplayer>
```

```xml
<AccessPolicy Scope="Multiplayer.Runtime">
  <Permission Type="FullAccess">
    <Principal Type="Product">THIS</Principal>
    <Principal Type="Product">Nod.Rally_abc123</Principal>
  </Permission>
</AccessPolicy>
```

Pass both files to `config save`, each with its own `--file`. Validation rejects a title ID
listed twice, the host title's own ID, and a title ID that doesn't exist.
