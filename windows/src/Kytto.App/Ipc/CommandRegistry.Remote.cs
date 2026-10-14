namespace Kytto.App.Ipc;

/// <summary>Remote Doctor's two commands: explicit sign-in, and forgetting it (§7.3).</summary>
internal static partial class CommandRegistry
{
    /// <summary>
    /// What this shell can do for a Streamable HTTP server, reported in <c>app.info</c>.
    /// </summary>
    /// <remarks>
    /// Listed here, next to the commands that make them true, so a capability cannot be
    /// advertised without the native path behind it: <c>health.http</c> is the check
    /// itself, <c>health.oauth</c> the sign-in (§3.2).
    /// </remarks>
    private static readonly string[] RemoteCapabilities = ["health.http", "health.oauth"];

    static partial void RegisterRemoteCommands(CommandRouter router, AppModel model)
    {
        // The window is created the first time it is needed, on the UI thread the
        // router continues on; a test substitutes its own on the model.
        var windows = new Lazy<RemoteSignInController>(() => new RemoteSignInController());

        // Only a server id crosses. Native looks the server up, discovers the provider,
        // asks the person, and keeps every endpoint, code and token to itself. Declining
        // the prompts returns the unchanged state.
        router.Register<HealthCheckPayload, StateDto>("health.signInRemote", async payload =>
        {
            await model.SignInRemoteAsync(payload.ServerID, model.RemoteSignInUi ?? windows.Value);
            return model.ToStateDto();
        });

        router.Register<HealthCheckPayload, IReadOnlyDictionary<string, string>>(
            "health.forgetRemoteAuthorization",
            payload =>
            {
                model.ForgetRemoteAuthorization(payload.ServerID);
                return new Dictionary<string, string>();
            });
    }
}
