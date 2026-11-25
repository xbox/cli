# fish completion for the XBOX CLI. Load it with: xbox completion fish | source
function __xbox_complete
    set -l tokens (commandline -opc)
    set -e tokens[1]
    set -l current (commandline -ct)
    set -l line (string join ' ' -- $tokens "$current")
    set -l candidates (xbox "[suggest:"(string length -- "$line")"]" "$line" 2>/dev/null | string match -v -- '')
    if set -q candidates[1]
        printf '%s\n' $candidates
    else
        __fish_complete_path "$current"
    end
end
complete -c xbox -f -a '(__xbox_complete)'
