using System.Reflection;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace Ducky.Core.Audio;

/// <summary>
/// AOT-safe replacement for NAudio's MMDeviceEnumerator, which uses ComImport coclass
/// activation that Native AOT cannot compile. Uses CLSID activation instead.
/// </summary>
internal sealed class AotDeviceEnumerator : IDisposable
{
    private static readonly Guid MmDeviceEnumeratorClsid = new("BCDE0395-E52F-467C-8E3D-C4579291692E");

    private static readonly Type ImmDeviceType;
    private static readonly Type ImmDeviceCollectionType;
    private static readonly Type ImmDeviceEnumeratorType;
    private static readonly ConstructorInfo MmDeviceCtor;
    private static readonly ConstructorInfo MmDeviceCollectionCtor;
    private static readonly MethodInfo EnumAudioEndpointsMethod;
    private static readonly MethodInfo GetDefaultAudioEndpointMethod;

    private readonly object _enumerator;

    static AotDeviceEnumerator()
    {
        var wasapi = typeof(MMDevice).Assembly;
        ImmDeviceType = wasapi.GetType("NAudio.CoreAudioApi.Interfaces.IMMDevice", true)!;
        ImmDeviceCollectionType = wasapi.GetType("NAudio.CoreAudioApi.Interfaces.IMMDeviceCollection", true)!;
        ImmDeviceEnumeratorType = wasapi.GetType("NAudio.CoreAudioApi.Interfaces.IMMDeviceEnumerator", true)!;

        MmDeviceCtor = typeof(MMDevice).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            types: [ImmDeviceType],
            modifiers: null)!;

        MmDeviceCollectionCtor = typeof(MMDeviceCollection).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            types: [ImmDeviceCollectionType],
            modifiers: null)!;

        EnumAudioEndpointsMethod = ImmDeviceEnumeratorType.GetMethod("EnumAudioEndpoints")!;
        GetDefaultAudioEndpointMethod = ImmDeviceEnumeratorType.GetMethod("GetDefaultAudioEndpoint")!;
    }

    public AotDeviceEnumerator()
    {
        var coclassType = Type.GetTypeFromCLSID(MmDeviceEnumeratorClsid)
            ?? throw new InvalidOperationException("MMDeviceEnumerator COM class is not registered.");

        _enumerator = Activator.CreateInstance(coclassType)
            ?? throw new InvalidOperationException("Failed to activate MMDeviceEnumerator.");
    }

    public MMDeviceCollection EnumerateAudioEndPoints(DataFlow dataFlow, DeviceState dwStateMask)
    {
        var args = new object?[] { dataFlow, dwStateMask, null };
        var hr = (int)EnumAudioEndpointsMethod.Invoke(_enumerator, args)!;
        Marshal.ThrowExceptionForHR(hr);
        return (MMDeviceCollection)MmDeviceCollectionCtor.Invoke([args[2]])!;
    }

    public MMDevice GetDefaultAudioEndpoint(DataFlow dataFlow, Role role)
    {
        var args = new object?[] { dataFlow, role, null };
        var hr = (int)GetDefaultAudioEndpointMethod.Invoke(_enumerator, args)!;
        Marshal.ThrowExceptionForHR(hr);
        return (MMDevice)MmDeviceCtor.Invoke([args[2]])!;
    }

    public void Dispose()
    {
        if (_enumerator is not null)
        {
            Marshal.ReleaseComObject(_enumerator);
        }
    }
}
