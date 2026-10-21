# Third-party notices / Üçüncü taraf bildirimleri

Rust Droper itself is released under the [MIT License](LICENSE). The release binary (`RustDroper.exe`, a self-contained single-file build) bundles the components below; their licenses apply to them. License identifiers and copyright lines were taken from the package metadata (`.nuspec`) of the exact versions used (see the `.csproj` files).

Rust Droper'ın kendisi [MIT Lisansı](LICENSE) ile yayımlanır. Yayın dosyası (`RustDroper.exe`, kendi içinde çalışma zamanını taşıyan tek dosya) aşağıdaki bileşenleri içerir; bunlar kendi lisanslarına tabidir. Lisans kimlikleri ve telif satırları, kullanılan sürümlerin paket meta verilerinden (`.nuspec`) alınmıştır.

## Runtime (shipped in the release binary) / Çalışma zamanı (yayın dosyasında)

| Component | Version | License | Notes |
|---|---|---|---|
| .NET 10 runtime and WPF (Microsoft) | 10.x | MIT | Bundled because the build is self-contained. <https://github.com/dotnet/runtime>, <https://github.com/dotnet/wpf> |
| Microsoft.Extensions.Logging.Abstractions (Microsoft) | 10.0.13 | MIT | © Microsoft Corporation |
| Microsoft.Extensions.DependencyInjection.Abstractions (Microsoft) | 10.0.13 | MIT | transitive dependency |
| System.Security.Cryptography.ProtectedData (Microsoft) | 10.0.13 | MIT | Windows DPAPI wrapper (stores sign-in data encrypted) |
| QRCoder (Raffael Herrmann, Shane Krueger) | 1.8.1 | MIT | Draws the Kick sign-in QR code locally; nothing is sent to a QR service |
| Microsoft.Web.WebView2 (Microsoft) | 1.0.4258.31 | Microsoft BSD-3-Clause-style license (full text below) | .NET bindings/loader for the Kick player. The **WebView2 Runtime itself is not shipped**: it is the Evergreen runtime that Windows 10/11 provides (installed by Microsoft Edge updates). |

## Build and test only (not shipped) / Yalnız derleme ve test (yayın dosyasında yok)

| Component | Version | License |
|---|---|---|
| xunit (+ xunit.core, xunit.assert, xunit.extensibility.*) | 2.9.3 | Apache-2.0 |
| xunit.runner.visualstudio | 3.1.4 | Apache-2.0 |
| Microsoft.NET.Test.Sdk | 17.14.1 | MIT |
| Microsoft.Extensions.TimeProvider.Testing | 10.11.0 | MIT |
| coverlet.collector | 6.0.4 | MIT |

## Reference projects / Referans projeler

Rust Droper's design was informed by an analysis of the behaviour of the open-source projects *TwitchDropsMiner*, *TwitchDropsBot* and *StreamDropCollector* (what the Twitch/Kick endpoints do, which failure modes exist). **No source code from these projects was copied or translated**; Rust Droper is an independent C# implementation. Public protocol facts (endpoint names, public application client IDs, persisted-query hashes, JSON field names) are interoperability information about Twitch and Kick, not code from those projects.

Rust Droper'ın tasarımı, açık kaynak *TwitchDropsMiner*, *TwitchDropsBot* ve *StreamDropCollector* projelerinin davranış analizinden esinlenmiştir. Bu projelerden **kaynak kod kopyalanmamış veya çevrilmemiştir**; Rust Droper bağımsız bir C# gerçeklemesidir. Herkese açık protokol bilgileri (uç nokta adları, herkese açık uygulama istemci kimlikleri, kalıcı sorgu özetleri, JSON alan adları) Twitch ve Kick ile birlikte çalışma bilgisidir; bu projelerin kodu değildir.

## Trademarks / Markalar

Twitch, Kick, Facepunch Studios, Rust, Steam, Windows, Microsoft Edge and WebView2 are trademarks of their respective owners. Rust Droper is not affiliated with, endorsed by or sponsored by any of them. Facepunch images are **not** redistributed in this repository or in the release; the app downloads them from Facepunch's website at run time and caches them locally.

Twitch, Kick, Facepunch Studios, Rust, Steam, Windows, Microsoft Edge ve WebView2 ilgili sahiplerinin markalarıdır. Rust Droper bunlarla bağlantılı değildir, onlarca onaylanmamış veya desteklenmemiştir. Facepunch görselleri bu depoda ve yayında **yeniden dağıtılmaz**; uygulama bunları çalışırken Facepunch'ın sitesinden indirip yerelde önbelleğe alır.

---

## License texts / Lisans metinleri

### MIT License (.NET, Microsoft.Extensions.*, System.Security.Cryptography.ProtectedData, test tools)

```
The MIT License (MIT)

Copyright (c) .NET Foundation and Contributors / Microsoft Corporation

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

### QRCoder 1.8.1 (MIT)

```
The MIT License (MIT)

Copyright (c) 2013-2025 Raffael Herrmann
Copyright (c) 2024-2025 Shane Krueger

Permission is hereby granted, free of charge, to any person obtaining a copy of
this software and associated documentation files (the "Software"), to deal in
the Software without restriction, including without limitation the rights to
use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of
the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS
FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR
COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER
IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN
CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
```

### Microsoft.Web.WebView2 1.0.4258.31

```
Copyright (C) Microsoft Corporation. All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are
met:

   * Redistributions of source code must retain the above copyright
notice, this list of conditions and the following disclaimer.
   * Redistributions in binary form must reproduce the above
copyright notice, this list of conditions and the following disclaimer
in the documentation and/or other materials provided with the
distribution.
   * The name of Microsoft Corporation, or the names of its contributors 
may not be used to endorse or promote products derived from this
software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
"AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT
OWNER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT
LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,
DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY
THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```

The WebView2 NuGet package also ships a `NOTICE.txt` with further third-party attributions for the SDK; it is part of the package (`microsoft.web.webview2/1.0.4258.31/NOTICE.txt`).

### Apache-2.0 (xunit, build/test only)

xunit is licensed under the Apache License, Version 2.0 — <https://www.apache.org/licenses/LICENSE-2.0>. It is used only to build and run the tests and is not part of the release binary.
