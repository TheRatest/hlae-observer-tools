using System;
using System.Runtime.InteropServices;

namespace HlaeObsTools.Services.Input;

/// <summary>
/// Binary input packet format matching C++ InputPacket struct (50 bytes)
/// Optimized for ultra-low latency transmission over UDP
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct InputPacket
{
    public uint Sequence;        // Packet sequence number
    public short MouseDx;        // Mouse delta X
    public short MouseDy;        // Mouse delta Y
    public sbyte MouseWheel;     // Mouse wheel delta
    public byte MouseButtons;    // Button flags (L=1, R=2, M=4, X1=8, X2=16)
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = KeyBitmapSize)]
    public byte[] KeyBitmap;     // 256-bit virtual-key bitmap (VK code -> bit)
    public ulong Timestamp;      // Microsecond timestamp

    public const int KeyBitmapSize = 32; // 256 bits (32 * 8)
    public static readonly int Size = Marshal.SizeOf<InputPacket>();

    /// <summary>
    /// Convert struct to byte array for network transmission
    /// </summary>
    public byte[] ToBytes()
    {
        KeyBitmap ??= new byte[KeyBitmapSize];

        var bytes = new byte[Size];
        var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            Marshal.StructureToPtr(this, handle.AddrOfPinnedObject(), false);
            return bytes;
        }
        finally
        {
            handle.Free();
        }
    }
}
