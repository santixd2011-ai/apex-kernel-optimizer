# OptimizadorCore.psm1
# Nucleo de optimizacion derivado del analisis REA de NoPing / Atlas Toolbox / KernelOS Toolbox.
# Diseno: catalogo declarativo + snapshots + Apply/Revert. Sin ejecucion automatica.
# Estado: %LOCALAPPDATA%\MiApp\Optimizador\state.json (legible por cualquier lenguaje).

$script:StateDir  = Join-Path $env:LOCALAPPDATA 'MiApp\Optimizador'
$script:StatePath = Join-Path $script:StateDir 'state.json'

function Test-IsAdmin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    (New-Object Security.Principal.WindowsPrincipal($id)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Get-OptimizadorState {
    if (Test-Path $script:StatePath) {
        Get-Content $script:StatePath -Raw | ConvertFrom-Json
    } else {
        [pscustomobject]@{ version = 1; applied = @{} }
    }
}

function Save-OptimizadorState {
    param([Parameter(Mandatory)] $State)
    if (-not (Test-Path $script:StateDir)) { New-Item -ItemType Directory -Path $script:StateDir -Force | Out-Null }
    $State | ConvertTo-Json -Depth 12 | Set-Content $script:StatePath -Encoding UTF8
}

function Get-RegValue {
    param([Parameter(Mandatory)] $Path, [Parameter(Mandatory)] $Name)
    if (-not (Test-Path $Path)) { return [pscustomobject]@{ Exists = $false; Value = $null; Kind = $null } }
    $key = Get-Item -Path $Path
    if ($key.GetValueNames() -notcontains $Name) { return [pscustomobject]@{ Exists = $false; Value = $null; Kind = $null } }
    [pscustomobject]@{ Exists = $true; Value = $key.GetValue($Name); Kind = $key.GetValueKind($Name).ToString() }
}

function Add-SnapshotAction {
    param([Parameter(Mandatory)] $State, [Parameter(Mandatory)] $Id, [Parameter(Mandatory)] $Action)
    if (-not $State.applied.PSObject.Properties.Name -contains $Id) {
        $State.applied | Add-Member -NotePropertyName $Id -NotePropertyValue ([pscustomobject]@{ actions = @() }) -Force
    }
    $tweak = $State.applied.$Id
    $tweak.actions = @($tweak.actions) + @($Action)
}

function Set-RegDword {
    param([Parameter(Mandatory)] $State, [Parameter(Mandatory)] $Id,
          [Parameter(Mandatory)] $Path, [Parameter(Mandatory)] $Name, [Parameter(Mandatory)] $Value)
    $before = Get-RegValue -Path $Path -Name $Name
    if (-not (Test-Path $Path)) { New-Item -Path $Path -Force | Out-Null }
    New-ItemProperty -Path $Path -Name $Name -Value $Value -PropertyType DWord -Force | Out-Null
    Add-SnapshotAction -State $State -Id $Id -Action ([pscustomobject]@{
        type = 'registry'; path = $Path; name = $Name
        beforeExists = $before.Exists; beforeValue = $before.Value; beforeKind = $before.Kind
        afterValue = $Value
    })
}

function Set-ServiceStart {
    param([Parameter(Mandatory)] $State, [Parameter(Mandatory)] $Id,
          [Parameter(Mandatory)] $Service, [Parameter(Mandatory)] $StartType)
    # StartType: demand=3, disabled=4, auto=2
    $svc = Get-Service -Name $Service -ErrorAction SilentlyContinue
    if (-not $svc) { throw "Servicio no encontrado: $Service" }
    $before = (Get-CimInstance Win32_Service -Filter "Name='$Service'").StartMode
    & sc.exe config $Service start= $StartType | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "sc.exe config fallo para $Service" }
    Add-SnapshotAction -State $State -Id $Id -Action ([pscustomobject]@{
        type = 'service'; service = $Service
        beforeStartMode = $before; afterStartMode = $StartType
    })
}

