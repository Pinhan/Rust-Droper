using System.Net;
using RustDroper.Core.Localization;

namespace RustDroper.Core.Platforms;

/// <summary>Ayrıştırma sonucu: geçerli öğeler + atlanan/şüpheli öğeler için uyarılar (tek bozuk öğe tüm listeyi öldürmez).</summary>
public sealed record ParseResult<T>(IReadOnlyList<T> Items, IReadOnlyList<string> Warnings);

/// <summary>Platform cevabının beklenen şekilde olmaması (şema/hash kayması). Kalıcı sınıf; geçici ağ hatası değildir.</summary>
public class PlatformDataException : Exception
{
    public PlatformDataException(string message)
        : base(message)
    {
    }

    public PlatformDataException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>Kimlik reddedildi (token geçersiz/yetkisiz). Motor "yeniden giriş gerekli" durumuna geçer; platformdan bağımsız taban sınıf.</summary>
public class PlatformAuthException(string message) : Exception(message);

/// <summary>Platform HTTP isteğinin başarısız dönmesi. 5xx/429 geçici, 401/403 kimlik/engel olarak sınıflanır.</summary>
public sealed class PlatformHttpException : Exception
{
    public PlatformHttpException(HttpStatusCode statusCode, Uri requestUri, TimeSpan? retryAfter = null)
        : base(Loc.T("err.httpStatus", requestUri.Host, (int)statusCode, statusCode))
    {
        StatusCode = statusCode;
        RequestUri = requestUri;
        RetryAfter = retryAfter;
    }

    public HttpStatusCode StatusCode { get; }

    public Uri RequestUri { get; }

    /// <summary>Sunucunun <c>Retry-After</c> ile istediği bekleme (varsa).</summary>
    public TimeSpan? RetryAfter { get; }

    public bool IsTransient => (int)StatusCode >= 500 || StatusCode == HttpStatusCode.TooManyRequests || StatusCode == HttpStatusCode.RequestTimeout;
}
