param(
    [Parameter(Mandatory=$true)]
    [ValidateSet('list','status','apply','revert','revert-all')]
    [string]$Action,
    [string[]]$Id = @(),
    [switch]$IncludeAggressive,
    [switch]$Json
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'OptimizadorCore.psm1') -Force -DisableNameChecking

switch ($Action) {
    'list' {
        $result = @(Get-OptimizadorCatalog)
    }
    'status' {
        $state = Get-OptimizadorState
        $result = foreach ($t in Get-OptimizadorCatalog) {
            [pscustomobject]@{
                id       = $t.id
                name     = $t.name
                risk     = $t.risk
                applied  = ($state.applied.PSObject.Properties.Name -contains $t.id)
                actions  = if ($state.applied.PSObject.Properties.Name -contains $t.id) { @($state.applied.$t.id.actions).Count } else { 0 }
            }
        }
    }
    'apply' {
        if (-not $Id) { throw 'Debe indicar -Id (o use un bucle desde su app).' }
        $result = foreach ($i in $Id) { Apply-Optimizacion -Id $i -IncludeAggressive:$IncludeAggressive }
    }
    'revert' {
        if (-not $Id) { throw 'Debe indicar -Id.' }
        $result = foreach ($i in $Id) { Restore-Optimizacion -Id $i -IncludeAggressive:$IncludeAggressive }
    }
    'revert-all' {
        $result = Restore-All -IncludeAggressive:$IncludeAggressive
    }
}

if ($Json) {
    $result | ConvertTo-Json -Depth 12
} else {
    $result | Format-Table -AutoSize | Out-String -Width 220
}
