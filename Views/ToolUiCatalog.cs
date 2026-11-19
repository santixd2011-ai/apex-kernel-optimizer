using ApexKernelOptimizer.Core.Models;

namespace ApexKernelOptimizer.Views;

/// <summary>Acción de una herramienta con la información que muestra la UI.</summary>
public sealed record ToolAction(
    string Label,
    string Description,
    bool NeedsConfirmation = false,
    bool NeedsInput = false,
    bool IsReadOnly = false);

/// <summary>Definición visual y funcional de una herramienta nativa.</summary>
public sealed record ToolDefinition(
    int Id,
    string Area,
    string Purpose,
    string Notes,
    IReadOnlyList<ToolAction> Actions);

/// <summary>
/// Metadatos de las herramientas simples. Las etiquetas coinciden exactamente con los
/// strings que acepta <c>ExecutionEngine.RunToolAsync</c>; cambiar uno sin el otro
/// haría que la herramienta responda "Acción no reconocida".
/// </summary>
public static class ToolUiCatalog
{
    private static ToolAction A(string label, string description, bool confirm = false, bool input = false, bool readOnly = false) =>
        new(label, description, confirm, input, readOnly);

    private static readonly Dictionary<int, ToolDefinition> Map = new()
    {
        [15] = new(15, "Periféricos", "Inventario USB/HID",
            "Recorre el árbol Plug and Play nativo sin instalar utilidades. Útil para confirmar que el ratón está conectado directamente a un controlador raíz y no por un hub.",
            [A("Escanear", "Enumera dispositivos USB/HID, su ruta de instancia y el resumen de hardware del equipo.", readOnly: true),
             A("Resumen rápido", "Muestra entradas USB, CPU, RAM, GPU, anticheat y compilación de Windows.", readOnly: true),
             A("Exportar informe", "Guarda el árbol USB/HID completo en Mis documentos\\ApexKernel.")]),

        [51] = new(51, "Energía", "Generador de planes de energía",
            "Duplica un plan base de Windows y crea una copia con nombre ApexKernel. El plan original no se modifica; puedes volver a él desde 'Listar'.",
            [A("Listar", "Muestra todos los planes de energía y sus GUID, con el plan activo marcado.", readOnly: true),
             A("Generar Equilibrado", "Crea una copia del plan Equilibrado de Windows, conservando el original.", false),
             A("Generar Competitivo", "Crea una copia del plan mínimo/latencia de ApexKernel para jugar.", true),
             A("Generar Máximo", "Crea una copia del plan de máximo rendimiento de Windows.", true)]),

        [56] = new(56, "PCI / interrupciones", "Message Signaled Interrupts (MSI)",
            "Activa MSI en los dispositivos PCI compatibles para reducir interrupciones compartidas. Requiere reinicio para surtir efecto.",
            [A("Verificar", "Comprueba MSISupported por dispositivo PCI con su clave completa y tipo de valor.", readOnly: true),
             A("Activar", "Escribe MSISupported=1 en los dispositivos detectados, con respaldo y aviso de reinicio.", true),
             A("Restaurar", "Devuelve los valores MSI previos desde el respaldo."),
             A("Solo GPU", "Activa MSI únicamente en los adaptadores de pantalla PCI.", true),
             A("Solo audio", "Activa MSI únicamente en los dispositivos de audio PCI.", true)]),

        [57] = new(57, "GPU / afinidad", "Afinidad de interrupciones de GPU",
            "Lee y ajusta DevicePolicy/AssignmentSetOverride en la clave PCI real del adaptador. La máscara KAFFINITY excluye los núcleos 0 y 1.",
            [A("Escanear", "Lee DevicePolicy y AssignmentSetOverride en la clave correcta de cada GPU.", readOnly: true),
             A("Aplicar", "Fija DevicePolicy=4 y una máscara de 64 bits sin los núcleos 0/1.", true),
             A("Restaurar", "Restaura la afinidad de interrupciones previa desde el respaldo."),
             A("Excluir Core 0", "Fija la máscara excluyendo solo la CPU lógica 0.", true),
             A("Todos los núcleos", "Permite todas las CPU lógicas del grupo en la afinidad de interrupciones.", true)]),

        [64] = new(64, "GPU / display", "Panel de control NVIDIA",
            "Abre el panel oficial de NVIDIA con la ruta hacia el ajuste de escalado; no modifica ningún valor por sí sola.",
            [A("Abrir panel", "Abre nvcplui.exe y muestra la ruta del ajuste de escalado.", readOnly: true),
             A("Abrir ajustes de resolución", "Abre el panel y indica Cambiar resolución / Sin escalado (o ms-settings:display).", readOnly: true),
             A("Ver versión de driver", "Lee la versión de nvidia-smi y del driver nvlddmkm.sys.", readOnly: true)]),

        [67] = new(67, "GPU / display", "Vibración digital NVIDIA",
            "Ajusta la intensidad de color por hardware mediante NvAPI. Un valor de 50 suele ser suficiente; 0 restaura el neutro. Entrada: un nivel de 0 a 100, por ejemplo 50.",
            [A("Leer valor actual", "Lee el nivel de Digital Vibrance vigente mediante NvAPI.", readOnly: true),
             A("Aplicar vibración", "Aplica un nivel de 0 a 100 usando NvAPI_DRS (no altera el perfil de color de Windows).", true, true),
             A("Neutro (0)", "Devuelve el color a neutro (0%).", true),
             A("Preset Suave (25)", "Aplica vibración suave al 25%.", true),
             A("Preset Competitivo (50)", "Aplica vibración equilibrada al 50%.", true),
             A("Preset Intenso (80)", "Aplica vibración intensa al 80%.", true)]),

        [71] = new(71, "GPU / energía", "Límite de potencia de GPU",
            "Consulta y aplica el límite de vatios de la GPU NVIDIA compatible. Requiere que el controlador permita ajustar el power limit. Entrada: vatios, por ejemplo 320.",
            [A("Ver rango", "Muestra mínimo, máximo, predeterminado y límite actual de potencia.", readOnly: true),
             A("Consultar", "Lee el límite de potencia actual y el rango admitido por la GPU.", readOnly: true),
             A("Aplicar límite", "Fija el límite de potencia en vatios indicado.", true, true),
             A("Restaurar límite", "Vuelve al vatio respaldado o al predeterminado de fábrica."),
             A("Preset 80%", "Aplica el 80% del máximo admitido, guardando el valor previo.", true),
             A("Preset 100%", "Aplica el máximo admitido, guardando el valor previo.", true)]),

        [82] = new(82, "Red / ping", "Benchmark y cambio de DNS",
            "Mide la latencia de DNS públicos y aplica los IPs elegidos al adaptador físico activo, mostrando el DNS anterior y el actual. Entrada para aplicar: una o dos IP separadas por espacio, por ejemplo 1.1.1.1 1.0.0.1.",
            [A("Consultar DNS", "Lee los servidores DNS IPv4 de los adaptadores físicos activos con su índice.", readOnly: true),
             A("Benchmark", "Mide latencia y aciertos de los DNS públicos y recomienda primario/secundario.", readOnly: true),
             A("Aplicar DNS", "Escribe una o dos IP en el adaptador físico activo y muestra antes/después.", true, true)]),

        [108] = new(108, "Almacenamiento", "Optimización TRIM",
            "Verifica el estado de TRIM y ejecuta la optimización de unidades SSD compatibles.",
            [A("Verificar", "Consulta si TRIM está activo y el estado de las unidades.", readOnly: true),
             A("Optimizar", "Ejecuta la optimización/trim de la unidad del sistema detectada."),
             A("Forzar ReTrim", "Ejecuta ReTrim en todos los volúmenes fijos e informa por unidad."),
             A("Estado SMART", "Muestra salud, temperatura, desgaste y contadores de error de los discos.", readOnly: true)]),

        [109] = new(109, "Limpieza", "Limpieza profunda",
            "Analiza archivos temporales y cachés; la limpieza elimina solo elementos reconocidos y muestra cuánto se liberó.",
            [A("Analizar", "Calcula el tamaño de cachés y archivos temporales sin borrar nada.", readOnly: true),
             A("Limpiar", "Elimina los elementos reconocidos y muestra el espacio liberado.", true),
             A("Solo temporales", "Limpia únicamente las carpetas temporales y muestra antes/después.", true),
             A("Solo cachés GPU", "Limpia únicamente D3DSCache y cachés DX/GL de NVIDIA.", true),
             A("Exportar informe", "Guarda el análisis de temporales y cachés como informe de texto.")]),

        [110] = new(110, "Aplicaciones", "Gestor de paquetes Appx",
            "Lista paquetes instalados, permite desinstalar uno por nombre y restaurar el paquete previo si estaba respaldado. Entrada: nombre exacto del paquete, por ejemplo Microsoft.BingWeather.",
            [A("Listar", "Enumera los paquetes Appx instalados para la cuenta actual.", readOnly: true),
             A("Desinstalar", "Quita el paquete cuyo nombre indiques, guardando el estado previo.", true, true),
             A("Restaurar", "Reinstala/restaura el paquete respaldado.", true, true)]),

        [111] = new(111, "Windows Update", "Control de actualizaciones",
            "Pone en pausa Windows Update cuatro semanas, cambia el modo a Manual o reanuda las actualizaciones.",
            [A("Verificar", "Lee NoAutoUpdate, AUOptions y el estado del servicio wuauserv.", readOnly: true),
             A("Pausar 4 semanas", "NoAutoUpdate=1 y servicio deshabilitado; muestra cómo revertir.", true),
             A("Modo Manual", "NoAutoUpdate=0 y AUOptions=2 (notificar antes de descargar).", true),
             A("Reanudar", "Quita la pausa y devuelve la configuración de actualizaciones a automática.")]),

        [112] = new(112, "Reparación", "Reparación del sistema",
            "Ejecuta reparaciones nativas. DISM y SFC pueden tardar varios minutos; no cierres la ventana hasta ver el resultado.",
            [A("DISM RestoreHealth", "Repara la imagen de Windows desde el componente de mantenimiento.", false),
             A("SFC /scannow", "Comprueba y repara archivos protegidos del sistema.", false),
             A("Reset Winsock", "Reinicia el catálogo de sockets de Windows.", true),
             A("Reset TCP/IP", "Restablece la pila TCP/IP a su estado inicial.", true),
             A("Runtimes VC++", "Comprueba e instala los redistribuibles Visual C++ necesarios.", false),
             A("DirectX", "Verifica y repara los componentes DirectX nativos.", false)]),

        [129] = new(129, "Red / ping", "Búfer de recepción",
            "Consulta y ajusta el tamaño del búfer de recepción del adaptador. Valores altos pueden reducir microcortes a costa de algo más de RAM. Entrada: tamaño de búfer, por ejemplo 1024.",
            [A("Consultar", "Lee el valor actual del búfer de recepción del adaptador activo.", readOnly: true),
             A("Aplicar", "Fija el valor indicado (recomendado entre 128 y 8192).", true, true),
             A("Preset 512", "Fija el búfer de recepción a 512 y guarda el valor previo.", true),
             A("Preset 1024", "Fija el búfer de recepción a 1024 y guarda el valor previo.", true),
             A("Preset 2048", "Fija el búfer de recepción a 2048 y guarda el valor previo.", true),
             A("Restaurar valor previo", "Reaplica los valores de búfer de recepción guardados.")]),

        [130] = new(130, "Red / ping", "Búfer de transmisión",
            "Consulta y ajusta el tamaño del búfer de transmisión del adaptador activo. Entrada: tamaño de búfer, por ejemplo 1024.",
            [A("Consultar", "Lee el valor actual del búfer de transmisión del adaptador activo.", readOnly: true),
             A("Aplicar", "Fija el valor indicado (recomendado entre 128 y 8192).", true, true),
             A("Preset 512", "Fija el búfer de transmisión a 512 y guarda el valor previo.", true),
             A("Preset 1024", "Fija el búfer de transmisión a 1024 y guarda el valor previo.", true),
             A("Preset 2048", "Fija el búfer de transmisión a 2048 y guarda el valor previo.", true),
             A("Restaurar valor previo", "Reaplica los valores de búfer de transmisión guardados.")]),

        [144] = new(144, "Navegadores", "Políticas de navegadores",
            "Aplica políticas de privacidad/rendimiento a Chrome, Edge y Brave, e instala navegadores alternativos si faltan.",
            [A("Verificar políticas", "Lee las 6 políticas de Chrome, Edge y Brave con ruta y valor actual.", readOnly: true),
             A("Aplicar políticas", "Fija métricas a 0 con respaldo e indica qué navegadores reiniciar.", true),
             A("Restaurar políticas", "Elimina las políticas aplicadas y deja los valores por defecto."),
             A("Instalar Brave", "Instala Brave desde su paquete oficial si no está presente.", false),
             A("Instalar Firefox", "Instala Firefox si no está presente.", false),
             A("Instalar Vivaldi", "Instala Vivaldi si no está presente.", false)]),

        [147] = new(147, "Dispositivos", "Dispositivos PnP fantasma",
            "Detecta dispositivos que Windows recuerda pero no están presentes, y permite retirarlos del árbol Plug and Play.",
            [A("Escanear", "Cuenta y lista los dispositivos PnP no presentes con clase e InstanceId.", readOnly: true),
             A("Limpiar", "Retira los dispositivos fantasma con contador de eliminados y fallidos.", true),
             A("Buscar cambios", "Detecta dispositivos PnP recreados desde la última limpieza.", readOnly: true),
             A("Exportar lista", "Guarda el listado de dispositivos fantasma con sus InstanceId.")]),

        [150] = new(150, "Microsoft Store", "Instalador por ID",
            "Busca aplicaciones en la Store e instala por ID de producto, sin abrir la tienda. Entrada: nombre para buscar o ID de producto (por ejemplo 9NBLGGH4NNS1).",
            [A("Buscar", "Busca aplicaciones por nombre y muestra sus IDs.", readOnly: true, input: true),
             A("Instalar por ID", "Instala el producto indicado mediante winget con el ID de la Store.", true, true),
             A("Listar actualizaciones", "Enumera las apps de la Store con actualización pendiente.", readOnly: true),
             A("Actualizar apps", "Actualiza todas las apps de la Store y muestra el estado antes/después.", true)]),

        [151] = new(151, "Reparación", "Consola TrustedInstaller",
            "Ejecuta un comando con un token primario de TrustedInstaller para modificar archivos protegidos. Úsalo solo si sabes qué hace el comando.",
            [A("Verificar", "Comprueba privilegio de administrador, servicio y proceso TrustedInstaller.", readOnly: true),
             A("Ejecutar comando", "Ejecuta el comando indicado con token primario y captura su salida.", true, true)]),

        [154] = new(154, "Periféricos", "Inventario de periféricos",
            "Enumera ratones, teclados y controladores con sus rutas de instancia y exporta el inventario.",
            [A("Escanear periféricos", "Muestra frecuencia, interfaz y ruta de cada periférico detectado.", readOnly: true),
             A("Exportar inventario", "Guarda el inventario en un archivo de texto en la carpeta de logs."),
             A("Solo ratones", "Lista solo ratones con VID/PID y ruta de instancia.", readOnly: true),
             A("Solo teclados", "Lista solo teclados con VID/PID y ruta de instancia.", readOnly: true)]),

        [176] = new(176, "Arranque", "Modo seguro",
            "Configura el arranque a modo seguro (con/sin red) o normal, con opción de reiniciar ahora.",
            [A("Verificar", "Muestra la configuración actual de bcdedit y el indicador safeboot.", readOnly: true),
             A("Modo Seguro", "Configura el siguiente arranque en modo seguro mínimo.", true),
             A("Modo Seguro con Red", "Configura el siguiente arranque en modo seguro con redes.", true),
             A("Arranque normal", "Quita la marca de arranque seguro y vuelve al modo normal.", true),
             A("Reiniciar ahora", "Reinicia inmediatamente para aplicar el modo seleccionado.", true)]),

        [177] = new(177, "Reparación", "Permisos del temporal de Windows",
            "Repara las ACL de %windir%\\Temp, un origen habitual de fallos de instaladores y actualizaciones.",
            [A("Verificar", "Muestra la ACL actual de %windir%\\Temp con icacls.", readOnly: true),
             A("Reparar", "Concede control total heredable a Administradores y muestra la ACL resultante.", true),
             A("Probar permisos", "Muestra la SDDL y prueba creando/borrando un archivo temporal.", readOnly: true),
             A("Restaurar ACL previa", "Reaplica la SDDL guardada antes de Reparar.")]),

        [182] = new(182, "Seguridad", "Exclusiones de Defender y SmartScreen",
            "Lista y gestiona exclusiones de Microsoft Defender y activa/desactiva SmartScreen con respaldo. Entrada para añadir/quitar: ruta de la carpeta de juegos.",
            [A("Listar exclusiones", "Muestra las exclusiones actuales de Defender.", readOnly: true),
             A("Verificar SmartScreen", "Lee EnableSmartScreen, ShellSmartScreenLevel y el valor de Edge.", readOnly: true),
             A("Añadir exclusión", "Añade la ruta indicada a las exclusiones de Defender.", true, true),
             A("Quitar exclusión", "Quita la ruta indicada de las exclusiones.", true, true),
             A("Desactivar SmartScreen", "Desactiva SmartScreen guardando el estado previo.", true),
             A("Restaurar SmartScreen", "Restaura el estado previo de SmartScreen.")]),

        [183] = new(183, "GPU / display", "Calculadora fps / Hz",
            "Calcula tiempo de fotograma, margen y fotogramas por refresco para una combinación de FPS y Hz. Entrada: 'fps,Hz', por ejemplo 240,240.",
            [A("Calcular", "Calcula tiempo de cuadro y márgenes a partir de 'fps,Hz'.", input: true)]),

        [195] = new(195, "Audio", "Reparación de GameInput",
            "Corrige los servicios duplicados de GameInput sin desactivar GameInputSvc, el servicio primario.",
            [A("Verificar", "Lista los servicios GameInput, su inicio/estado y cuál es el primario.", readOnly: true),
             A("Corregir", "Deshabilita los duplicados con respaldo y conserva GameInputSvc.", true),
             A("Listar todos los servicios", "Enumera todos los servicios GameInput con arranque, estado y respaldo.", readOnly: true),
             A("Restaurar servicios", "Recupera los estados guardados por Corregir.")]),

        [203] = new(203, "GPU / display", "ReBAR por juego",
            "Fuerza el Resizable BAR en el perfil DRS de NVIDIA para un ejecutable concreto, con verificación y opción de quitar. Entrada para activar: ejecutable del juego, por ejemplo Cyberpunk2077.exe.",
            [A("Verificar", "Comprueba si ReBAR está forzado en el perfil base.", readOnly: true),
             A("Activar", "Crea un perfil para el ejecutable indicado y aplica los valores community de ReBAR.", true, true),
             A("Quitar", "Elimina los valores ReBAR aplicados al perfil base.", true),
             A("Verificar juego", "Lee ReBAR en el perfil DRS del .exe indicado en la entrada.", input: true),
             A("Quitar del perfil del juego", "Elimina las 5 anulaciones del perfil del .exe indicado, sin tocar la base.", true, true)]),

        [204] = new(204, "GPU / display", "Sincronización de refresco",
            "Enumera los modos disponibles de cada monitor, detecta el máximo real y alinea la frecuencia activa.",
            [A("Escanear", "Enumera monitores, resolución y frecuencia actual/máxima.", readOnly: true),
             A("Sincronizar", "Ajusta cada monitor a la frecuencia máxima detectada con reversión temporal."),
             A("Sincronizar a 60", "Ajusta cada monitor a 60 Hz con vista previa y respaldo.", true),
             A("Sincronizar a 120", "Ajusta cada monitor a 120 Hz con vista previa y respaldo.", true),
             A("Restaurar frecuencia", "Devuelve la frecuencia original respaldada.")]),

        [208] = new(208, "Hardware", "Escudo de hardware",
            "Analiza el equipo y avisa de combinaciones de hardware o controladores que puedan limitar el rendimiento.",
            [A("Escanear", "Muestra un informe de hardware, controladores y recomendaciones.", readOnly: true),
             A("Verificación rápida", "Muestra comprobaciones concretas con estado [OK]/[AVISO].", readOnly: true),
             A("Exportar informe", "Guarda el informe completo de hardware y anticheats como texto.")])
    };

    public static bool Supports(int id) => Map.ContainsKey(id);

    public static ToolDefinition For(int id) => Map.TryGetValue(id, out ToolDefinition? definition)
        ? definition
        : new(id, "Herramienta", $"Herramienta nativa #{id}", "Consulta los detalles en el catálogo.",
            [A("Ejecutar", "Ejecuta la acción principal de esta herramienta.")]);

    public static string InputPlaceholder(int id) => id switch
    {
        67 => "Nivel de vibración (0-100)…",
        71 => "Vatios (por ejemplo 320)…",
        82 => "1.1.1.1 1.0.0.1",
        110 => "Microsoft.BingWeather",
        129 or 130 => "Tamaño de búfer (128-8192)…",
        150 => "Nombre o ID de producto (9N…)…",
        151 => "Comando a ejecutar con TrustedInstaller…",
        182 => "C:\\Juegos\\...",
        183 => "240,240",
        203 => "Cyberpunk2077.exe",
        _ => "Parámetro opcional (según la acción)…"
    };
}
