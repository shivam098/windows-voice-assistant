using System.Runtime.InteropServices;

/// <summary>
/// Lets this unpackaged process use the installed Aion Instruct Preview framework package (and the Windows App Runtime
/// it needs) through the Windows dynamic dependency API (appmodel.h), so no MSIX identity or Developer Mode is needed.
/// </summary>
internal static class AionFramework
{
    private const int Arm64 = 0x10, X64 = 0x4;

    [DllImport("kernelbase.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int TryCreatePackageDependency(IntPtr user, string packageFamilyName, ulong minVersion,
        int architectures, int lifetimeKind, string? lifetimeArtifact, int options, out IntPtr dependencyId);

    [DllImport("kernelbase.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int AddPackageDependency(IntPtr dependencyId, int rank, int options, out IntPtr context,
        out IntPtr packageFullName);

    public static void Load()
    {
        Add("Microsoft.WindowsAppRuntime.2_8wekyb3d8bbwe", Version(2, 0, 1));
        Add("Microsoft.AionInstructPreview.Framework.1.0_8wekyb3d8bbwe", Version(1, 1, 0));
    }

    private static ulong Version(ulong major, ulong minor, ulong build) => (major << 48) | (minor << 32) | (build << 16);

    private static void Add(string family, ulong minVersion)
    {
        var arch = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? Arm64 : X64;
        // Lifetime kind 0 = the dependency lasts until this process exits.
        var hr = TryCreatePackageDependency(IntPtr.Zero, family, minVersion, arch, 0, null, 0, out var id);
        if (hr == 0) hr = AddPackageDependency(id, 0, 0, out _, out _);
        if (hr != 0) throw new InvalidOperationException($"{family} is not installed (0x{hr:X8}).");
    }
}
