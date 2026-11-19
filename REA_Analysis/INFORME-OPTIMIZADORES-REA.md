# Informe de ingeniería inversa — optimizadores de PC (NoPing / Atlas Toolbox / KernelOS Toolbox)

Generado con REA 6.9.0 (servidor MCP `rea`, 255 herramientas). **Análisis estático:** no se ejecutaron las aplicaciones, no se aplicó ningún tweak y no se modificó el sistema. Se permitió observación controlada pero no fue necesaria: los literales exactos estaban en los archivos.

Convención usada: **[O]** = observado literalmente en el artefacto · **[I]** = inferido de la descripción del fabricante o del comportamiento estándar de Windows · **[D]** = desconocido / no recuperado.

---

## 0. Inventario

| App | Ruta | Ejecutables | Capa con la lógica |
|---|---|---|---|
| NoPing | `C:\Program Files\NoPing` | 15 `.exe` (NoPing.exe 222 MB es shell Electron) | `resources\app.asar` (58,7 MB) + `NoPingServiceHelper.exe` (1,46 MB, v2.0.62, elevado) |
| Atlas Toolbox | `C:\Program Files\Atlas Toolbox` | `AtlasToolbox.exe`, `RestartAgent.exe`, `createdump.exe`, `unins000.exe` | `AtlasToolbox.dll` (.NET 8-ish, 798 KB, 2.050 métodos, 1.616 user strings) + scripts `.cmd` externos |
| KernelOS Toolbox | `C:\Program Files\KernelOS Toolbox` | `KOS-PostInstall.exe`, `unins000.exe` | `KOS-PostInstall.dll` (.NET, 1,24 MB, 2.237 métodos) **ofuscado con Sapphire** |

---

## 1. NoPing — "Boost FPS / optimiza tu PC"

### 1.1 Arquitectura observada

- El renderer (Electron) contiene el catálogo con IDs y textos: `dist/renderer/assets/boost-fps.store-*.js`, `AdvancedBoostsTab-*.js`, `prosettings-*.js`.
- Comunicación IPC: canales `BOOST_FPS_APPLY`, `BOOST_FPS_APPLY_BOOST`, `BOOST_FPS_REVERT`, `BOOST_FPS_REVERT_BOOST`, `BOOST_FPS_LIST_BOOSTS`, `BOOST_FPS_IMPROVE_RUN`, `BOOST_FPS_REAPPLY_LAST`, `BOOST_FPS_STATUS`, `BOOST_FPS_RAM_CLEAN` **[O — `dist/ipc/boost-fps.jsc`]**.
- El helper nativo `NoPingServiceHelper.exe` ejecuta los boosts con elevación, mediante pipe `\\.\pipe\noping_*` con token y handshake **[O]**.
- Estado persistente: `SOFTWARE\NoPing\Improve` y `SOFTWARE\NoPing\Improve\Services` **[O]**; información adicional en `Software\NoPing\Information` **[O]**.
- Operaciones: `apply-boost`, `apply-boosts`, `revert-boost`, `revert-boosts`, `revert-keep-snapshot`, `restore-create`, `restore-list`, `restore-to`, `list-boosts`, `improve-run`, `improve-query`, `ram-clean`, `reapply-last-state` **[O]**.
- El helper guarda el estado anterior (`PreviousPowerPlan`, `STOPPED_BY_BOOST`, `SERVICE_STARTTYPE`, `PRIORITY_CLASS`) y revierte todo al recibir `APP_QUITTING`, `SESSION_ENDED`, heartbeat timeout o desconexión del pipe **[O]**.

### 1.2 Catálogo exacto de boosts

IDs observados en el helper y en el store **[O]**; clases C++ `NoPing::*Boost` también observadas **[O]**.

