# PowerShell completion for the XBOX CLI. Load it with: xbox completion powershell | Out-String | Invoke-Expression
Register-ArgumentCompleter -Native -CommandName xbox -ScriptBlock {
    param($wordToComplete, $commandAst, $cursorPosition)
    $line = $commandAst.Extent.Text.PadRight($cursorPosition - $commandAst.Extent.StartOffset)
    $line = $line.Substring(0, $cursorPosition - $commandAst.Extent.StartOffset)
    $skip = [Math]::Min($commandAst.CommandElements[0].Extent.EndOffset - $commandAst.Extent.StartOffset + 1, $line.Length)
    $line = $line.Substring($skip)
    xbox "[suggest:$($line.Length)]" "$line" 2>$null |
        Where-Object { $_ -like "$wordToComplete*" } |
        ForEach-Object { [System.Management.Automation.CompletionResult]::new($_, $_, 'ParameterValue', $_) }
}
