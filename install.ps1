# Installs the `modelbench` CLI and dashboard into one folder and puts it on your PATH.
# Usage: ./install.ps1 [-Dest <folder>]      Re-run after pulling to update.
# Works with Windows PowerShell 5.1 and PowerShell 7 (pwsh, also on macOS/Linux).
param([string]$Dest = (Join-Path $HOME '.modelbench/bin'))
$ErrorActionPreference = 'Stop'
# Full path with every existing symlink or junction resolved: the path itself, any linked ancestor, and link chains.
# Comparing these real paths stops an alias of the repository from passing the guard below.
function Resolve-RealPath([string]$path, [int]$depth = 0) {
    if ($depth -gt 32) { throw "too many levels of links while resolving '$path'" }
    $full = [IO.Path]::GetFullPath($path)
    $cur = [IO.Path]::GetPathRoot($full)
    $parts = @($full.Substring($cur.Length) -split '[\\/]' | Where-Object { $_ })
    for ($i = 0; $i -lt $parts.Count; $i++) {
        $next = Join-Path $cur $parts[$i]
        $item = Get-Item -LiteralPath $next -Force -ErrorAction SilentlyContinue
        if (-not $item) { # the rest does not exist yet, so it cannot be a link
            foreach ($p in ($parts | Select-Object -Skip ($i + 1))) { $next = Join-Path $next $p }
            return [IO.Path]::GetFullPath($next)
        }
        if ($item.LinkType -in 'SymbolicLink', 'Junction') {
            $target = @($item.Target)[0]
            if (-not [IO.Path]::IsPathRooted($target)) { $target = Join-Path $cur $target }
            $next = Resolve-RealPath $target ($depth + 1)
        }
        $cur = $next
    }
    $cur.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
}

$repo = Resolve-RealPath $PSScriptRoot
$Dest = Resolve-RealPath $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Dest)

# The install step below deletes $Dest/benchmarks and $Dest/sandbox. Refuse any -Dest where that could touch the repository
# (the repo itself, a folder inside it, a parent whose benchmarks/sandbox folder holds it, or a link to any of those),
# before anything is published or deleted.
function Test-Within([string]$path, [string]$dir) {
    $path.Equals($dir, 'OrdinalIgnoreCase') -or $path.StartsWith($dir + [IO.Path]::DirectorySeparatorChar, 'OrdinalIgnoreCase')
}
if (Test-Within $Dest $repo) { throw "-Dest '$Dest' is inside the repository '$repo'; choose a folder outside it" }
foreach ($d in 'benchmarks', 'sandbox') {
    $t = Resolve-RealPath (Join-Path $Dest $d)
    if ((Test-Within $repo $t) -or (Test-Within $t $repo)) { throw "-Dest '$Dest' would replace '$t', which overlaps the repository; choose another folder" }
}

dotnet publish "$repo/src/ModelBench.Cli" -c Release -o $Dest --nologo -v quiet
if ($LASTEXITCODE) { exit $LASTEXITCODE }
dotnet publish "$repo/src/ModelBench.Web" -c Release -o $Dest --nologo -v quiet
if ($LASTEXITCODE) { exit $LASTEXITCODE }

# Benchmark definitions and sandbox Dockerfiles are found by walking up from the executable, so ship them alongside it.
foreach ($d in 'benchmarks', 'sandbox') {
    $target = Join-Path $Dest $d
    if (Test-Path $target) { Remove-Item $target -Recurse -Force }
    Copy-Item (Join-Path $repo $d) $target -Recurse
    Get-ChildItem $target -Recurse -Directory -Include bin, obj | Remove-Item -Recurse -Force
}

$sep = [IO.Path]::PathSeparator
$userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
if ($IsLinux -or $IsMacOS) {
    Write-Host "Installed to $Dest. Add it to PATH in your shell profile:  export PATH=`"$Dest`:`$PATH`""
} elseif (($userPath -split $sep) -notcontains $Dest) {
    [Environment]::SetEnvironmentVariable('Path', ($userPath.TrimEnd($sep) + $sep + $Dest), 'User')
    Write-Host "Installed to $Dest and added it to your user PATH. Open a new terminal to use 'modelbench'."
} else {
    Write-Host "Updated $Dest."
}
Write-Host "Next: modelbench sandbox build   (once, needs Docker)   then: modelbench web"
