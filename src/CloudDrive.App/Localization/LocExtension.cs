using System;
using WpfBinding = System.Windows.Data.Binding;
using WpfBindingMode = System.Windows.Data.BindingMode;
using WpfUpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger;
using System.Windows.Markup;
using CloudDrive.Core.Localization;

namespace CloudDrive.App.Localization;

[MarkupExtensionReturnType(typeof(object))]
public sealed class LocExtension : MarkupExtension
{
    public LocExtension()
    {
    }

    public LocExtension(string key)
    {
        Key = key;
    }

    [ConstructorArgument("key")]
    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        return new WpfBinding($"[{Key}]")
        {
            Source = AppLocalizer.Instance,
            Mode = WpfBindingMode.OneWay,
            UpdateSourceTrigger = WpfUpdateSourceTrigger.PropertyChanged
        }.ProvideValue(serviceProvider);
    }
}
