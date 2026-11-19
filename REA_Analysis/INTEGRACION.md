# Integración de los tweaks en tu aplicación

## 0. Qué se construyó

| Archivo | Función |
|---|---|
| `OptimizadorCore.psm1` | Núcleo: catálogo declarativo, snapshots, `Apply` / `Revert`, guardas, estado JSON |
| `optimizador.ps1` | CLI con salida JSON: `list`, `status`, `apply`, `revert`, `revert-all` |
| `INFORME-OPTIMIZADORES-REA.md` | Evidencia y valores exactos (incluye observación en vivo) |
| `atlas-scripts-completos.txt`, `atlas-modules-scripts-completos.txt` | Implementación original de Atlas (245 scripts reales) |

Estado en `%LOCALAPPDATA%\MiApp\Optimizador\state.json`. Lo lee cualquier lenguaje.

**Nada se aplicó durante la construcción.** Solo se ejecutaron `list` y `status` (solo lectura).

---

## 1. Arquitectura recomendada

```
Tu aplicación (sin elevación)
        │  subprocess / IPC, argumentos + JSON
        ▼
optimizador.ps1  ──►  OptimizadorCore.psm1
        │                    │
        │                    ├─ snapshots antes de escribir
        │                    ├─ guardas (SMT, laptop, admin)
        │                    └─ escribe state.json
        ▼
Registry / powercfg / sc.exe  (requiere Admin)
```

Regla clave: **tu app corre normal; el hijo que escribe se lanza elevado**. Así no contaminas tu app con permisos altos.

### Por qué este diseño (viene de lo observado)

- **NoPing**: IDs estables + catálogo separado del aplicador; snapshot en `SOFTWARE\NoPing\Improve`; apagado ordenado (¡la kill forzada NO revierte!).
- **Atlas**: cada toggle es un estado (`HKLM\SOFTWARE\AtlasOS\Services\<Nombre>\state`) + un script `.cmd`; guardas antes de aplicar (SMT en `Disable Idle.cmd`).
- **KernelOS**: mismos conceptos con nombres `Apply*`/`Read*`, pero cifrado.

---

## 2. Los "mejores tweaks" (curados)

### Incluidos por defecto — `risk: safe`
| ID | Valor exacto | Origen |
|---|---|---|
| `mmcss_system_responsiveness` | `SystemResponsiveness = 0` | NoPing, **confirmado en vivo** |
| `network_throttling_off` | `NetworkThrottlingIndex = 4294967295` | NoPing, **confirmado en vivo** |
| `mmcss_no_lazy` | `NoLazyMode = 1` | NoPing / KernelOS |
| `power_throttling_off` | `PowerThrottlingOff = 1` | NoPing / Atlas |
| `enable_transparency_off` | `EnableTransparency = 0` | NoPing / Atlas |
| `background_apps_off` | `GlobalUserDisabled = 1`, `BackgroundAppGlobalToggle = 0` | Atlas (script real) |

### Incluidos con `risk: moderate`
`fso_gamebar_off` (script real de Atlas: GameConfigStore + GameDVR + políticas), `web_search_off`, `power_plan_high` (GUID `8C5E7FDA-...`, guarda el plan anterior), `cpu_idle_off` (con guarda SMT), `xbox_services_off` (StartMode 3→4, confirmado en vivo).

### Excluidos / solo con `-IncludeAggressive`
`telemetry_services_off`, `windows_update_off`. **No los actives por defecto**: desactivar Windows Update o Defender tiene coste de seguridad y el usuario debe decidirlo explícitamente.

### Deliberadamente NO incluidos
- Cualquier tweak de KernelOS con valor desconocido (ofuscación Sapphire).
- `AMD Anti-Lag` / `NVIDIA Low Latency` (dependen de drivers del fabricante).
- `TRIM`, recorte de working-set, prioridad de proceso en primer plano (requieren monitorización en vivo).
- `HVCI/VBS/LUA` (cambian postura de seguridad del equipo).

---

## 3. Llamarlo desde tu aplicación

### 3.1 Cualquier lenguaje — patrón general

Ejecuta:
```
powershell.exe -NoProfile -ExecutionPolicy Bypass \
  -File "C:\Users\Administrator\Documents\Default Project\optimizador.ps1" \
  -Action apply -Id mmcss_system_responsiveness -Id network_throttling_off -Json
```
La salida es un array JSON: `[{"id":"...","status":"applied","actions":1}]`.
`status` devuelve `applied: true/false` por tweak; `revert-all` restaura todo.

### 3.2 C# (.NET)

