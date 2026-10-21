using System.Runtime.CompilerServices;
using System.Runtime.Versioning;

// Ürün Windows-only (DPAPI, WPF, WebView2): platform-uyumluluk analizörü (CA1416) bunu bilsin.
[assembly: SupportedOSPlatform("windows")]
[assembly: InternalsVisibleTo("RustDroper.Core.Tests")]
