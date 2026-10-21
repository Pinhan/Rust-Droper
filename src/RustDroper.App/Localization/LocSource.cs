using System.ComponentModel;
using RustDroper.Core.Localization;

namespace RustDroper.App.Localization;

/// <summary>
/// XAML için tek bir bağlama kaynağı: <c>{loc:Loc (anahtar)}</c> bu nesnenin indeksleyicisine bağlanır; dil değişince <c>Item[]</c> bildirimi tüm metinleri
/// yeniden okutur (pencereyi yeniden kurmadan, anında). Zamanlayıcı/çizim yoktur; yalnız dil değişiminde tek olay.
/// </summary>
public sealed class LocSource : INotifyPropertyChanged
{
    public static LocSource Instance { get; } = new();

    private LocSource()
    {
        Loc.LanguageChanged += () => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string this[string key] => Loc.T(key);
}
