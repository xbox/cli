# bash completion for the XBOX CLI. Load it with: eval "$(xbox completion bash)"
_xbox_complete() {
    local line="${COMP_LINE:0:COMP_POINT}"
    line="${line#"${line%%[![:space:]]*}"}"
    if [[ "$line" == *[[:space:]]* ]]; then line="${line#*[[:space:]]}"; else line=""; fi
    local IFS=$'\n'
    COMPREPLY=($(compgen -W "$(xbox "[suggest:${#line}]" "$line" 2>/dev/null)" -- "${COMP_WORDS[COMP_CWORD]}"))
}
complete -o default -F _xbox_complete xbox