| ID del boost | Mecanismo | Clave / comando / valor | Reversión |
|---|---|---|---|
| `power_ultimate_perf_plan` | Plan de energía oculto "Ultimate Performance" | API `PowerGetActiveScheme` / `PowerDuplicateScheme` / `PowerSetActiveScheme`; guarda `PreviousPowerPlan` **[O]** | Reactiva el plan guardado **[O]** |
| `opt_power_plan_high` / `Apply High Performance Power Plan` | Plan "High Performance" | `PowerPlanHigh` / `PowerPlanHigh@Improve@NoPing` **[O]** **[D]** GUID exacto no literal | — |
| `power_core_parking_off` | Desaparcar todos los cores | `CoreParkingMin` = 100 % **[O]** **[I]** bajo `SYSTEM\CurrentControlSet\Control\Power` | Restaura valor **[I]** |
| `power_boost_mode_aggressive` | Boost agresivo de CPU | `BoostMode` = agresivo **[O]** | Restaura valor **[I]** |
| `power_epp_performance` | EPP = rendimiento (Speed Shift) | `EppPerformanceBoost` **[O]** | Restaura valor **[D]** |
| `power_system_responsiveness` | Casi 100 % CPU al juego | `SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\SystemResponsiveness` = **0** **[O]** | Restaura DWORD **[I]** |
| `power_mmcss_gaming` | Prioridad de tarea Games | `...\Multimedia\SystemProfile\Tasks\Games` → `MMCSS_Priority`, `MMCSS_GPUPriority`, `MMCSS_SchedulingCategory` **[O]** (valores exactos **[D]**) | Restaura valores **[I]** |
| `mmcss_no_lazy` | Desactiva detección de inactivity de MMCSS | `MmcssNoLazyBoost` **[O]** | Restaura **[I]** |
| `dwm_enable_mmcss` | DWM/CSRSS a prioridad realtime MMCSS | `DwmEnableMMCSS(TRUE)` vía `dwmapi.dll`; constantes `DWM_MMCSS`, `DwmMMCSS` **[O]** | `DwmEnableMMCSS(FALSE)` **[O]** |
| `dwm_disable_mpo` | Desactiva Multi-Plane Overlay | `SOFTWARE\Microsoft\Windows\Dwm\OverlayTestMode` = **5** **[O]** | Elimina la clave **[O]** |
| `visual_disable_transparency` | Quitar transparencia | `Software\Microsoft\Windows\CurrentVersion\Themes\Personalize\EnableTransparency` = 0 **[O-I]** | Restaura **[I]** |
| `visual_disable_animations` | Animaciones de ventana/menú | `EnableTransparency`, `VisualEffects`, `TaskbarAnimations`, `DragFullWindows`, `EnableAeroPeek` **[O]** | Restaura **[I]** |
| `visual_disable_fso` | Fullscreen Optimizations (DX9-DX11) | `DisableFsoBoost` **[O]**; ruta exacta **[D]** | Restaura **[I]** |
| `visual_disable_gamebar_dvr` | Game Bar / Game DVR | `Software\Microsoft\GameBar`, `Software\Microsoft\Windows\CurrentVersion\GameDVR` **[O]** | Restaura **[I]** |
| `visual_disable_background_apps` | Apps en segundo plano | `Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications\GlobalUserDisabled` **[O]** | Restaura **[I]** |
| `mem_disable_sysmain` | Parar SysMain (Superfetch) | `ControlService(STOP)` sobre `SysMain` **[O]**; omite si disco del sistema es HDD **[O]** | `StartService` **[O]** |
| `mem_trim_working_set` | Recortar working set de procesos | API de memory; omite procesos protegidos/sistema **[O]** | No aplica (temporario) **[O]** |
| `PurgeStandbyListBoost` | Vaciar standby list | "RAM Clean: Memory pages deduplicated" **[O]** | No aplica **[O]** |
| `system_disable_diagtrack` | Parar telemetría | `ControlService(STOP)` sobre `DiagTrack` **[O]** | `StartService` **[O]** |
| `service_stop_wsearch` | Parar indexación | `WSearch` **[O]** | `StartService` **[O]** |
| `service_stop_pcasvc` | Parar Program Compatibility Assistant | `PcaSvc` **[O]** | `StartService` **[O]** |
| `service_stop_werservice` | Parar Windows Error Reporting | `WerSvc` **[O]** | `StartService` **[O]** |
| `adv_disable_xbox_services` | Servicios Xbox | `Disable Xbox services` **[O]** | `StartService` **[O]** |
| `system_disable_auto_maintenance` | Mantenimiento automático | `SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\Maintenance\MaintenanceDisabled` = 1 **[O]** | Restaura **[I]** |
| `system_disable_winsat` | Tarea WinSAT | `DisableWinsatTaskBoost` **[O]** | Reactiva tarea **[O]** |
| `system_disable_windows_update` | Bloquear updates durante el juego | `SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU` **[O]**; para servicios de update **[O]** | Restaura **[I]** |
| `power_pcie_aspm_off` | ASPM PCIe | `PcieAspm`, `PcieAspmOffBoost` **[O]** | Restaura **[I]** |
| `power_usb_selective_suspend_off` | USB selective suspend | `UsbSelectiveSuspendOffBoost` **[O]** | Restaura **[I]** |
| `nic_disable_eee_green_ethernet` | EEE / Green Ethernet | `AdvancedEEE`, `*EEE` en `SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}` **[O]** | Restaura por adaptador **[I]** |
| `nic_disable_interrupt_moderation` | Interrupt coalescing | `*InterruptModeration` **[O]** | Restaura **[I]** |
| `io_disable_power_throttling` | Speed Shift / throttling | `SYSTEM\CurrentControlSet\Control\Power\PowerThrottling\PowerThrottlingOff` **[O]** | Restaura **[I]** |
| `io_foreground_boost` | Prioridad de proceso en primer plano | `SYSTEM\CurrentControlSet\Control\PriorityControl\Win32PrioritySeparation`; `SetPriorityClass`/`GetPriorityClass`; omite navegadores y procesos del sistema **[O]** | Restaura `PRIORITY_CLASS` **[O]** |
| `opt_network_release_renew` | Renovar IP | `netsh` **[O]** | — |
| `opt_trim_ssd` | TRIM de SSD | ID observado en el store **[O]**; comando exacto **[D]** (típicamente `fsutil behavior set DisableDeleteNotify 0`) | Restaura valor **[D]** |
| `HagsToggleBoost` | HAGS | `HwSchMode`, `SYSTEM\CurrentControlSet\Control\GraphicsDrivers` **[O]** | Restaura **[I]** |
| `AmdAntiLagBoost` / `NvidiaLowLatencyBoost` | Anti-Lag / Low Latency vía driver | ADLX / NVIDIA (perfil de driver) **[O]** | Restaura perfil **[O]** |
| `HighResTimerBoost` | Timer de alta resolución | `SYSTEM\CurrentControlSet\Control\Session Manager\kernel` **[O]**; omitido en portátiles **[O]** | Restaura **[I]** |
| `MouseAccelOffBoost` | Ratón 1:1 | Umbrales y bit EPM **[O]** | Restaura **[O]** |
| `NetworkThrottlingOffBoost` | Quitar throttle de red | `NetworkThrottlingIndex` bajo `...\Multimedia\SystemProfile` **[O-I]** | Restaura **[I]** |
| `ShaderCacheBoost`, `KillBackgroundAppsBoost`, `EnableGameModeBoost`, `ForegroundBoostBoost`, `ImproveToggleAsBoost`, `ServiceStopBoostBase`, `AdvancedServiceFeature` | Clases auxiliares observadas **[O]** | Detalle interno **[D]** | — |

