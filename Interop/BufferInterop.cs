using System.Runtime.InteropServices;
using Windows.Foundation;
using Windows.Storage.Streams;
using WinRT;

namespace Talky.Interop;

/// <summary>
/// Direct pointer access to WinRT buffers (AudioFrame / SoftwareBitmap / WriteableBitmap) through
/// <c>IMemoryBufferByteAccess</c> and <c>IBufferByteAccess</c>. Calls the COM vtable directly so it
/// needs no built-in COM interop and stays trim/AOT safe.
/// </summary>
internal static unsafe class BufferInterop
{
    private static readonly Guid IidMemoryBufferByteAccess = new("5b0d3235-4dba-4d44-865e-8f1d0e4fd04d");
    private static readonly Guid IidBufferByteAccess = new("905a0fef-bc53-11df-8c49-001e4fc686da");

    /// <summary>Pointer stays valid while <paramref name="reference"/> is alive (not disposed).</summary>
    public static byte* GetPointer(IMemoryBufferReference reference, out uint capacity)
    {
        var access = QueryInterface(reference, IidMemoryBufferByteAccess);
        try
        {
            byte* data;
            uint size;
            // IMemoryBufferByteAccess::GetBuffer(BYTE** value, UINT32* capacity) — vtable slot 3.
            var getBuffer = (delegate* unmanaged[Stdcall]<nint, byte**, uint*, int>)(*(void***)access)[3];
            Marshal.ThrowExceptionForHR(getBuffer(access, &data, &size));
            capacity = size;
            return data;
        }
        finally
        {
            Marshal.Release(access);
            GC.KeepAlive(reference);
        }
    }

    /// <summary>Pointer stays valid while <paramref name="buffer"/> is alive.</summary>
    public static byte* GetPointer(IBuffer buffer)
    {
        var access = QueryInterface(buffer, IidBufferByteAccess);
        try
        {
            byte* data;
            // IBufferByteAccess::Buffer(byte** value) — vtable slot 3.
            var getBuffer = (delegate* unmanaged[Stdcall]<nint, byte**, int>)(*(void***)access)[3];
            Marshal.ThrowExceptionForHR(getBuffer(access, &data));
            return data;
        }
        finally
        {
            Marshal.Release(access);
            GC.KeepAlive(buffer);
        }
    }

    private static nint QueryInterface(object winrtObject, Guid iid)
    {
        if (winrtObject is not IWinRTObject projected)
        {
            throw new ArgumentException("Expected a native WinRT object.", nameof(winrtObject));
        }

        nint thisPtr = projected.NativeObject.ThisPtr;
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(thisPtr, in iid, out nint result));
        return result;
    }
}
