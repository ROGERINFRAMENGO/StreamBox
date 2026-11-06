using System.Runtime.InteropServices;
using WinRT;

namespace StreamBox.Streaming;

/// <summary>
/// Interop com APIs WinRT. Usa COM interop clássico (RCW) porque objetos de fábrica
/// do sistema não podem ser embrulhados pelo ComWrappers gerado.
/// </summary>
public static class WinRtInterop
{
    /// <summary>Ponteiro nativo de um objeto WinRT (referência deve ser liberada depois do uso).</summary>
    public static IntPtr FromInspectable(object value) => MarshalInspectable<object>.FromManaged(value);

    /// <summary>Embrulha um ponteiro COM em um objeto gerenciável clássico.</summary>
    public static T Wrap<T>(IntPtr pointer) where T : class =>
        (T)Marshal.GetObjectForIUnknown(pointer);

    public static void Release(IntPtr pointer)
    {
        if (pointer == IntPtr.Zero) return;
        try { Marshal.Release(pointer); } catch { }
    }
}
