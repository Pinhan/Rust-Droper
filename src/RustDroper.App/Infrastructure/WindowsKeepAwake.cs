using System.Runtime.InteropServices;
using RustDroper.Core.Orchestration;

namespace RustDroper.App.Infrastructure;

/// <summary>
/// <c>SetThreadExecutionState</c> ile uyku engelleme. Yürütme durumu iş parçacığına bağlıdır: çağrılar hep UI iş parçacığından
/// (uygulama ömrü boyunca yaşar) yapılır. Yalnızca <c>ES_SYSTEM_REQUIRED</c> kullanılır — ekranın kapanmasına izin verilir.
/// </summary>
public sealed class WindowsKeepAwake(IUiDispatcher ui) : IKeepAwake
{
    private const uint EsContinuous = 0x80000000;
    private const uint EsSystemRequired = 0x00000001;

    public void Hold() => ui.Post(() => _ = SetThreadExecutionState(EsContinuous | EsSystemRequired));

    public void Release() => ui.Post(() => _ = SetThreadExecutionState(EsContinuous));

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint SetThreadExecutionState(uint flags);
}