function Set-PowerPlan {
    param([Parameter(Mandatory)] $State, [Parameter(Mandatory)] $Id, [Parameter(Mandatory)] $Guid)
    $before = (powercfg /getactivescheme) -replace '.*GUID:\s*([0-9a-fA-F-]+).*','$1'
    powercfg /setactive $Guid | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "powercfg /setactive fallo para $Guid" }
    Add-SnapshotAction -State $State -Id $Id -Action ([pscustomobject]@{
        type = 'powerplan'; beforeGuid = $before; afterGuid = $Guid
    })
}

function Test-HasSMT {
    $cpus = Get-CimInstance Win32_Processor
    foreach ($c in $cpus) { if ([int]$c.NumberOfLogicalProcessors -gt [int]$c.NumberOfCores) { return $true } }
    $false
}

function Test-IsLaptop {
    $b = Get-CimInstance Win32_Battery -ErrorAction SilentlyContinue
    [bool]$b
}

# Catalogo curado. risk: safe | moderate | aggressive (aggressive requiere -IncludeAggressive).
function Get-OptimizadorCatalog {
    @(
        [pscustomobject]@{ id='mmcss_system_responsiveness'; name='MMCSS: prioridad al juego'; risk='safe';
            source='NoPing (observado en vivo: 10 -> 0)'; detail='HKLM ...\Multimedia\SystemProfile\SystemResponsiveness = 0' }
        [pscustomobject]@{ id='network_throttling_off'; name='Quitar throttling de red'; risk='safe';
            source='NoPing (observado en vivo: 10 -> 0xFFFFFFFF)'; detail='HKLM ...\Multimedia\SystemProfile\NetworkThrottlingIndex = 4294967295' }
        [pscustomobject]@{ id='mmcss_no_lazy'; name='MMCSS sin modo lazy'; risk='safe';
            source='NoPing / KernelOS'; detail='HKLM ...\Multimedia\SystemProfile\NoLazyMode = 1' }
        [pscustomobject]@{ id='power_throttling_off'; name='Desactivar Power Throttling'; risk='safe';
            source='NoPing / Atlas'; detail='HKLM ...\Control\Power\PowerThrottling\PowerThrottlingOff = 1' }
        [pscustomobject]@{ id='enable_transparency_off'; name='Desactivar transparencia'; risk='safe';
            source='NoPing / Atlas'; detail='HKCU ...\Themes\Personalize\EnableTransparency = 0' }
        [pscustomobject]@{ id='background_apps_off'; name='Desactivar apps en segundo plano'; risk='safe';
            source='NoPing / Atlas (script real)'; detail='HKCU ...\BackgroundAccessApplications\GlobalUserDisabled = 1' }
        [pscustomobject]@{ id='fso_gamebar_off'; name='Desactivar FSO y Game Bar'; risk='moderate';
            source='Atlas (script real)'; detail='GameConfigStore + GameDVR + Políticas; quita Xbox Gaming Overlay' }
        [pscustomobject]@{ id='web_search_off'; name='Desactivar búsqueda web'; risk='moderate';
            source='Atlas (script real)'; detail='BingSearchEnabled=0, DisableWebSearch=1, SearchHost.exe' }
        [pscustomobject]@{ id='power_plan_high'; name='Plan de energía Alto Rendimiento'; risk='moderate';
            source='NoPing (observado en vivo)'; detail='8C5E7FDA-E8BF-4A96-9A85-A6E23A8C635C; guarda el plan anterior' }
        [pscustomobject]@{ id='cpu_idle_off'; name='Desactivar idle de CPU'; risk='moderate';
            source='Atlas (script real)'; detail='powercfg /setacvalueindex scheme_current sub_processor 5D76A2CA-... 1; omitido si hay SMT' }
        [pscustomobject]@{ id='xbox_services_off'; name='Desactivar servicios Xbox'; risk='moderate';
            source='NoPing (observado en vivo: 3 -> 4)'; detail='XblAuthManager, XblGameSave, XboxGipSvc, XboxNetApiSvc' }
        [pscustomobject]@{ id='telemetry_services_off'; name='Parar servicios de telemetría/indexación'; risk='aggressive';
            source='NoPing / Atlas'; detail='DiagTrack, WSearch, SysMain, PcaSvc (StartType=4)' }
        [pscustomobject]@{ id='windows_update_off'; name='Desactivar Windows Update'; risk='aggressive';
            source='NoPing / Atlas (script real)'; detail='wuauserv start=disabled + tareas sih/sihboot' }
    )
}

