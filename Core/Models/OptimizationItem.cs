using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ApexKernelOptimizer.Core.Models;

public enum OptimizationCategory
{
    Peripherals, CpuMemoryKernel, PowerThermal, GpuDisplay, NetworkPing,
    AudioMultimedia, UiPrivacyQoL, ServicesStorage, NativeTools, Other
}

public enum RiskLevel { Green, Yellow, Red }

public sealed class RegistryChange
{
    public required string Hive { get; init; }
    public required string KeyPath { get; init; }
    public required string ValueName { get; init; }
    public required string ValueKind { get; init; }
    public required string ApplyValue { get; init; }
    public required string RevertValue { get; init; }
    public bool DeleteOnApply { get; init; }
}

public sealed class OptimizationItem : INotifyPropertyChanged
{
    private bool _isEnabled;
    private string _status = "Solo lectura";

    public required int Id { get; init; }
    public required OptimizationCategory Category { get; init; }
    public required string Title { get; init; }
    public required string ShortDescription { get; init; }
    public required string TechnicalSummary { get; init; }
    public required RiskLevel RiskLevel { get; init; }
    public string? WarningMessage { get; init; }
    public bool IsImplemented { get; init; }
    public bool IsTool { get; init; }
    public List<RegistryChange> RegistryChanges { get; init; } = [];
    public bool IsHardwareApplicable { get; init; } = true;

    private string? _selectedValue;
    /// <summary>Etiquetas legibles de las variantes disponibles para este ajuste.</summary>
    public List<string> Options { get; init; } = [];
    /// <summary>Valores que se escriben, en el mismo orden que Options.</summary>
    public List<string> OptionValues { get; init; } = [];
    public bool HasOptions => Options.Count > 0;
    public string? SelectedValue
    {
        get => _selectedValue ?? (Options.Count > 0 ? Options[0] : null);
        set
        {
            if (_selectedValue == value) return;
            _selectedValue = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedOptionValue));
            OnPropertyChanged(nameof(TargetValueText));
        }
    }
    /// <summary>Valor real correspondiente a la opción elegida (o null si el ajuste no tiene variantes).</summary>
    public string? SelectedOptionValue
    {
        get
        {
            if (!HasOptions) return null;
            int i = Options.IndexOf(SelectedValue!);
            return i >= 0 && i < OptionValues.Count ? OptionValues[i] : null;
        }
    }

    public string Status
    {
        get => _status;
        set { if (_status == value) return; _status = value; OnPropertyChanged(); }
    }

    private bool _isSelected;
    /// <summary>Selección para operaciones masivas en la vista Pro.</summary>
    public bool IsSelected { get => _isSelected; set { if (_isSelected == value) return; _isSelected = value; OnPropertyChanged(); } }

    private string _currentValueText = "—";
    /// <summary>Valor real leído del sistema para la vista Pro.</summary>
    public string CurrentValueText { get => _currentValueText; set { if (_currentValueText == value) return; _currentValueText = value; OnPropertyChanged(); } }

    /// <summary>Valor que se escribiría al aplicar (opción elegida o valor por defecto).</summary>
    private string _appliedOptionText = "—";
    /// <summary>Opción que coincide con el valor real del sistema en este momento.</summary>
    public string AppliedOptionText { get => _appliedOptionText; set { if (_appliedOptionText == value) return; _appliedOptionText = value; OnPropertyChanged(); } }

    public string TargetValueText
    {
        get
        {
            string? selected = SelectedOptionValue;
            if (selected == "__DELETE__") return "(ausente — valor eliminado)";
            return selected ?? (RegistryChanges.Count > 0 ? RegistryChanges[0].ApplyValue : "—");
        }
    }
    public string ActionLabel => IsTool ? "Abrir herramienta" : IsImplemented ? "Aplicar" : "Solo lectura";

    private bool _hasBackup;
    public bool HasBackup { get => _hasBackup; set { if (_hasBackup == value) return; _hasBackup = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanRestore)); } }

    private bool _hasDefault;
    public bool HasDefault { get => _hasDefault; set { if (_hasDefault == value) return; _hasDefault = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanSetDefault)); } }

    private string _defaultValueText = "—";
    public string DefaultValueText { get => _defaultValueText; set { if (_defaultValueText == value) return; _defaultValueText = value; OnPropertyChanged(); } }

    // Los tres botones existen siempre; se deshabilitan cuando la acción no procede,
    // pero nunca desaparecen de la interfaz.
    public bool CanApply => IsImplemented && !IsTool;
    public bool CanRestore => IsImplemented && !IsTool && HasBackup;
    public bool CanSetDefault => IsImplemented && !IsTool && HasDefault;

    public string StateLabel => !IsImplemented ? "Solo lectura"
        : IsTool ? "Herramienta"
        : IsEnabled ? "Aplicado" : "Sin aplicar";
    public string RiskLabel => RiskLevel switch { RiskLevel.Green => "VERDE", RiskLevel.Yellow => "AMARILLO", _ => "ROJO" };
    public string CategoryLabel => Category switch
    {
        OptimizationCategory.Peripherals => "Mouse y periféricos",
        OptimizationCategory.CpuMemoryKernel => "CPU, kernel y memoria",
        OptimizationCategory.PowerThermal => "Energía y térmica",
        OptimizationCategory.GpuDisplay => "GPU y pantalla",
        OptimizationCategory.NetworkPing => "Red y ping",
        OptimizationCategory.AudioMultimedia => "Audio y multimedia",
        OptimizationCategory.UiPrivacyQoL => "Interfaz y privacidad",
        OptimizationCategory.ServicesStorage => "Servicios y almacenamiento",
        OptimizationCategory.NativeTools => "Herramientas nativas",
        _ => "Por clasificar"
    };

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (_isEnabled == value) return;
            _isEnabled = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanApply));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
