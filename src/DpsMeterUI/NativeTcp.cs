using System;
using System.Collections.Generic;
using System.Net;
using System.Runtime.InteropServices;

namespace DpsMeterUI;

/// Daftar koneksi TCP (IPv4) milik 1 proses, langsung dari Windows (GetExtendedTcpTable).
/// Pengganti parsing `netstat -ano`: output netstat ikut bahasa Windows (status
/// "ESTABLISHED" bisa diterjemahkan) dan spawn proses tiap beberapa detik itu berat.
static class NativeTcp
{
    public record Connection(IPAddress LocalIp, int LocalPort, IPAddress RemoteIp, int RemotePort);

    const int AF_INET = 2;
    const int TCP_TABLE_OWNER_PID_ALL = 5;
    const uint MIB_TCP_STATE_ESTAB = 5;

    [StructLayout(LayoutKind.Sequential)]
    struct MibTcpRowOwnerPid
    {
        public uint State;
        public uint LocalAddr;
        public uint LocalPort;
        public uint RemoteAddr;
        public uint RemotePort;
        public uint OwningPid;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int af, int tableClass, uint reserved);

    /// Koneksi ESTABLISHED milik proses pid.
    public static List<Connection> Established(int pid)
    {
        var result = new List<Connection>();
        int size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0);
        IntPtr buf = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(buf, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0) != 0) return result;
            int count = Marshal.ReadInt32(buf);
            int rowSize = Marshal.SizeOf<MibTcpRowOwnerPid>();
            for (int i = 0; i < count; i++)
            {
                var row = Marshal.PtrToStructure<MibTcpRowOwnerPid>(buf + 4 + i * rowSize);
                if (row.OwningPid != pid || row.State != MIB_TCP_STATE_ESTAB) continue;
                result.Add(new Connection(new IPAddress(row.LocalAddr), Port(row.LocalPort),
                                          new IPAddress(row.RemoteAddr), Port(row.RemotePort)));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
        return result;
    }

    // Port disimpan big-endian di 16 bit bawah.
    static int Port(uint raw) => (int)(((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF));
}
