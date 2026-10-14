namespace Kytto.Core;

/// <summary>
/// The one gate every multi-file mutation of Kytto's own state passes through (§6.4).
/// </summary>
/// <remarks>
/// <para>
/// A portable-setup import changes several things that must stand or fall together:
/// client configs, the park store, the profile store and the pending contract
/// references. Each store already serializes itself, which is not the same as
/// serializing the <em>sequence</em>: a profile saved in the middle of an import
/// would be overwritten by the import's rollback.
/// </para>
/// <para>
/// So the stores take this gate first and their own lock second, and the import
/// holds it for its whole run. The lock is re-entrant, so the nested calls an import
/// makes into those stores do not deadlock. <see cref="ConfigWriter"/>'s commit lock
/// is always taken <em>inside</em> it and never the other way round, which keeps the
/// order fixed.
/// </para>
/// </remarks>
public static class MutationCoordinator
{
    internal static readonly Lock Gate = new();

    public static T Run<T>(Func<T> work)
    {
        lock (Gate)
        {
            return work();
        }
    }

    public static void Run(Action work)
    {
        lock (Gate)
        {
            work();
        }
    }
}
