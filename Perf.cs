using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TynoRec
{
    /// <summary>Réglages système pour que TynoRec pèse le moins possible pendant qu'on joue.</summary>
    static class Perf
    {
        [StructLayout(LayoutKind.Sequential)] struct PowerThrottling { public uint Version, ControlMask, StateMask; }
        [DllImport("kernel32.dll")] static extern bool SetProcessInformation(IntPtr h, int cls, ref PowerThrottling info, int size);
        [DllImport("kernel32.dll")] static extern bool SetProcessWorkingSetSize(IntPtr h, IntPtr min, IntPtr max);
        [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll")] static extern IntPtr GetCurrentThread();
        [DllImport("kernel32.dll")] static extern bool SetThreadPriority(IntPtr h, int p);

        /// <summary>Le thread courant passe en mode arrière-plan : priorité CPU, disque et mémoire au minimum (le jeu passe toujours avant).</summary>
        public static void BgBegin() { try { SetThreadPriority(GetCurrentThread(), 0x00010000); } catch { } }
        public static void BgEnd() { try { SetThreadPriority(GetCurrentThread(), 0x00020000); } catch { } }

        const int ProcessPowerThrottling = 4;

        static void SetEco(IntPtr h, bool eco)
        {
            try
            {
                var s = new PowerThrottling { Version = 1, ControlMask = 1, StateMask = eco ? 1u : 0u };
                SetProcessInformation(h, ProcessPowerThrottling, ref s, Marshal.SizeOf<PowerThrottling>());
            }
            catch { }
        }

        /// <summary>Mode éco pour l'interface quand la fenêtre est cachée : Windows la range sur les coeurs économes, les coeurs rapides restent au jeu.</summary>
        public static void Eco(bool on) => SetEco(GetCurrentProcess(), on);

        /// <summary>ffmpeg (capture / audio) ne doit JAMAIS être mis en éco : on désactive explicitement la limitation.</summary>
        public static void NoThrottle(Process p) { try { SetEco(p.Handle, false); } catch { } }

        /// <summary>Rend la mémoire inutilisée à Windows (appelé quand la fenêtre est cachée).</summary>
        public static void Trim()
        {
            try
            {
                GC.Collect(2, GCCollectionMode.Forced, true, true);
                SetProcessWorkingSetSize(GetCurrentProcess(), new IntPtr(-1), new IntPtr(-1));
            }
            catch { }
        }
    }
}
