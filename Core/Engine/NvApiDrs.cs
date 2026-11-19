using System.Runtime.InteropServices;

namespace ApexKernelOptimizer.Core.Engine;

/// <summary>
/// Wrapper mínimo de nvapi64.dll (DRS) para configurar perfiles del driver NVIDIA de forma nativa.
/// Todas las operaciones devuelven mensajes de error claros; nunca lanza excepciones no controladas.
/// </summary>
public static class NvApiDrs
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NvAPI_UnicodeString
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 2048)]
        public string Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NvDRSSetting
    {
        public uint Version;
        public uint SettingId;
        public uint SettingType;
        public uint IsCurrentGlobalProfile;
        public uint Location;
        public uint LocationId;
        public uint CurrentValue;
        public uint U32Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NvDRSProfile
    {
        public uint Version;
        public IntPtr Name;
        public uint GpuSupport;
        public uint ApplicationSupport;
        public uint NumOfSettings;
        public uint NumOfApplications;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NvDRSApplication
    {
        public uint Version;
        public uint PredefinedColorNote;
        public uint IsPredefined;
        public uint Executable_Name_Type;
        public IntPtr AppNameAndArguments;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 2048)] public string Executable_Name;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 2048)] public string File_Path;
    }

    private const uint NvUStringLen = 2048;
    private const uint DrsSettingVer = 0x20040;
    private const uint DrsProfileVer = 0x10050;
    private const uint DrsAppVer = 0x10020;

    // Códigos de estado NVAPI (se devuelven como int con complemento a dos).
    private const int NvApiProfileNotFound = unchecked((int)0xFFFFFC5B);
    private const int NvApiAppAlreadyExists = unchecked((int)0xFFFFFC57);
    private const int NvApiSettingNotFound = unchecked((int)0xFFFFFC1D);
    private const int NvApiDriverSettingNotFound = 0x1000017;

    // IDs de funciones de nvapi64.dll (nvapi_QueryInterface)
    private const uint IdInitialize = 0x0150E828;
    private const uint IdUnload = 0xD22BDD7E;
    private const uint IdDrsCreateSession = 0x0694D52E;
    private const uint IdDrsDestroySession = 0xDAD9CFF8;
    private const uint IdDrsLoadSettings = 0x375DBD6B;
    private const uint IdDrsSaveSettings = 0xFCBC7E14;
    private const uint IdDrsGetBaseProfile = 0xDA8466A0;
    private const uint IdDrsFindProfileByName = 0x7E4A9A0B;
    private const uint IdDrsCreateProfile = 0xCC176068;
    private const uint IdDrsDeleteProfile = 0x17093206;
    private const uint IdDrsCreateApplication = 0x4347A9DE;
    private const uint IdDrsSetSetting = 0x577DD202;
    private const uint IdDrsGetSetting = 0x73BF8338;
    private const uint IdDrsDeleteProfileSetting = 0xE4A26362;
    private const uint IdDrsRestoreProfileDefaultSetting = 0x53F0381E;
    private const uint IdEnumPhysicalGPUs = 0xE5AC921F;
    private const uint IdSetScanoutIntensity = 0xA57457A4;
    private const uint IdGetScanoutIntensityState = 0xE81CE836;

    private static readonly object Gate = new();
    private static IntPtr _interface;
    private static readonly Dictionary<uint, IntPtr> Functions = new();

    private static bool TryAcquire(out string error)
    {
        error = "";
        lock (Gate)
        {
            if (_interface != IntPtr.Zero) return true;
            if (!NativeLibrary.TryLoad("nvapi64.dll", out _interface) || _interface == IntPtr.Zero)
            {
                error = "No se encontró nvapi64.dll; no parece haber un driver NVIDIA instalado.";
                return false;
            }
            IntPtr queryInterface = NativeLibrary.GetExport(_interface, "nvapi_QueryInterface");
            foreach (uint id in new[] { IdInitialize, IdUnload, IdDrsCreateSession, IdDrsDestroySession, IdDrsLoadSettings, IdDrsSaveSettings, IdDrsGetBaseProfile, IdDrsFindProfileByName, IdDrsCreateProfile, IdDrsDeleteProfile, IdDrsCreateApplication, IdDrsSetSetting, IdDrsGetSetting, IdDrsDeleteProfileSetting, IdDrsRestoreProfileDefaultSetting, IdEnumPhysicalGPUs, IdSetScanoutIntensity, IdGetScanoutIntensityState })
            {
                IntPtr fn = Marshal.GetDelegateForFunctionPointer<QueryInterfaceDelegate>(queryInterface)(id);
                Functions[id] = fn;
            }
            int status = Marshal.GetDelegateForFunctionPointer<StatusNoArgDelegate>(Functions[IdInitialize])();
            if (status != 0)
            {
                NativeLibrary.Free(_interface);
                _interface = IntPtr.Zero;
                error = $"NvAPI_Initialize devolvió {status} (no hay GPU NVIDIA disponible).";
                return false;
            }
            return true;
        }
    }

    private delegate IntPtr QueryInterfaceDelegate(uint functionId);
    private delegate int StatusDelegate(IntPtr handle);
    private delegate int StatusNoArgDelegate();
    private delegate int SessionOutDelegate(IntPtr session, out IntPtr profile);
    private delegate int EnumGpusDelegate(out IntPtr gpuHandlesPtr, out uint count);

    private static IntPtr Fn(uint id) => Functions.TryGetValue(id, out IntPtr fn) ? fn : IntPtr.Zero;

    private static NvAPI_UnicodeString ToUnicode(string value) => new() { Data = value };

    private static IntPtr AllocUnicode(string value)
    {
        IntPtr ptr = Marshal.AllocHGlobal((int)(NvUStringLen * 2 + 4));
        byte[] bytes = new byte[NvUStringLen * 2];
        string trimmed = value.Length > (int)NvUStringLen - 1 ? value[..(int)(NvUStringLen - 1)] : value;
        Array.Copy(System.Text.Encoding.Unicode.GetBytes(trimmed + "\0"), bytes, Math.Min(bytes.Length, System.Text.Encoding.Unicode.GetBytes(trimmed + "\0").Length));
        Marshal.Copy(bytes, 0, ptr, bytes.Length);
        return ptr;
    }

    public static string ApplyDrsValues(string? profileName, string? applicationExe, IReadOnlyList<(uint SettingId, uint Value)> settings)
    {
        lock (Gate)
        {
            if (!TryAcquire(out string error)) return error;
            IntPtr session = IntPtr.Zero, profile = IntPtr.Zero;
            try
            {
                int status = Marshal.GetDelegateForFunctionPointer<OutPointerDelegate>(Fn(IdDrsCreateSession))(out session);
                if (status != 0) return $"No se pudo crear la sesión DRS (código {status}).";
                status = Marshal.GetDelegateForFunctionPointer<StatusDelegate>(Fn(IdDrsLoadSettings))(session);
                if (status != 0) return $"No se pudo cargar la base de perfiles (código {status}).";

                if (profileName is null)
                {
                    status = Marshal.GetDelegateForFunctionPointer<SessionOutDelegate>(Fn(IdDrsGetBaseProfile))(session, out profile);
                    if (status != 0) return $"No se pudo obtener el perfil base (código {status}).";
                }
                else
                {
                    IntPtr namePtr = AllocUnicode(profileName);
                    try
                    {
                        status = Marshal.GetDelegateForFunctionPointer<FindProfileDelegate>(Fn(IdDrsFindProfileByName))(session, namePtr, out profile);
                        if (status == NvApiDriverSettingNotFound || status == NvApiProfileNotFound || status != 0) // no encontrado
                        {
                            IntPtr unicodeName = AllocUnicode(profileName);
                            var prof = new NvDRSProfile { Version = DrsProfileVer, Name = unicodeName };
                            IntPtr profPtr = Marshal.AllocHGlobal(Marshal.SizeOf<NvDRSProfile>());
                            try
                            {
                                Marshal.StructureToPtr(prof, profPtr, false);
                                status = Marshal.GetDelegateForFunctionPointer<CreateProfileDelegate>(Fn(IdDrsCreateProfile))(session, profPtr, out profile);
                            }
                            finally { Marshal.FreeHGlobal(profPtr); Marshal.FreeHGlobal(unicodeName); }
                            if (status != 0) return $"No se pudo crear el perfil {profileName} (código {status}).";
                        }
                    }
                    finally { Marshal.FreeHGlobal(namePtr); }

                    if (applicationExe is not null)
                    {
                        var app = new NvDRSApplication
                        {
                            Version = DrsAppVer,
                            Executable_Name = applicationExe,
                            Executable_Name_Type = 0
                        };
                        IntPtr appName = AllocUnicode(applicationExe);
                        app.AppNameAndArguments = appName;
                        IntPtr appPtr = Marshal.AllocHGlobal(Marshal.SizeOf<NvDRSApplication>());
                        try
                        {
                            Marshal.StructureToPtr(app, appPtr, false);
                            status = Marshal.GetDelegateForFunctionPointer<CreateAppDelegate>(Fn(IdDrsCreateApplication))(session, profile, appPtr);
                        }
                        finally { Marshal.FreeHGlobal(appPtr); Marshal.FreeHGlobal(appName); }
                        if (status != 0 && status != NvApiAppAlreadyExists) // ya existe
                            return $"No se pudo asociar {applicationExe} al perfil (código {status}).";
                    }
                }

                foreach ((uint settingId, uint value) in settings)
                {
                    var setting = new NvDRSSetting { Version = DrsSettingVer, SettingId = settingId, SettingType = 0, U32Value = value, CurrentValue = value };
                    IntPtr setPtr = Marshal.AllocHGlobal(Marshal.SizeOf<NvDRSSetting>());
                    try
                    {
                        Marshal.StructureToPtr(setting, setPtr, false);
                        status = Marshal.GetDelegateForFunctionPointer<SetSettingDelegate>(Fn(IdDrsSetSetting))(session, profile, setPtr);
                    }
                    finally { Marshal.FreeHGlobal(setPtr); }
                    if (status != 0) return $"El controlador no aceptó el ajuste 0x{settingId:X8} (código {status}).";
                }

                status = Marshal.GetDelegateForFunctionPointer<StatusDelegate>(Fn(IdDrsSaveSettings))(session);
                if (status != 0) return $"No se pudieron guardar los perfiles (código {status}).";
                return $"Perfiles de NVIDIA actualizados ({settings.Count} ajuste(s){(profileName is null ? " en el perfil Base" : $" en el perfil {profileName}")}).";
            }
            catch (Exception ex)
            {
                return $"No se pudo configurar el driver NVIDIA: {ex.Message}";
            }
            finally
            {
                if (session != IntPtr.Zero) { try { Marshal.GetDelegateForFunctionPointer<StatusDelegate>(Fn(IdDrsDestroySession))(session); } catch { } }
            }
        }
    }

    private delegate int OutPointerDelegate(out IntPtr pointer);
    private delegate int FindProfileDelegate(IntPtr session, IntPtr name, out IntPtr profile);
    private delegate int CreateProfileDelegate(IntPtr session, IntPtr profile, out IntPtr newProfile);
    private delegate int CreateAppDelegate(IntPtr session, IntPtr profile, IntPtr application);
    private delegate int SetSettingDelegate(IntPtr session, IntPtr profile, IntPtr setting);
    private delegate int DeleteSettingDelegate(IntPtr session, IntPtr profile, uint settingId);

    /// <summary>Lee el valor actual de un ajuste DRS en el perfil base o en un perfil por nombre. Devuelve error vacío si se leyó bien.</summary>
    public static (string Error, uint Value) ReadDrsSetting(string? profileName, uint settingId)
    {
        lock (Gate)
        {
            if (!TryAcquire(out string error)) return (error, 0);
            int status = OpenProfile(profileName, out IntPtr session, out IntPtr profile, out IntPtr namePtr);
            if (status != 0) { CleanupSession(session, profileName is not null ? namePtr : IntPtr.Zero); return (status == NvApiProfileNotFound ? "No se encontró el perfil." : $"No se pudo abrir el perfil (código {status}).", 0); }
            try
            {
                var setting = new NvDRSSetting { Version = DrsSettingVer, SettingId = settingId };
                IntPtr setPtr = Marshal.AllocHGlobal(Marshal.SizeOf<NvDRSSetting>());
                NvDRSSetting read;
                try
                {
                    Marshal.StructureToPtr(setting, setPtr, false);
                    status = Marshal.GetDelegateForFunctionPointer<GetSettingDelegate>(Fn(IdDrsGetSetting))(session, profile, setPtr);
                    // Leer ANTES de liberar: el código anterior usaba el puntero después de FreeHGlobal.
                    read = Marshal.PtrToStructure<NvDRSSetting>(setPtr);
                }
                finally { Marshal.FreeHGlobal(setPtr); }
                if (status == NvApiSettingNotFound || status == NvApiDriverSettingNotFound) return ("", 0); // ajuste no presente en el perfil
                if (status != 0) return ($"El controlador no devolvió el ajuste 0x{settingId:X8} (código {status}).", 0);
                return ("", read.CurrentValue);
            }
            catch (Exception ex) { return ($"No se pudo leer el driver NVIDIA: {ex.Message}", 0); }
            finally { CleanupSession(session, profileName is not null ? namePtr : IntPtr.Zero); }
        }
    }

    /// <summary>Restaura el ajuste DRS al valor por defecto del driver.</summary>
    public static string ResetDrsSetting(string? profileName, uint settingId)
    {
        lock (Gate)
        {
            if (!TryAcquire(out string error)) return error;
            int status = OpenProfile(profileName, out IntPtr session, out IntPtr profile, out IntPtr namePtr);
            if (status != 0) { CleanupSession(session, profileName is not null ? namePtr : IntPtr.Zero); return $"No se pudo abrir el perfil (código {status})."; }
            try
            {
                status = Marshal.GetDelegateForFunctionPointer<DeleteSettingDelegate>(Fn(IdDrsRestoreProfileDefaultSetting))(session, profile, settingId);
                if (status != 0) return $"El controlador no restauró el ajuste 0x{settingId:X8} (código {status}).";
                status = Marshal.GetDelegateForFunctionPointer<StatusDelegate>(Fn(IdDrsSaveSettings))(session);
                return status != 0 ? $"No se pudieron guardar los perfiles (código {status})." : "Ajuste del driver NVIDIA restaurado a su valor por defecto.";
            }
            catch (Exception ex) { return $"No se pudo restaurar el driver NVIDIA: {ex.Message}"; }
            finally { CleanupSession(session, profileName is not null ? namePtr : IntPtr.Zero); }
        }
    }

    /// <summary>Elimina una anulación de ajuste DRS de un perfil.</summary>
    public static string DeleteDrsSetting(string? profileName, uint settingId)
    {
        lock (Gate)
        {
            if (!TryAcquire(out string error)) return error;
            int status = OpenProfile(profileName, out IntPtr session, out IntPtr profile, out IntPtr namePtr);
            if (status != 0) { CleanupSession(session, profileName is not null ? namePtr : IntPtr.Zero); return $"No se pudo abrir el perfil (código {status})."; }
            try
            {
                status = Marshal.GetDelegateForFunctionPointer<DeleteSettingDelegate>(Fn(IdDrsDeleteProfileSetting))(session, profile, settingId);
                if (status == NvApiSettingNotFound || status == NvApiDriverSettingNotFound) return "El ajuste no estaba anulado en el perfil; nada que eliminar.";
                if (status != 0) return $"El controlador no eliminó el ajuste 0x{settingId:X8} (código {status}).";
                status = Marshal.GetDelegateForFunctionPointer<StatusDelegate>(Fn(IdDrsSaveSettings))(session);
                return status != 0 ? $"No se pudieron guardar los perfiles (código {status})." : "Ajuste eliminado del perfil NVIDIA.";
            }
            catch (Exception ex) { return $"No se pudo editar el driver NVIDIA: {ex.Message}"; }
            finally { CleanupSession(session, profileName is not null ? namePtr : IntPtr.Zero); }
        }
    }

    /// <summary>Aplica Digital Vibrance (saturación de salida) 0-100 en las GPU NVIDIA mediante NvAPI_GPU_SetScanoutIntensity.</summary>
    public static string SetDigitalVibrance(int percent)
    {
        lock (Gate)
        {
            if (!TryAcquire(out string error)) return error;
            try
            {
                int status = Marshal.GetDelegateForFunctionPointer<EnumGpusDelegate>(Fn(IdEnumPhysicalGPUs))(out IntPtr gpuHandle, out uint count);
                if (status != 0 || count == 0) return $"No se detectaron GPU NVIDIA (código {status}).";
                int boost = 4096 + Math.Clamp(percent, 0, 100) * 16; // Q12 base 1.0 + refuerzo
                Span<int> coefficients = stackalloc int[9];
                coefficients[0] = boost; coefficients[4] = boost; coefficients[8] = boost;
                var intensity = new NvScanoutIntensity { Version = (uint)(Marshal.SizeOf<NvScanoutIntensity>() | (1 << 16)), Cmd = 1, Coefficient = new int[9] };
                for (int i = 0; i < 9; i++) intensity.Coefficient[i] = coefficients[i];
                IntPtr ptr = Marshal.AllocHGlobal(Marshal.SizeOf<NvScanoutIntensity>());
                try
                {
                    Marshal.StructureToPtr(intensity, ptr, false);
                    status = Marshal.GetDelegateForFunctionPointer<TwoPointerStatusDelegate>(Fn(IdSetScanoutIntensity))(Marshal.ReadIntPtr(gpuHandle), ptr);
                }
                finally { Marshal.FreeHGlobal(ptr); }
                return status != 0 ? $"El controlador no aceptó la vibración digital (código {status})." : $"Digital Vibrance aplicada al {percent}% en las salidas NVIDIA.";
            }
            catch (Exception ex) { return $"No se pudo aplicar Digital Vibrance: {ex.Message}"; }
        }
    }

    private static int OpenProfile(string? profileName, out IntPtr session, out IntPtr profile, out IntPtr namePtr)
    {
        session = profile = namePtr = IntPtr.Zero;
        int status = Marshal.GetDelegateForFunctionPointer<OutPointerDelegate>(Fn(IdDrsCreateSession))(out session);
        if (status != 0) return status;
        status = Marshal.GetDelegateForFunctionPointer<StatusDelegate>(Fn(IdDrsLoadSettings))(session);
        if (status != 0) return status;
        if (profileName is null)
        {
            return Marshal.GetDelegateForFunctionPointer<SessionOutDelegate>(Fn(IdDrsGetBaseProfile))(session, out profile);
        }
        namePtr = AllocUnicode(profileName);
        status = Marshal.GetDelegateForFunctionPointer<FindProfileDelegate>(Fn(IdDrsFindProfileByName))(session, namePtr, out profile);
        return status;
    }

    private static void CleanupSession(IntPtr session, IntPtr namePtr)
    {
        if (session != IntPtr.Zero) { try { Marshal.GetDelegateForFunctionPointer<StatusDelegate>(Fn(IdDrsDestroySession))(session); } catch { } }
        if (namePtr != IntPtr.Zero) Marshal.FreeHGlobal(namePtr);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NvScanoutIntensity
    {
        public uint Version;
        public uint Cmd;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 9)]
        public int[] Coefficient;
    }

    private delegate int GetSettingDelegate(IntPtr session, IntPtr profile, IntPtr setting);
    private delegate int TwoPointerStatusDelegate(IntPtr handle, IntPtr data);
}
