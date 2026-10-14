namespace Kytto.App.Ipc;

internal static partial class CommandRegistry
{
    /// <summary>
    /// Portable setup (§7.13). Four commands, none of which carries a path, a value
    /// someone typed, or a way to switch an imported server on.
    /// </summary>
    /// <remarks>
    /// The file choosers and the secure inputs are native windows opened from inside
    /// these handlers; the page learns only the counts, the names, the display strings
    /// and the opaque id of a plan that is held natively.
    /// </remarks>
    static partial void RegisterPortableCommands(CommandRouter router, AppModel model)
    {
        router.Register("setup.export", () => PortableSetupController.Export(model));

        router.Register("setup.previewImport", () => PortableSetupController.Preview(model));

        // The reply is the whole state: the import changed configs, profiles and the
        // pending references at once, and the page redraws from one payload.
        router.Register<PortableApplyPayload, StateDto>("setup.applyImport", payload =>
        {
            model.ApplyPortableSetup(payload.PreviewID);
            return model.ToStateDto();
        });

        router.Register<PortableApplyPayload, Dictionary<string, string>>("setup.discardImport", payload =>
        {
            model.DiscardPortableSetup(payload.PreviewID);
            return new Dictionary<string, string>();
        });
    }
}
