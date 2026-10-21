using System.Runtime.CompilerServices;
using RustDroper.Core.Localization;

namespace RustDroper.App.Localization;

/// <summary>Uygulama katmanı metin tablolarını modül yüklenince kaydeder (hangi kod yoluyla girilirse girilsin tablolar hazır olur).</summary>
internal static class AppLocalization
{
    [ModuleInitializer]
    internal static void Init() => Loc.Register("app", AppStrings.All);
}