**Rutas de registro observadas textualmente en `NoPingServiceHelper.exe`** (útiles para implementar):

```
SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile
SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games
SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\Maintenance
SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore
SOFTWARE\Microsoft\Windows NT\CurrentVersion
SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize
SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Advanced
SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects
SOFTWARE\Microsoft\Windows\CurrentVersion\Search
SOFTWARE\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications
SOFTWARE\Microsoft\Windows\CurrentVersion\GameBar
SOFTWARE\Microsoft\Windows\CurrentVersion\GameDVR
SOFTWARE\Microsoft\Windows\CurrentVersion\Run / Run-disabled
SOFTWARE\Microsoft\Windows Defender\Windows Defender Exploit Guard\ASR\Rules
SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU
SOFTWARE\Policies\Microsoft\Windows Defender\Windows Defender Exploit Guard\ASR\Rules
SOFTWARE\NoPing\Improve
SOFTWARE\NoPing\Improve\Services
Software\NoPing\Information
Software\NoPing\Auth
SYSTEM\CurrentControlSet\Control\GraphicsDrivers
SYSTEM\CurrentControlSet\Control\PriorityControl
SYSTEM\CurrentControlSet\Control\Power\PowerThrottling
SYSTEM\CurrentControlSet\Control\Session Manager\kernel
SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}
SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces
SYSTEM\CurrentControlSet\Services\NDISRD
SYSTEM\CurrentControlSet\Services\npfilter
HARDWARE\DESCRIPTION\System\CentralProcessor\0
Software\Microsoft\Windows\Windows Error Reporting\LocalDumps\
Software\Microsoft\Windows\Windows Error Reporting\RuntimeExceptionHelperModules
Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\SystemAppData
```

---

## 2. Atlas Toolbox — comandos exactos

Atlas es un **frontend**: cada toggle es un estado en `HKLM\SOFTWARE\AtlasOS\Services\<Nombre>` **[O]** y ejecuta un script `.cmd` de `\AtlasDesktop\...` o `\AtlasModules\Toolbox\Scripts\...` **[O]**. Tiene un `REGHELPER` que escribe/elimina valores y claves y fusiona `.reg` **[O]**.

### 2.1 Comandos literales recuperados **[O]**

