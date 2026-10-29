using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Markup;

namespace FlowLedger.Desktop.Localization;

// Columns do not inherit the visual resource tree. An explicit binding source makes their
// headers update immediately, including columns that were previously offscreen/collapsed.
public sealed class UiResourceSource : INotifyPropertyChanged
{
    public static UiResourceSource Instance { get; } = new();
    public event PropertyChangedEventHandler? PropertyChanged;
    public string this[string key] => UiCatalog.Entries[key].ToString();
    public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
}

[MarkupExtensionReturnType(typeof(object))]
public sealed class TextExtension(string key) : MarkupExtension
{
    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding($"[{key}]") { Source = UiResourceSource.Instance, Mode = BindingMode.OneWay }.ProvideValue(serviceProvider);
}
