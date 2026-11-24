---
title: Completion
description: Print a script that makes your shell complete XBOX CLI commands, options, and values when you press Tab.
---

# Completion

Print a shell completion script, so pressing Tab completes `xbox` commands, options, and
values from a fixed list.

```bash
xbox completion <shell>
```

`<shell>` is `bash`, `zsh`, `fish`, or `powershell`. The command writes the script to stdout
and exits `0`. It makes no network request and doesn't need you to sign in. It doesn't accept
`--output table`.

## What completes

- Commands and subcommands at every depth, such as `play` to `playfab` or `playtest`.
- The options a command takes, including the global ones.
- Values from a fixed list, such as `--output` formats, `--supported-device` values, the
  `--scope` values of `xbl config` commands, the `--view` values of `xbl config download`,
  and the keys `context set` and `context unset` take.

Completion doesn't look up resources such as playtests, sandboxes, or builds, or the profiles
you saved, so those are still typed.

## Enable completion

Add the line for your shell to its startup file, then open a new terminal. To try it in the
current terminal first, run the same line there.

Bash, in `~/.bashrc`:

```bash
eval "$(xbox completion bash)"
```

Zsh, in `~/.zshrc`, after `compinit` runs:

```zsh
source <(xbox completion zsh)
```

fish, in `~/.config/fish/config.fish`:

```fish
xbox completion fish | source
```

PowerShell, in the file `$PROFILE` names:

```powershell
xbox completion powershell | Out-String | Invoke-Expression
```

The script completes the command named `xbox`, so the CLI must be on `PATH` under that name.

## How it works

Each script asks the CLI for candidates whenever you press Tab, by running `xbox` with the
command line typed so far. The CLI answers from its own command tree. It doesn't read
configuration or saved context, sign you in, send telemetry, or write anything.