```bat
:: CPU Idle
powercfg /setacvalueindex scheme_current sub_processor 5d76a2ca-e8c0-402f-a133-2158492d58ad 1
powercfg /setactive scheme_current
:: (reverse)
powercfg /setacvalueindex scheme_current sub_processor 5d76a2ca-e8c0-402f-a133-2158492d58ad 0

:: Sleep / suspend (GUIDs de subgroup 238c9fa8-0aad-41ed-83f4-97be242c8f20 y 2e601130-5351-4d9d-8e04-252966bad054)
powercfg /setacvalueindex scheme_current 238c9fa8-0aad-41ed-83f4-97be242c8f20 25dfa149-5dd1-4736-b5ab-e8a37b5b8187 0
powercfg /setacvalueindex scheme_current 238c9fa8-0aad-41ed-83f4-97be242c8f20 abfc2519-3608-4c2a-94ea-171b0ed546ab 0
powercfg /setacvalueindex scheme_current 238c9fa8-0aad-41ed-83f4-97be242c8f20 94ac6d29-73ce-41a6-809f-6363ba21b47e 0
powercfg /setacvalueindex scheme_current 238c9fa8-0aad-41ed-83f4-97be242c8f20 7bc4a2f9-d8fc-4469-b07b-33eb785aaca0 0
powercfg /setacvalueindex scheme_current 238c9fa8-0aad-41ed-83f4-97be242c8f20 bd3b718a-0680-4d9d-8ab2-e1d2b4ac806d 0
powercfg /setacvalueindex scheme_current 2e601130-5351-4d9d-8e04-252966bad054 d502f7ee-1dc7-4efd-a55d-f04b6f5c0545 0
powercfg /setactive scheme_currernt        :: (sic, error tipográfico en el original)

:: Hibernación
powercfg /h off      / powercfg /h on

:: Windows Update
sc stop wuauserv
sc config wuauserv start= disabled
schtasks /Change /TN "Microsoft\Windows\WindowsUpdate\sih" /Disable
schtasks /Change /TN "Microsoft\Windows\WindowsUpdate\sihboot" /Disable
:: reversión
sc config wuauserv start= demand
sc start wuauserv
schtasks /Change /TN "Microsoft\Windows\WindowsUpdate\sih" /Enable
schtasks /Change /TN "Microsoft\Windows\WindowsUpdate\sihboot" /Enable

:: Ubicación
sc stop lfscv
sc stop MapsBroker
:: reversión: sc start lfscv / sc start MapsBroker

:: Búsqueda web
taskkill /f /im SearchHost.exe
powershell -NoP -NonI "Get-AppxPackage -AllUsers Microsoft.BingSearch* | Remove-AppxPackage -AllUsers"

:: Microsoft Store
powershell.exe "Get-AppxPackage -AllUsers Microsoft.WindowsStore | Remove-AppxPackage"
powershell.exe "Get-AppxPackage -AllUsers Microsoft.WindowsStore | ForEach-Object {Add-AppxPackage -DisableDevelopmentMode -Register (Join-Path $_.InstallLocation 'AppXManifest.xml')}"
powershell.exe "start ms-windows-store:"

:: Phone Link
powershell -NoP -NonI "Get-AppxPackage -AllUsers Microsoft.YourPhone* | Remove-AppxPackage -AllUsers"
call %windir%\AtlasModules\Scripts\settingsPages.cmd /hide mobile-devices :: /unhide ...

:: Explorer (aplicar cambios visuales)
/c taskkill /f /im explorer.exe
/c explorer.exe

:: Run with priority (menú contextual)
cmd /c start "" /Realtime "%1"    :: también /High /AboveNormal /Normal /BelowNormal /Low

:: Take ownership
cmd.exe /c takeown /f "%1" /r /d y && icacls "%1" /grant *S-1-3-4:F /t /c & Pause

:: Scripts de energía
powershell -EP Bypass -NoP ^& """$env:%windir%\AtlasModules\Scripts\ScriptWrappers\DisablePowerSaving.ps1""" %*
powershell -EP Bypass -NoP ^& """$env:%windir%\AtlasModules\Scripts\ScriptWrappers\DefaultPowerSaving.ps1""" %*
```

### 2.2 Claves de registro observadas **[O]**

