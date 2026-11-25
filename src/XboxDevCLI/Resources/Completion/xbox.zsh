#compdef xbox
# zsh completion for the XBOX CLI. Load it with: source <(xbox completion zsh)
_xbox() {
    local line="${(j: :)words[2,CURRENT]}"
    local -a completions
    completions=(${(f)"$(xbox "[suggest:${#line}]" "$line" 2>/dev/null)"})
    if (( $#completions )); then
        compadd -- "${completions[@]}"
    else
        _files
    fi
}
compdef _xbox xbox
