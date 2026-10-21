using RustDroper.Core.Localization;

namespace RustDroper.App.ViewModels;

/// <summary>Başlat uyarısı penceresinin sonucu: kullanıcı "Bir daha gösterme"yi işaretledi mi.</summary>
public sealed record StartNoticeAnswer(bool DontShowAgain);

/// <summary>
/// Başlat'a basılınca (yalnız pencere açıkken, Duraklat'ta değil) gösterilen kısa bilgi penceresi için soyutlama; testte sahte ile atlanır.
/// Pencere kapatılırsa da Tamam sayılır (yalnız bilgilendirme: motor yine başlar).
/// </summary>
public interface IStartNoticePrompt
{
    Task<StartNoticeAnswer> ShowAsync();
}

public static class StartNoticeText
{
    public static string Title => Loc.T("startNotice.title");

    public static string Message => Loc.T("startNotice.message");
}