```csharp
using System.Diagnostics;
using System.Text.Json;

static async Task<IReadOnlyList<TweakResult>> RunAsync(string action, params string[] ids)
{
    var args = new List<string> {
        "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
        @"C:\Users\Administrator\Documents\Default Project\optimizador.ps1",
        "-Action", action, "-Json"
    };
    foreach (var id in ids) { args.Add("-Id"); args.Add(id); }

    var psi = new ProcessStartInfo("powershell.exe") { RedirectStandardOutput = true, UseShellExecute = false };
    foreach (var a in args) psi.ArgumentList.Add(a);
    using var p = Process.Start(psi)!;
    var json = await p.StandardOutput.ReadToEndAsync();
    await p.WaitForExitAsync();
    return JsonSerializer.Deserialize<List<TweakResult>>(json)!;
}

record TweakResult(string id, string status, int actions);
```

Para elevar: cambia `UseShellExecute = true` y `Verb = "runas"` (o lanza un pequeño servicio con `requireAdministrator`).

### 3.3 Node / Electron

```js
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
const run = promisify(execFile);

const { stdout } = await run('powershell.exe', [
  '-NoProfile','-ExecutionPolicy','Bypass','-File',
  'C:\\Users\\Administrator\\Documents\\Default Project\\optimizador.ps1',
  '-Action','status','-Json'
]);
const tweaks = JSON.parse(stdout);
```

### 3.4 Python

```python
import json, subprocess
cmd = ["powershell.exe","-NoProfile","-ExecutionPolicy","Bypass","-File",
       r"C:\Users\Administrator\Documents\Default Project\optimizador.ps1",
       "-Action","apply","-Id","mmcss_system_responsiveness","-Json"]
out = subprocess.run(cmd, capture_output=True, text=True, check=True).stdout
print(json.loads(out))
```

---

## 4. Reglas de ciclo de vida (copiadas de las apps analizadas)

1. **Al iniciar tu app**: `-Action status`, lee `state.json`, decide qué aplicar.
2. **Sesión de juego**: aplica los tweaks `safe`. Al terminar la sesión, `revert-all`.
3. **Cierre de la app**: **siempre** `revert-all` en un handler ordenado (`Application.Exit`, `before-quit`, `finally`). No confíes en matar procesos: comprobamos que NoPing no revierte con kill forzada.
4. **Persistencia**: si quieres que un tweak quede fijo (estilo Atlas), no hagas revert; guarda el estado `applied` en tu propio config para saber que lo aplicaste.
5. **Guardas**: el núcleo ya omite `cpu_idle_off` si detecta SMT. Añade las tuyas: no desactivar SysMain en HDD, no tocar timer de alta resolución en portátil.
6. **Idempotencia**: `Apply` devuelve `already_applied` si el ID ya está en el estado; evita duplicar snapshots.

---

## 5. Verificación

```powershell
# Solo lectura, seguro de ejecutar:
powershell -NoProfile -ExecutionPolicy Bypass -File .\optimizador.ps1 -Action list -Json
powershell -NoProfile -ExecutionPolicy Bypass -File .\optimizador.ps1 -Action status -Json

# Aplicar (requiere Admin; CAMBIA EL SISTEMA):
powershell -NoProfile -ExecutionPolicy Bypass -File .\optimizador.ps1 -Action apply -Id mmcss_system_responsiveness -Json

# Restaurar:
powershell -NoProfile -ExecutionPolicy Bypass -File .\optimizador.ps1 -Action revert -Id mmcss_system_responsiveness -Json
```

Comprobar un valor aplicado:
```powershell
Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile' |
  Select-Object SystemResponsiveness, NetworkThrottlingIndex
```

---

## 6. Si te referías a conectar REA a tu app

REA ya está registrado como servidor MCP `rea` en OpenCode. Para que **tu propia app** hable con REA por stdio:

```
npx -y rea-agents@latest mcp
```
y regístralo con el formato MCP de tu cliente (`command: npx`, `args: ["-y","rea-agents@latest","mcp"]`). Eso da a tu app acceso a las 255 herramientas de análisis, **no** aplica tweaks por sí mismo. Para optimización usa el núcleo de esta carpeta.

---

## 7. Siguiente paso

Dime con qué está hecha tu aplicación y genero el adaptador nativo (elevación, JSON, manejo de errores y revert automático en el cierre):

- **.NET / C#**, **Electron / Node / TypeScript**, **Python**, u **otro**.
- ¿Quieres que los tweaks sean **de sesión** (revert al cerrar) o **persistentes**?
