using System;
using System.Windows.Data;
using System.Windows.Markup;

namespace SEP.App.Localization;

/// <summary>
/// XAML shorthand for a live-updating localized string: <c>Content="{loc:Tr Nav_Projects}"</c>
/// expands to <c>{Binding [Nav_Projects], Source={x:Static loc:Loc.Instance}, Mode=OneWay}</c>.
/// Only valid on dependency properties (Text, Content, Title, ToolTip, PlaceholderText, ...).
/// </summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class TrExtension : MarkupExtension
{
    public TrExtension() { }

    public TrExtension(string key) => Key = key;

    /// <summary>Resource key in Strings.resx.</summary>
    [ConstructorArgument("key")]
    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new Binding($"[{Key}]") { Source = Loc.Instance, Mode = BindingMode.OneWay };
        return binding.ProvideValue(serviceProvider);
    }
}