function Apply-Optimizacion {
    param([Parameter(Mandatory)] $Id, [switch]$IncludeAggressive)
    $item = Get-OptimizadorCatalog | Where-Object id -eq $Id
    if (-not $item) { throw "ID desconocido: $Id" }
    if ($item.risk -eq 'aggressive' -and -not $IncludeAggressive) {
        return [pscustomobject]@{ id=$Id; status='skipped'; reason='aggressive requiere -IncludeAggressive' }
    }
    if (-not (Test-IsAdmin)) { throw 'Se requieren privilegios de administrador' }

    $state = Get-OptimizadorState
    if ($state.applied.PSObject.Properties.Name -contains $Id) {
        return [pscustomobject]@{ id=$Id; status='already_applied' }
    }

    switch ($Id) {
        'mmcss_system_responsiveness' {
            Set-RegDword $state $Id 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile' 'SystemResponsiveness' 0
        }
        'network_throttling_off' {
            Set-RegDword $state $Id 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile' 'NetworkThrottlingIndex' 4294967295
        }
        'mmcss_no_lazy' {
            Set-RegDword $state $Id 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile' 'NoLazyMode' 1
        }
        'power_throttling_off' {
            Set-RegDword $state $Id 'HKLM:\SYSTEM\CurrentControlSet\Control\Power\PowerThrottling' 'PowerThrottlingOff' 1
        }
        'enable_transparency_off' {
            Set-RegDword $state $Id 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize' 'EnableTransparency' 0
        }
        'background_apps_off' {
            Set-RegDword $state $Id 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications' 'GlobalUserDisabled' 1
            Set-RegDword $state $Id 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Search' 'BackgroundAppGlobalToggle' 0
        }
        'fso_gamebar_off' {
            $gc = 'HKCU:\System\GameConfigStore'
            Set-RegDword $state $Id $gc 'GameDVR_DSEBehavior' 2
            Set-RegDword $state $Id $gc 'GameDVR_DXGIHonorFSEWindowsCompatible' 1
            Set-RegDword $state $Id $gc 'GameDVR_EFSEFeatureFlags' 0
            Set-RegDword $state $Id $gc 'GameDVR_FSEBehavior' 2
            Set-RegDword $state $Id $gc 'GameDVR_FSEBehaviorMode' 2
            Set-RegDword $state $Id $gc 'GameDVR_HonorUserFSEBehaviorMode' 1
            Set-RegDword $state $Id $gc 'GameDVR_Enabled' 0
            Set-RegDword $state $Id 'HKCU:\System\GameBar' 'ShowStartupPanel' 0
            Set-RegDword $state $Id 'HKCU:\System\GameBar' 'UseNexusForGameBarEnabled' 0
            Set-RegDword $state $Id 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\GameDVR' 'AllowGameDVR' 0
            Set-RegDword $state $Id 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\GameDVR' 'AppCaptureEnabled' 0
            Set-RegDword $state $Id 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Environment' '__COMPAT_LAYER' '~ DISABLEDXMAXIMIZEDWINDOWEDMODE'
        }
        'web_search_off' {
            Set-RegDword $state $Id 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Search' 'BingSearchEnabled' 0
            Set-RegDword $state $Id 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\Windows Search' 'DisableWebSearch' 1
            Set-RegDword $state $Id 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Search' 'SearchboxTaskbarMode' 1
            Set-RegDword $state $Id 'HKCU:\SOFTWARE\Policies\Microsoft\Windows\Explorer' 'DisableSearchBoxSuggestions' 1
        }
        'power_plan_high' { Set-PowerPlan $state $Id '8C5E7FDA-E8BF-4A96-9A85-A6E23A8C635C' }
        'cpu_idle_off' {
            if (Test-HasSMT) { return [pscustomobject]@{ id=$Id; status='skipped'; reason='SMT/HyperThreading detectado (guarda de Atlas)' } }
            & powercfg /setacvalueindex scheme_current sub_processor 5D76A2CA-E8C0-402F-A133-2158492D58AD 1 | Out-Null
            & powercfg /setactive scheme_current | Out-Null
            Add-SnapshotAction -State $state -Id $Id -Action ([pscustomobject]@{
                type='manual'; detail='powercfg sub_processor 5D76A2CA-E8C0-402F-A133-2158492D58AD=1'; revert='powercfg /setacvalueindex scheme_current sub_processor 5D76A2CA-E8C0-402F-A133-2158492D58AD 0'
            })
        }
        'xbox_services_off' {
            foreach ($s in @('XblAuthManager','XblGameSave','XboxGipSvc','XboxNetApiSvc')) { Set-ServiceStart $state $Id $s 'demand' }
        }
        'telemetry_services_off' {
            foreach ($s in @('DiagTrack','WSearch','SysMain','PcaSvc')) { Set-ServiceStart $state $Id $s 'disabled' }
        }
        'windows_update_off' { Set-ServiceStart $state $Id 'wuauserv' 'disabled' }
    }

    Save-OptimizadorState $state
    [pscustomobject]@{ id=$Id; status='applied'; actions=@($state.applied.$Id.actions).Count }
}

