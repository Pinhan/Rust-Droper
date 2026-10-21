using System.Windows;
using System.Windows.Data;
using System.Windows.Markup;

namespace RustDroper.App.Localization;

/// <summary>
/// <c>{loc:Loc settings.title}</c>: anahtarın geçerli dildeki metnine bağlanır ve dil değişince kendiliğinden yenilenir.
/// Bağlama <see cref="LocSource"/>'a yapılır (tek yönlü).
/// </summary>
[MarkupExtensionReturnType(typeof(string))]
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
        var binding = new Binding
        {
            Source = LocSource.Instance,
            Path = new PropertyPath("[" + Key + "]"),
            Mode = BindingMode.OneWay,
        };

        // Stil/şablon içindeki Setter gibi DependencyObject olmayan hedeflerde bağlamanın kendisi döner (WPF sonra uygular).
        return serviceProvider.GetService(typeof(IProvideValueTarget)) is IProvideValueTarget { TargetObject: DependencyObject }
            ? binding.ProvideValue(serviceProvider)
            : binding;
    }
}