```
HKLM\SOFTWARE\AtlasOS\Toolbox
HKLM\SOFTWARE\AtlasOS\Toolbox\Favorites
HKLM\SOFTWARE\AtlasOS\Toolbox\Experiments\
HKLM\SOFTWARE\AtlasOS\FeatureUpdateDeferrals
HKLM\SOFTWARE\AtlasOS\QualityUpdateDeferrals
HKLM\SOFTWARE\AtlasOS\Services\<Nombre>            <- estado de cada toggle
HKLM\SYSTEM\CurrentControlSet\Services\
HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion
HKLM\SOFTWARE\Microsoft\PolicyManager\default\Update
HKLM\SOFTWARE\Microsoft\PolicyManager\current\device\Update
HKLM\SOFTWARE\Microsoft\WindowsUpdate\UX\Settings
HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU
HKLM\SOFTWARE\Policies\Microsoft\Windows\Windows Search
HKLM\SOFTWARE\Policies\Microsoft\Windows\Windows Feeds
HKLM\SOFTWARE\Policies\Microsoft\Windows\CloudContent
HKLM\SOFTWARE\Policies\Microsoft\Windows\Explorer
HKLM\SOFTWARE\Policies\Microsoft\Windows\Personalization
HKLM\SOFTWARE\Policies\Microsoft\Windows Defender Security Center\App and Browser protection
HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsAI
HKLM\SOFTWARE\Policies\Microsoft\FindMyDevice
HKLM\SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization
HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Device Metadata
HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\DriverSearching
HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\FlyoutMenuSettings
HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Shell Extensions\Blocked
HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System
HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsStore\WindowsUpdate
HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\PushNotifications
HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Desktop\NameSpace\DelegateFolders\...
HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Explorer\Desktop\NameSpace\DelegateFolders\...
HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers
HKLM\SYSTEM\CurrentControlSet\Control\DeviceGuard
HKLM\SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity
HKLM\SYSTEM\CurrentControlSet\Control\WMI\Autologger\DiagLog
HKLM\SOFTWARE\Microsoft\FTH
HKCU\Control Panel\Desktop
HKCU\Control Panel\Desktop\WindowMetrics
HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Advanced
HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects
HKCU\SOFTWARE\Microsoft\Windows\DWM
HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Search
HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\SearchSettings
HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Notifications\Settings
HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications
HKCU\SOFTWARE\Microsoft\GameBar
HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer
HKCU\SOFTWARE\Policies\Microsoft\Windows\Explorer
HKCU\SOFTWARE\Policies\Microsoft\Windows\CloudContent
HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\NamingTemplates
HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Accent
HKCR\exefile\Shell\Priority\shell\00Xflyout\command
HKCR\DesktopBackground\shell\NVIDIAContainer\...
HKCR\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}\InprocServer32
```

Nombres de campos observados: `DeferFeatureUpdatesPeriodInDays`, `DeferQualityUpdatesPeriodInDays`, `DragFullWindows`, `TaskbarAnimations`, `EnableAeroPeek`, `AutoGameModeEnabled`, `GlobalUserDisabled`, `BingSearchEnabled`, `ToastEnabled`, `DisableNotificationCenter`, `DisableWindowsConsumerFeatures`, `ExcludeWUDriversInQualityUpdate`, `PreventDeviceMetadataFromNetwork`, `DisableSR`, `EnableMtcUvc`, `EnableSnapAssistFlyout`, `EnableSnapBar`, `LocationSyncEnabled`, `EnableVirtualizationBasedSecurity`, `DisableAIDataAnalysis` **[O]**.

### 2.3 Catálogo de toggles (claves `HKLM\SOFTWARE\AtlasOS\Services\...`) **[O]**

Bluetooth, Toolbox, NVidiaDisplayContainer, NVidiaDisplayContainerContextMenu, AdvancedBootOptions, Animation, AppIconsThumbnail, AppStoreArchiving, AutomaticFolderDiscovery, AutomaticRepair, AutomaticUpdates, BackgroundApps, BootLogo, CompactView, ContextMenuTerminals, MicrosoftCopilot, CpuIdle, CPUIdleContextMenu, DeliveryOptimisation, EdgeSwipe, ExtractContextMenu, FaultTolerantHeap, FileSharing, FSOGameBar, Gallery, GiveAccessToMenu, Hibernation, HideAppBrowserControl, HightestMode, KernelParameters, LanmanWorkstation, Location, LockScreen, MicrosoftStore, Mitigations, NetworkDiscovery, NetworkNavigationPane, NewBootMenu, OldContextMenu, PhoneLink, PowerSaving, Printing, ProcessExplorer, QuickAccess, recall, RecentItems, RemovableDrivesInSidebar, RunWithPriority, SafeMode, SearchIndexing, SecurityHealthTray, ShortcutIcon, ShortcutText, Sleep, SnapLayouts, SpinningAnimations, SuperFetch, SystemRestore, TakeOwnership, ToggleWindowsUpdates, UpdateNotifications, VbsState, VerboseStatusMessage, WebSearch, Widgets, WindowsHello, WindowsSpotlight.

---

## 3. KernelOS Toolbox — catálogo (valores cifrados)

`KOS-PostInstall.dll` está ofuscado con **Sapphire**: los recursos (`Sapphire#*.resources`, 267 KB) y los literales `#US` están cifrados; la metadata es `partial` (2 errores `invalid-heap-index`). Por eso **los valores DWORD exactos no se recuperaron** **[D]**, pero los nombres de métodos (metadata, 2.237 métodos) exponen el catálogo completo **[O]**.

