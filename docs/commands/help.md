---
title: Help
description: Show the help for any command, the same text that passing --help prints.
---

# Help

Show help for any command.

```text
xbox help [command...]
```

`xbox help <command> [subcommand...]` prints the same text as
`xbox <command> [subcommand...] --help`. With no command, it prints the command overview, as
`xbox --help` and `xbox -h` do. A bare `xbox` shows the overview too, except the first time you
run a new version, when it shows the [launch screen](info.md#first-run-and-terminal-output).

```bash
xbox help playfab mps build
xbox playfab mps build --help   # same output
```

Help is text for people to read, whatever `--output` is. It labels Experimental commands, whose
options and output might change or be removed; see
[Experimental commands](../cli-command-lifecycle.md).

## Exit codes

Help writes to stdout and exits `0`. A usage error, such as an unknown command or option or a
missing required option, writes the error and usage to stderr, writes nothing to stdout, and
exits `1`. An unknown command also lists the commands available at that level; any other usage
error ends with the `xbox help` command to run for more.

```text
$ xbox completion nope
Argument 'nope' not recognized. Must be one of:
	'bash'
	'zsh'
	'fish'
	'powershell'

Usage:  xbox completion <shell> [flags]
Run 'xbox help completion' for more.
```
