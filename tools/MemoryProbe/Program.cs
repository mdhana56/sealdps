using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

// Riset: cek apakah GameGuard mengizinkan OpenProcess/ReadProcessMemory
// terhadap proses Seal Online. Jalankan tool ini SAAT game sedang berjalan.

const uint PROCESS_QUERY_INFORMATION = 0x0400;
const uint PROCESS_VM_READ = 0x0010;
const uint PROCESS_ALL_ACCESS = 0x001F0FFF;

string[] candidateNames = { "SO3DPlus", "SO3DPlus_x64" };

Console.WriteLine("=== Seal Online Memory Access Probe ===");

Process? target = null;
foreach (var name in candidateNames)
{
    var procs = Process.GetProcessesByName(name);
    if (procs.Length > 0)
    {
        target = procs[0];
        Console.WriteLine($"Ditemukan proses: {name} (PID {target.Id})");
        break;
    }
}

if (target is null)
{
    Console.WriteLine("Proses game tidak ditemukan. Jalankan Seal Online dulu, lalu jalankan probe ini lagi.");
    Console.WriteLine("Nama proses yang dicari: " + string.Join(", ", candidateNames));
    return;
}

TryOpen("PROCESS_QUERY_INFORMATION | PROCESS_VM_READ", PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, target.Id);
TryOpen("PROCESS_ALL_ACCESS", PROCESS_ALL_ACCESS, target.Id);

static void TryOpen(string label, uint access, int pid)
{
    IntPtr handle = OpenProcess(access, false, pid);
    if (handle == IntPtr.Zero)
    {
        int err = Marshal.GetLastWin32Error();
        Console.WriteLine($"[GAGAL] OpenProcess({label}) -> Win32Error {err} ({new System.ComponentModel.Win32Exception(err).Message})");
    }
    else
    {
        Console.WriteLine($"[OK]    OpenProcess({label}) berhasil, handle=0x{handle:X}");

        // Coba baca sedikit memory dari base address module utama untuk memastikan
        // ReadProcessMemory juga tidak diblokir (GameGuard kadang izinkan OpenProcess
        // tapi gagalkan ReadProcessMemory / mengembalikan data acak).
        try
        {
            var proc = Process.GetProcessById(pid);
            IntPtr baseAddr = proc.MainModule?.BaseAddress ?? IntPtr.Zero;
            if (baseAddr != IntPtr.Zero)
            {
                byte[] buffer = new byte[16];
                bool ok = ReadProcessMemory(handle, baseAddr, buffer, buffer.Length, out int bytesRead);
                Console.WriteLine(ok
                    ? $"        ReadProcessMemory dari base module: OK, {bytesRead} bytes dibaca -> {BitConverter.ToString(buffer)}"
                    : $"        ReadProcessMemory GAGAL, Win32Error {Marshal.GetLastWin32Error()}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"        Tidak bisa akses MainModule: {ex.Message}");
        }

        CloseHandle(handle);
    }
}

[DllImport("kernel32.dll", SetLastError = true)]
static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

[DllImport("kernel32.dll", SetLastError = true)]
static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, int dwSize, out int lpNumberOfBytesRead);

[DllImport("kernel32.dll", SetLastError = true)]
static extern bool CloseHandle(IntPtr hObject);