### 3.1 Tweaks de scheduling / CPU (métodos `Read*`/`Apply*`) **[O]**

`Win32PrioritySeparation` (con bits 6, ratio PS, quantum variable/corto/largo, `ApplyQuantum`, `RestoreQuantumDefaults`), `SystemResponsiveness` (`ApplySystemResponsivenessValue`), `LazyModeTimeout`, `NoLazyMode`, `CsrssPriority`, `NicBuffers` (receive/transmit), `IoLatencyCap`, `IOPageLockLimit`, `TscSyncPolicy`, `UsePlatformTick`, `ThreadDpc`, `InterruptSteering`, `PowerThrottling`, `CpuIdle`, `Nagle`, `NipPack`, `QueuedPresentLimit`, `MultiPlaneOverlay` (MPO), `ForceDirectFlip`, `ForceIndependentFlip`, `ForceFlipTrueImmediateMode`, `NvidiaForcePState0`, `DisablePagingExecutive`, `DisablePageCombining`, `HighResTimer`.

### 3.2 Tweaks de seguridad / servicios **[O]**

`VbsEnabled`, `HvciEnabled`, `LuaEnabled`, `VulnerableDriverBlockListEnabled`, `WifiEnabled`, `BluetoothEnabled`, `PrinterEnabled`, `TaskMgrRedirectEnabled`, `WuDriversEnabled`, `StartMenuEnabled`, `SearchHostEnabled`, `CrossDeviceResumeEnabled`, `TextInputHostEnabled`, `UseDefaultTile`, `HopLimit`, `Nx`, `FiveMServices`, `StoreApps`.

### 3.3 Helpers de implementación **[O]**

`SetDwordHKLM`, `ForceSetDwordHKLM`, `SetDwordHKCU`, `SetStringHKLM`, `SetStringHKCU`, `ReadDwordHKLM/HKCU`, `ReadStringHKLM/HKCU`, `EnumSubKeysHKLM`, `ReadServiceStart`, `WriteServiceStart`, `RunPowerCfg`, `RunProcessCapture`, `RunPowerShellCaptureAsync`, `EnablePrivilege`, `OpenProcessToken`, `TryReadUniformNetAdapterValue`, `ReadBcdSetting`, `GetTweakDir`, `GetTweaksRoot`, `EscapePowerShellString`.

### 3.4 Actualizador **[O]**

`C:\Program Files\KernelOS Toolbox\Update.cmd`:
```bat
powershell -NoProfile -ExecutionPolicy Bypass -Command "Invoke-WebRequest -Uri 'https://pub-f7e768aa8f4b4ef89b472ef13ca77df3.r2.dev/kos-setup.exe' -OutFile '%TEMP%\kos-setup.exe' -UseBasicParsing"
```

---

## 4. Patrones reutilizables para su proyecto

1. **IDs estables + catálogo declarativo.** NoPing separa `id` / descripción / estado por idioma del código que aplica el tweak. Las descripciones multilingües están en el bundle renderer; el helper solo conoce IDs.
2. **Apply / Revert con snapshot.** NoPing guarda estado anterior (`PreviousPowerPlan`, `SERVICE_STARTTYPE`, `PRIORITY_CLASS`, DWORD originales) en `SOFTWARE\NoPing\Improve` y revierte en `APP_QUITTING`, `SESSION_ENDED`, pérdida de heartbeat o desconexión del pipe. Varios tweaks son **solo de sesión** (parar servicios, prioridad de proceso) y se restauran al salir.
3. **Elevación explícita.** El helper tiene manifiesto `requireAdministrator`; las operaciones sensibles van en un proceso aparte comunicado por pipe con token, no en el renderer.
4. **Omisiones deliberadas.** NoPing omite SysMain en HDD, HighResTimer en portátiles, y prioridad en procesos del sistema y navegadores. Son reglas de seguridad que conviene copiar.
5. **Frontend + scripts (Atlas).** Cada toggle es una clave de estado y un `.cmd` / `.reg` / script PowerShell. Facilita auditoría y reversión: el estado del toggle no depende de leer el valor de Windows.
6. **Persistencia vs sesión.** Diferenciar tweaks persistentes (registro, plan de energía) de temporales (servicios, prioridad, working set) y documentarlo en la UI.

---

## 5. Evidencia y archivos generados

