# ApexKernel Optimizer

Aplicación de escritorio WPF para Windows, con interfaz en español y catálogo de 227 ajustes.

## Funciones disponibles

- Catálogo de 227 ajustes con descripciones en español, búsqueda global instantánea y filtros por categoría/riesgo/estado (activos y respaldados).
- Interfaz rediseñada estilo AtlasOS/KernelOS: barra lateral con las 9 categorías y accesos rápidos, dashboard de hardware con 4 tarjetas de telemetría, acciones masivas y 3 perfiles generales (Uso normal, Modo gaming y Aplicar casi todo — NO RECOMENDADO), y consola de auditoría colapsable.
- Espacio de trabajo unificado para las 28 herramientas simples: entre 3 y 6 opciones por herramienta (lecturas, presets, exportación, restauraciones parciales y diagnósticos), confirmación integrada, historial y copia/guardado de resultados.
- Demonio nativo en tiempo real con reglas por juego, purga inteligente de standby list y smart trim solo bajo presión de RAM (≥80 %).
- Detección de CPU y topología de núcleos, RAM, GPUs, tipo de unidad del sistema, batería, Windows y servicios anticheat.
- Estado sincronizado con Windows para los 227 ajustes del catálogo.
- Aplicación/restauración de los 227 ajustes, todos respaldados antes de cambiar el sistema: Registro HKCU/HKLM/HKCR, BCD, servicios, PowerShell nativo y planes de energía. El gestor #68 permite probar y conservar la frecuencia máxima enumerada a resolución actual con temporizador de reversión. El gestor #72 pone en cuarentena archivos de D3DSCache y NVIDIA DXCache/GLCache con opción de restaurarlos; los archivos ocupados se omiten. El gestor #69 configura FSO y HIGHDPIAWARE por ejecutable real y restaura cada valor previo. El ajuste #34 modifica los parámetros de la tarea MMCSS Games y advierte qué valores no tienen efecto según Microsoft. El ajuste #52 consulta soporte HAGS real del controlador con D3DKMT y solo se activa si es compatible; necesita reiniciar. El gestor #18–#20 configura CpuPriorityClass, IoPriority y PagePriority por nombre IFEO, guarda cada DWORD previo y evita pisar cambios externos; la regla se aplica a todos los procesos con ese nombre. El gestor #54 asigna preferencia Windows de GPU de alto rendimiento por ruta `.exe`, conserva otras opciones del valor y restaura el estado original de cada ejecutable. El ajuste #14 actualiza los valores DWORD de energía ya existentes en instancias USB/HID; no incluye dispositivos nuevos hasta reaplicarlo y algunos controladores pueden ignorar estos parámetros. El selector #17 permite elegir 36, 40 o 38 para Win32PrioritySeparation y conserva el valor original exacto; requiere administrador. El ajuste #32 modifica únicamente disabledynamictick en la entrada BCD actual, restaura el valor o ausencia previa y requiere reinicio; Microsoft lo marca como opción solo de depuración. Los ajustes #33, #125 y #126 gestionan por separado useplatformclock, tscsyncpolicy y useplatformtick, guardan el valor previo y requieren administrador y reinicio; Windows documenta useplatformclock y useplatformtick como opciones de depuración, y ninguna promete mejoras de rendimiento. Los controles #89, #97, #156, #161, #169 y #188 comprueban la edición de Windows; #89 requiere build 15063+, #97 y #161 Windows 11 21H2+, #156 build 17134+, #169 Windows 10 2004 actualizado o Windows 11, y requieren edición Pro/Education/Enterprise/IoT Enterprise. El ajuste de compresión de memoria requiere 16 GB+ RAM. Si falta el respaldo original, la app se niega a adivinar cómo restaurar. Los ajustes amarillos piden confirmación antes de aplicarse.
- Exportación/importación de perfiles `.optprofile` para las funciones disponibles.
- Consola de actividad y archivo de log en `%LocalAppData%\ApexKernel\Logs\app.log`.
- Ajustes por registro, BCD, energía y servicios habilitados para todo el catálogo (incluidos #1, #3–#14; #17; #32–#34 y #125–#126; #44; #46–#49; #52–#55; #76–#93; #98–#145; #156–#206, según corresponda a cada hardware). #18–#20 (gestor de prioridades CPU/E/S/memoria por juego), #16 (calculadora DPI), #54 (preferencia GPU por ejecutable), #69 (gestor FSO/DPI por juego), #72 (respaldo/restauración de caché de shaders), #148 (medición Sleep(1)), #149 (medidor Raw Input), #153 (visor USB/HID), #174 (Explorer/Configuración), #146 (restablecimiento gráfico), #192 (buscador/filtros), #201 (diagnóstico BypassIO), #207 (índice de respaldos), #209 (gestor de perfiles) y #210 (actualizar estado).
- Herramientas nativas con ventana propia (53 en total): gestor de servicios (#105/#205), gestor de arranque (#152), monitor avanzado de procesos (#143), consola de auditoría (#155), reglas del demonio por juego (#21/#22), vibración digital NVIDIA (#67), ReBAR por ejecutable (#203), planificador de planes de energía (#51), límite de potencia (#71), DNS benchmark (#82), TRIM (#108), limpieza profunda (#109), Appx (#110), pausa de actualizaciones (#111), reparación del sistema (#112), navegadores (#144), caché de shaders por juego (#147), winget (#150), TrustedInstaller (#151), periféricos (#154), arranque seguro (#176), registro de juegos (#177), exclusiones SmartScreen (#182), calculadora de monitor (#183), reparación de tienda (#195), sincronización de refresco (#204), MSI (#56), afinidad (#57), panel NVIDIA (#64), USB directos (#15) y eDPI/diagnósticos restantes.

Los 227 ajustes del catálogo están implementados y sincronizados con el estado real de Windows; los 53 marcados como herramienta abren su ventana de ejecución al pulsar sobre ellos. Las etiquetas de riesgo y descripciones provienen de los documentos fuente; no deben interpretarse como validación de rendimiento para todos los equipos.

## Ejecutar

Para Windows x64, abre `dist\win-x64\ApexKernelOptimizer.exe`. No requiere instalar .NET. La primera ejecución detecta las características disponibles del equipo; las acciones de Registro habilitadas se aplican a la cuenta actual y se pueden revertir desde la misma interfaz cuando existe un respaldo previo.

## Desarrollo

Requiere el SDK de .NET 8 con el workload de escritorio de Windows. El SDK se necesita solo para compilar, no para quien utilice una publicación autocontenida.

```powershell
dotnet build .\ApexKernelOptimizer.csproj
```

Para crear un ejecutable único autocontenido de Windows x64 o ARM64:

```powershell
.\publish.ps1 -Runtime win-x64
# En equipos Windows ARM64: .\publish.ps1 -Runtime win-arm64
```

El catálogo va incrustado en el ejecutable y el .NET Runtime se incluye en la publicación; las personas que lo ejecuten no tienen que instalar .NET.

## Tres acciones por optimización: Aplicar / Restaurar / Defecto

Cada ajuste ofrece ahora tres botones explícitos (en la tarjeta y en el Modo Pro):

- **Aplicar**: escribe el valor de optimización y guarda el estado previo.
- **Restaurar**: vuelve al estado anterior guardado por ApexKernel.
- **Defecto**: vuelve al **valor oficial documentado de Windows**, tomado del catálogo (campo `RevertValue`), **no del estado que tuviera este equipo**.

El panel «Detalle técnico» muestra el valor oficial que se aplicaría con Defecto.

Fuentes consultadas para los valores oficiales corregidos:

| Valor | Defecto documentado | Fuente |
|---|---|---|
| `SystemResponsiveness` | 20 (valores <10 se fijan en 20) | Microsoft Learn — MMCSS |
| `NetworkThrottlingIndex` | 10 (`0xA`) | Microsoft Q&A / documentación MMCSS |
| `HwSchMode` (HAGS) | 0 (comportamiento predeterminado del sistema) | NinjaOne / documentación Windows |
| `WaitToKillServiceTimeout` | 5000 ms | Windows 11 Forum / Microsoft |
| `MouseSpeed` / `MouseThreshold1` / `MouseThreshold2` | 1 / 6 / 10 | Windows 11 Forum (valor por defecto de precisión de puntero) |
| `MenuShowDelay` | 400 | Documentación de Windows |
| `UserPreferencesMask` (animaciones activadas) | `9E 1E 07 80 12 00 00 00` | Comunidad Microsoft / guías de efectos visuales |
| DSCP | 0 (Best Effort) | Cisco / Microsoft QoS |

También se corrigieron los valores oficiales de #212, #227, #52, #74, #73, #91 y #115 en el catálogo incrustado.

## Refactorización de estabilidad y rendimiento (2026-11-18)

- **Sin crashes por DWord grande**: `ParseInputValue`/`RestoreValue` aceptan decimal con signo, sin signo (`4294967295`) y hexadecimal (`0xFFFFFFFF`); también corregido el `int.Parse` del gestor IFEO.
- **Telemetría 100 % asíncrona**: `SampleAsync()` usa `WaitForExitAsync`/`SendPingAsync`, cachea la ruta de `nvidia-smi` y cuenta procesos con `GetPerformanceInfo`. `SampleTelemetryAsync()` está protegido con `Interlocked` y eliminamos el `DispatcherTimer` duplicado de `MainWindow.xaml.cs`.
- **Arranque paralelo**: el catálogo se publica de inmediato en la UI y los 174 estados se verifican con `SemaphoreSlim(8)` mientras el escaneo de hardware corre en paralelo. `AddLog` hace marshal al Dispatcher, evitando el `NotSupportedException` de CollectionView.
- **Cierre limpio del demonio**: `Dispose()` reanuda procesos RGB suspendidos, restaura el plan de energía y libera la resolución del temporizador. `MainWindow.OnClosed` llama a `Dispose()`.
- **NVAPI corregido**: `TryLoad` en vez de `Load`, liberación cuando `NvAPI_Initialize` falla, `ByValTStr(2048)` en los campos de `NvDRSApplication` y lectura del `PtrToStructure` antes de `FreeHGlobal` (use-after-free).
- **Sin deadlocks de PowerShell**: stdout y stderr se leen en paralelo antes de `WaitForExitAsync` (punto de restauración y escáner de hardware).
- **Cachés en memoria**: `StateBackupManager` y `DaemonConfigStore` solo releen el disco cuando cambia `LastWriteTimeUtc`.
- **Menor peso**: `app.manifest` con `requireAdministrator` y PerMonitorV2, flags de trimming del runtime, catálogo minificado (152 KB) y 74 MB de artefactos de análisis movidos fuera del proyecto.

## Temas, telemetría e informes

- **Selector de temas** en la cabecera: **Apex Neo** (verde lima sobre carbón, identidad propia inspirada en NoPing), **Apex Atlas** (azul acero) y **Apex Amber** (ámbar). La elección se guarda en `%LocalAppData%\ApexKernel\theme.txt`.
- **Telemetría en vivo** en el panel de control cada **5 segundos**: CPU, RAM, GPU (vía `nvidia-smi` cuando está disponible), latencia, disco y número de procesos, con gráficas de tendencia.
- **Informes de cambios** en *Acciones masivas*: **HTML**, **JSON** y **Markdown**, con valor anterior, valor objetivo y clave de registro. Se guardan en `%LocalAppData%\ApexKernel\Reports\` y se abre la carpeta.

## Modo Pro

El botón **MODO PRO ▤** de la cabecera abre una vista profesional en forma de tabla:

- **Valor actual** leído del sistema frente a **Objetivo** que se escribiría.
- **Selección múltiple** con casillas y botones *Aplicar seleccionados* / *Restaurar seleccionados*.
- **Desplegable de variantes** por ajuste cuando existen opciones.
- **Acción por fila** (Aplicar / Restaurar) y columna de estado.
- *Actualizar valores* vuelve a leer el registro para reflejar cambios externos.
- Los filtros de riesgo, búsqueda y categoría de la cabecera aplican también a esta vista.

## Opciones configurables por ajuste

Además del interruptor, nueve ajustes muestran un desplegable con variantes reales; el valor elegido se escribe al activar el ajuste y el estado previo se respalda igual que siempre:

| ID | Ajuste | Variantes |
|---|---|---|
| 1 | Aceleración del puntero | 0 / 1 / 2 |
| 3 | Sensibilidad Windows | 6/11 (10) / 8 / 12 |
| 5 | Doble clic | 200 / 300 / 400 / 500 ms |
| 11 | StickyKeys | Desactivar (506) / Activar (510) |
| 92 | Retraso de menús | 0 / 100 / 400 ms |
| 115 | SystemResponsiveness | 0% / 10% / 20% |
| 211 | Network Throttling | Sin límite / 10 / 32 |
| 226 | Iconos de bandeja | Mostrar / Ocultar |
| 227 | Network Throttling normal | 10 / Sin límite |

Los ajustes 218-227 añaden además planes de energía (High/Ultimate) y EPP en tres niveles (0/30/50), usando el mismo sistema de respaldo.

## Mejoras integradas desde el análisis REA (2026-11-18)

El catálogo pasó de 210 a **227 ajustes**. Los nuevos IDs 211-227 proceden del análisis estático y de la observación controlada de NoPing, Atlas Toolbox y KernelOS Toolbox:

| ID | Ajuste | Origen del valor |
|---|---|---|
| 211 | Eliminar limitación de ancho de banda multimedia (`NetworkThrottlingIndex = 0xFFFFFFFF`) | NoPing, confirmado en vivo (10 → 4294967295) |
| 212 | `SystemResponsiveness = 0` (CPU casi total al juego) | NoPing, confirmado en vivo (10 → 0). Mutuamente exclusivo con #115 |
| 213 | Desactivar apps en segundo plano (global) | Script real de Atlas Toolbox |
| 214 | Desactivar búsqueda web de Bing | Script real de Atlas Toolbox |
| 215 | Bloqueo completo de FSO y GameDVR | Script real de Atlas Toolbox; complementa a #88 |
| 216 | Desactivar Aero Peek | Script real de Atlas Toolbox (`EnableAeroPeek`/`AlwaysHibernateThumbnails`) |
| 217 | Ocultar sufijo "Shortcut" en accesos directos | Script real de Atlas Toolbox (`ShortcutNameTemplate`) |
| 218 | Activar plan Ultimate Performance | NoPing (`power_ultimate_perf_plan`), GUID e9a42b02-... |
| 219 | Activar plan High Performance | NoPing (`opt_power_plan_high`), GUID 8c5e7fda-... (observado en vivo) |
| 220 | EPP: rendimiento máximo (0) | NoPing (`power_epp_performance`), PERFEPP 36687f9e-... |
| 221 | EPP: equilibrado-rendimiento (30) | Variante multi-opción |
| 222 | EPP: valor Windows por defecto (50) | Variante multi-opción |
| 223 | DWM MMCSS Boost | NoPing (`dwm_enable_mmcss`) vía `DwmEnableMMCSS` |
| 224 | Desactivar EEE / Green Ethernet | NoPing (`nic_disable_eee_green_ethernet`), por adaptador |
| 225 | Desactivar benchmark WinSAT | NoPing (`system_disable_winsat`), tarea `\Microsoft\Windows\Maintenance\WinSAT` |
| 226 | Mostrar todos los iconos de la bandeja | NoPing (`visual_show_all_tray`), `EnableAutoTray=0` |
| 227 | Network Throttling: valor normal (10) | Variante multi-opción del #211 |

Todos usan el respaldo automático existente (`StateBackupManager`), por lo que se pueden restaurar desde la interfaz. Los archivos `optimizations_catalog.json.bak-rea` y los informes de REA en `Documents\Default Project` documentan el análisis. El artefacto queda en `dist\<runtime>\`.
