function Restore-Optimizacion {
    param([Parameter(Mandatory)] $Id, [switch]$IncludeAggressive)
    if (-not (Test-IsAdmin)) { throw 'Se requieren privilegios de administrador' }
    $state = Get-OptimizadorState
    if (-not ($state.applied.PSObject.Properties.Name -contains $Id)) {
        return [pscustomobject]@{ id=$Id; status='not_applied' }
    }
    $actions = @($state.applied.$Id.actions)
    [array]::Reverse($actions)
    foreach ($a in $actions) {
        switch ($a.type) {
            'registry' {
                if (-not (Test-Path $a.path)) { New-Item -Path $a.path -Force | Out-Null }
                if ($a.beforeExists) {
                    $kind = if ($a.beforeKind) { $a.beforeKind } else { 'DWord' }
                    New-ItemProperty -Path $a.path -Name $a.name -Value $a.beforeValue -PropertyType $kind -Force | Out-Null
                } else {
                    Remove-ItemProperty -Path $a.path -Name $a.name -ErrorAction SilentlyContinue
                }
            }
            'service'   { & sc.exe config $a.service start= $a.beforeStartMode | Out-Null }
            'powerplan' { powercfg /setactive $a.beforeGuid | Out-Null }
            'manual'    { Invoke-Expression $a.revert }
        }
    }
    $state.applied.PSObject.Properties.Remove($Id)
    Save-OptimizadorState $state
    [pscustomobject]@{ id=$Id; status='reverted'; actions=$actions.Count }
}

function Restore-All {
    param([switch]$IncludeAggressive)
    $state = Get-OptimizadorState
    $ids = @($state.applied.PSObject.Properties.Name)
    foreach ($id in $ids) { Restore-Optimizacion -Id $id -IncludeAggressive:$IncludeAggressive | Out-Null }
    [pscustomobject]@{ reverted=$ids.Count; ids=$ids }
}

Export-ModuleMember -Function Test-IsAdmin, Get-OptimizadorState, Get-OptimizadorCatalog, Apply-Optimizacion, Restore-Optimizacion, Restore-All