| Artefacto | SHA-256 / ID | Qué cubre |
|---|---|---|
| `app.asar` | `8214c20ef10ab4d00b42944e23db9aca9276a4d027acc0c7f564aef8cc17f525` | Catálogo y descripciones (331 hallazgos JS) |
| Evidencia JS | `ev_e53532b0f68752b9629c05cc40c76a1218745c32e352bdff113a63f463584d68` | `prosettings`, `boost-fps.store`, `AdvancedBoostsTab`, `BoostFpsPage`, `ram-cleaner.store` |
| `NoPingServiceHelper.exe` | literales volcados en `noping-helper-literales.txt` | 227 líneas: IDs, clases Boost, registry paths, mensajes |
| `AtlasToolbox.dll` | `216eee8e29c3278f08512ec23c3e6d6aa0c23f50bd4dc39b1a3249effca26330`, MVID `f82cb529-5c69-4262-a26b-6283ab2981e6` | 1.616 user strings, 2.050 métodos |
| Evidencia Atlas | `ev_f6513da8002c40ce685bf0000dad1b7163aaf77af3b1c7f34fe1aed45f1c7282` | `inspect_managed_members` → `user_strings` |
| `KOS-PostInstall.dll` | `908cf78e0b9255f9778fd1c8d79eb5b688a6af1bc8aea0367aba09960f059d30`, MVID `0f912584-4908-4b59-961b-7212b32f8e50` | 2.237 métodos, metadata `partial` |

Archivos de trabajo en esta carpeta:
- `noping-helper-literales.txt`
- `atlas-literales.txt`
- `kernelos-literales.txt`

---

## 6. Observación en vivo (segunda pasada)

Se abrieron las aplicaciones con observación controlada. **No se pulsó ningún botón "Aplicar"**: los cambios que aparecieron los aplicó NoPing automáticamente al iniciar (`boostFpsEnabled: true`). Al terminar, **se restauraron todos los valores al estado original** (diff vacío frente al snapshot inicial).

### 6.1 Lo que NoPing aplicó automáticamente al arrancar **[O — observado en vivo]**

| Elemento | Antes | Después | Boost correspondiente |
|---|---|---|---|
| `HKLM\...\Multimedia\SystemProfile\SystemResponsiveness` | `10` | **`0`** | `power_system_responsiveness` |
| `HKLM\...\Multimedia\SystemProfile\NetworkThrottlingIndex` | `10` | **`4294967295`** (`0xFFFFFFFF`) | `NetworkThrottlingOffBoost` |
| Plan de energía activo | `381B4222-F694-41F0-9685-FF5BB260DF2E` (Balanced, guardado en `SOFTWARE\NoPing\Improve\PreviousPowerPlan`) | **`8C5E7FDA-E8BF-4A96-9A85-A6E23A8C635C` (High performance)** | `opt_power_plan_high` |
| `XblAuthManager`, `XblGameSave`, `XboxGipSvc`, `XboxNetApiSvc` | `StartType=3` (Demand), guardado en `SOFTWARE\NoPing\Improve\Services` | **`StartType=4` (Disabled)** | `adv_disable_xbox_services` |
| `wuauserv` | `Stopped/Manual` | **`Stopped/Disabled`** | `system_disable_windows_update` |

Procesos generados por NoPing al abrir: `NoPing` ×4, `NoPingBackend`, `NoPingPerformanceMonitor`, `NoPingServiceHelper`, `NoPingTools` (además de `NoPingNotifications`, que ya estaba). El helper se lanza así **[O]**:

```
"C:\Program Files\NoPing\NoPingServiceHelper.exe" --pipe noping_948686b2403168bd --token 04c5243eb9e4b236fd79e4c291a75e16
```

**Descubrimiento importante:** el cierre forzado de procesos **no** disparó el revert (aunque el helper lo promete en sus cadenas). Hubo que restaurar manualmente. Si va a replicar el patrón, implemente un apagado ordenado con acknowledge; no confíe solo en la desconexión del pipe.

### 6.2 Por qué no se pudo instrumentar NoPing

`inspect_electron_fuses` muestra `EnableNodeCliInspectArguments = disabled` y `EnableEmbeddedAsarIntegrityValidation = enabled`. `capture_electron_scenario` rechaza el objetivo con:

> Target binary disables the EnableNodeCliInspectArguments fuse; Node CLI inspector arguments are ignored and active scenario capture cannot attach.

No había puerto CDP abierto (solo los puertos propios del backend `49813`/`49814`). La observación se hizo por snapshots de sistema.

### 6.3 Atlas Toolbox

`AtlasToolbox.exe` se inicia (PID efímero) y **termina inmediatamente** sin salida ni error, incluso con `-Verb RunAs`. No aplicó cambios. En su lugar se recuperaron **los scripts reales** del sistema, que son la fuente definitiva:

