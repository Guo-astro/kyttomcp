using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Kytto.Core;

/// <summary>
/// Where a path really ends up, once every link on the way has been followed.
/// </summary>
/// <remarks>
/// <para>
/// A save dialog hands back whatever string the user chose, and on Windows several
/// strings can name one file: a symbolic link, a directory junction or mount point
/// anywhere above it, a short <c>PROGRA~1</c> spelling, a different capitalisation,
/// a <c>\\?\</c> prefix. Comparing the strings says nothing, so a refusal such as
/// "do not overwrite a live client config" would be defeated by an alias. This
/// answers the question the kernel answers: the final path of the deepest part that
/// exists, with whatever does not exist yet appended.
/// </para>
/// <para>
/// The result is for <em>comparison</em> (ordinal, ignoring case). It is never the
/// path something is written to.
/// </para>
/// </remarks>
internal static class FinalPath
{
    private const uint ShareAll = 7;
    private const uint OpenExisting = 3;
    private const uint BackupSemantics = 0x02000000;
    private const int MaximumLinkHops = 40;

    public static string Resolve(string path) => Resolve(Path.GetFullPath(path), hops: 0);

    /// <summary>Whether <paramref name="path"/> is <paramref name="protectedPath"/> or lies under it.</summary>
    public static bool IsSameOrUnder(string path, string protectedPath)
    {
        var candidate = Trimmed(Resolve(path));
        var guarded = Trimmed(Resolve(protectedPath));
        return string.Equals(candidate, guarded, StringComparison.OrdinalIgnoreCase) ||
               candidate.StartsWith(guarded + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether the path names a share served by this very computer —
    /// <c>\\localhost\C$\…</c>, <c>\\127.0.0.1\C$\…</c>, <c>\\THIS-PC\Users\…</c>.
    /// </summary>
    /// <remarks>
    /// Such a path reaches a local folder through the file-sharing stack, and the kernel
    /// answers with the share's own name rather than the drive path, so comparing the
    /// resolved strings cannot tell it from the file it aliases. A share of another
    /// computer cannot alias anything here and is left alone. Only this computer's own
    /// names and addresses are recognised: a DNS alias is not looked up, because a path
    /// check must not go to the network.
    /// </remarks>
    public static bool IsShareOnThisComputer(string path)
    {
        var resolved = Resolve(path);
        foreach (var prefix in (string[])[@"\\?\UNC\", @"\\.\UNC\"])
        {
            if (resolved.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                resolved = @"\\" + resolved[prefix.Length..];
                break;
            }
        }
        if (!resolved.StartsWith(@"\\", StringComparison.Ordinal)) return false;

        var rest = resolved[2..];
        var end = rest.IndexOfAny(['\\', '/']);
        var host = (end < 0 ? rest : rest[..end]).Trim('[', ']');
        return host.Length > 0 && IsThisComputer(host);
    }

    private static bool IsThisComputer(string host)
    {
        // `fe80--1.ipv6-literal.net` is how a UNC path spells an IPv6 address.
        if (host.EndsWith(".ipv6-literal.net", StringComparison.OrdinalIgnoreCase))
        {
            host = host[..^".ipv6-literal.net".Length].Replace('-', ':').Replace('s', '%');
        }
        if (host == "." || host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        if (System.Net.IPAddress.TryParse(host, out var address))
        {
            if (System.Net.IPAddress.IsLoopback(address)) return true;
            try
            {
                return System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                    .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses)
                    .Any(unicast => unicast.Address.Equals(address));
            }
            catch (Exception error) when (error is System.Net.NetworkInformation.NetworkInformationException)
            {
                return false;
            }
        }

        var names = new List<string> { Environment.MachineName };
        try
        {
            names.Add(System.Net.Dns.GetHostName());
            var properties = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties();
            if (properties.DomainName.Length > 0) names.Add($"{properties.HostName}.{properties.DomainName}");
        }
        catch (Exception error) when (
            error is System.Net.Sockets.SocketException or System.Net.NetworkInformation.NetworkInformationException)
        {
            // The machine name above still counts.
        }
        return names.Any(name => name.Equals(host, StringComparison.OrdinalIgnoreCase));
    }

    private static string Trimmed(string path) =>
        path.Length > 3 ? path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : path;

    private static string Resolve(string full, int hops)
    {
        if (hops > MaximumLinkHops) return full;

        // The deepest component that exists — or is a link, even a dangling one.
        var remainder = new Stack<string>();
        var current = full;
        while (!Exists(current))
        {
            var parent = Path.GetDirectoryName(current);
            if (parent is null) return full;
            remainder.Push(Path.GetFileName(current));
            current = parent;
        }

        var resolved = LinkTarget(current) is { } target
            // A link the kernel could not open (its target is missing): follow it by hand.
            ? Resolve(target, hops + 1)
            : FinalOf(current) ?? current;

        foreach (var name in remainder) resolved = Path.Combine(resolved, name);
        return resolved;
    }

    private static bool Exists(string path)
    {
        try
        {
            _ = File.GetAttributes(path);
            return true;
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
        catch (Exception error) when (
            error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // It is there, it just cannot be inspected; treating it as absent would
            // let an alias slip through.
            return true;
        }
    }

    /// <summary>The target of a link that points at nothing, or null.</summary>
    private static string? LinkTarget(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.LinkTarget is null) return null;
            // Only a dangling link needs this: an intact one is answered, in full, by
            // the handle below.
            if (info.Exists || Directory.Exists(path)) return null;
            return info.ResolveLinkTarget(returnFinalTarget: true)?.FullName;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static string? FinalOf(string existing)
    {
        try
        {
            using var handle = CreateFileW(existing, 0, ShareAll, IntPtr.Zero, OpenExisting, BackupSemantics, IntPtr.Zero);
            if (handle.IsInvalid) return null;

            var buffer = new StringBuilder(1024);
            var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
            if (length > buffer.Capacity)
            {
                buffer = new StringBuilder((int)length + 1);
                length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
            }
            if (length == 0) return null;

            var final = buffer.ToString();
            if (final.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)) return @"\\" + final[8..];
            return final.StartsWith(@"\\?\", StringComparison.Ordinal) ? final[4..] : final;
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(
        SafeFileHandle file,
        StringBuilder filePath,
        uint filePathLength,
        uint flags);
}
