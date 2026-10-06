using System;
using System.IO;
using System.Runtime.InteropServices;

namespace AgentManagerNotch.Services
{
    /// <summary>
    /// Edición de Microsoft Store: el mismo ejecutable, empaquetado como MSIX (ver <c>crear-msix.ps1</c> y
    /// <c>store\</c>). Empaquetado, el .exe vive en <c>C:\Program Files\WindowsApps\…</c>, donde otros programas no
    /// pueden ejecutarlo por su ruta: los hooks de Claude Code, el aviso de Codex y el inicio con Windows lo llaman por
    /// su alias (<c>%LOCALAPPDATA%\Microsoft\WindowsApps\AgentManagerNotch.exe</c>). Las actualizaciones las hace la
    /// Store, así que la autoactualización y el instalador propio no se usan.
    /// </summary>
    public static class AppIdentity
    {
        public const string Alias = "AgentManagerNotch.exe";

        /// <summary>true si corre como paquete MSIX (instalado desde la Store o registrado para probarlo).</summary>
        public static bool IsPackaged { get; } = DetectPackaged();

        /// <summary>Ruta con la que otros programas (hooks, inicio con Windows) deben lanzar este ejecutable.</summary>
        public static string SelfExe => IsPackaged
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", Alias)
            : Environment.ProcessPath ?? Alias;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetCurrentPackageFullName(ref int packageFullNameLength, char[]? packageFullName);

        private static bool DetectPackaged()
        {
            const int AppModelErrorNoPackage = 15700;
            try
            {
                int length = 0;
                return GetCurrentPackageFullName(ref length, null) != AppModelErrorNoPackage;
            }
            catch { return false; }
        }
    }
}