- `C:\Windows\AtlasDesktop` — 159 `.cmd` + 2 `.reg` → **`atlas-scripts-completos.txt`** (161 archivos, 235 KB).
- `C:\Windows\AtlasModules` — 37 `.cmd`, 20 `.reg`, 16 `.ps1`, 11 `.psm1` → **`atlas-modules-scripts-completos.txt`** (84 archivos, 453 KB).

Ejemplo exacto (desactivar FSO/Game Bar) **[O — script real]**:

```bat
reg add "HKCU\System\GameConfigStore" /v "GameDVR_DSEBehavior" /t REG_DWORD /d "2" /f
reg add "HKCU\System\GameConfigStore" /v "GameDVR_DXGIHonorFSEWindowsCompatible" /t REG_DWORD /d "1" /f
reg add "HKCU\System\GameConfigStore" /v "GameDVR_EFSEFeatureFlags" /t REG_DWORD /d "0" /f
reg add "HKCU\System\GameConfigStore" /v "GameDVR_FSEBehavior" /t REG_DWORD /d "2" /f
reg add "HKCU\System\GameConfigStore" /v "GameDVR_FSEBehaviorMode" /t REG_DWORD /d "2" /f
reg add "HKCU\System\GameConfigStore" /v "GameDVR_HonorUserFSEBehaviorMode" /t REG_DWORD /d "1" /f
reg add "HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Environment" /v "__COMPAT_LAYER" /t REG_SZ /d "~ DISABLEDXMAXIMIZEDWINDOWEDMODE" /f
reg add "HKCU\System\GameBar" /v "GamePanelStartupTipIndex" /t REG_DWORD /d "3" /f
reg add "HKCU\System\GameBar" /v "ShowStartupPanel" /t REG_DWORD /d "0" /f
reg add "HKCU\System\GameBar" /v "UseNexusForGameBarEnabled" /t REG_DWORD /d "0" /f
reg add "HKLM\SOFTWARE\Microsoft\WindowsRuntime\ActivatableClassId\Windows.Gaming.GameBar.PresenceServer.Internal.PresenceWriter" /v "ActivationType" /t REG_DWORD /d "0" /f
reg add "HKLM\SOFTWARE\Policies\Microsoft\Windows\GameDVR" /v "AllowGameDVR" /t REG_DWORD /d "0" /f
reg add "HKLM\SOFTWARE\Microsoft\PolicyManager\default\ApplicationManagement\AllowGameDVR" /v "value" /t REG_DWORD /d "0" /f
reg add "HKCU\System\GameConfigStore" /v "GameDVR_Enabled" /t REG_DWORD /d "0" /f
reg add "HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\GameDVR" /v "AppCaptureEnabled" /t REG_DWORD /d "0" /f
Get-AppxPackage *xboxgamingoverlay* | Remove-AppxPackage -Confirm:$false
```

Patrón de estado de Atlas **[O]**: cada script escribe `HKLM\SOFTWARE\AtlasOS\Services\<Nombre>\state` (DWORD) y `...\path` (Ruta al script), y exige elevación (`fltmc` / `RunAsTI.cmd`). En este equipo, por ejemplo: `CpuIdle=1`, `FSOGameBar=1`, `Animation=0`, `BackgroundApps=0`, `AutomaticUpdates=0`, `Indexing=1`.

También hay guardas inteligentes: `Disable Idle.cmd` detecta Hyper-Threading/SMT con `Get-CimInstance Win32_Processor` y **se niega a desactivar idle** si está activo (advierte que empeora el rendimiento).

### 6.4 KernelOS

No hay UI: `KOS-PostInstall.exe` es el instalador/post-instalación. No se abrió para evitar ejecutar un instalador.

---

## 7. Limitaciones y recomendaciones

- La observación en vivo se hizo por **snapshots de registro/servicios/plan de energía**, no por instrumentación de proceso (en Windows `capture_process_scenario` es `unsupported_host` y los fuses de NoPing bloquean CDP activo).
- Los `.jsc` son bytecode V8 con prefijo `0A0A0A0A`; REA recuperó cadenas incrustadas y nombres de canales/funciones, **no descompila bytecode**. Los comandos exactos estaban en el helper nativo y ahora están confirmados en vivo.
- Los valores DWORD precisos de **KernelOS** siguen siendo **[D]** por la ofuscación Sapphire. Haría falta un desofuscador/decompilador de .NET (no instalado; `winget search Ghidra` no devolvió resultados).
- Algunas rutas marcadas **[O-I]** se observaron como nombres de campo en el helper, pero la clave completa se infirió de la convención de Windows. Verifique esas pocas antes de copiarlas.
- **No copie ciegamente** tweaks agresivos (desactivar Windows Update, Defender, telemetría o pantalla azul): están documentados con fines de investigación. Replique las guardas y el apagado ordenado de NoPing/Atlas.
