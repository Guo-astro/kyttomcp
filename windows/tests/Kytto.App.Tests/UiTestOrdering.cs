using Xunit.Abstractions;
using Xunit.Sdk;

[assembly: Xunit.TestCollectionOrderer("Kytto.App.Tests.NativeUiSmokeLastOrderer", "Kytto.App.Tests")]

namespace Kytto.App.Tests;

/// <summary>
/// Runs the one test that creates and shuts down a <c>System.Windows.Application</c>
/// after every other test.
/// </summary>
/// <remarks>
/// WPF refuses to load any XAML once an <c>Application</c> in the process has shut
/// down ("The Application object is being shut down"), and it never resets. The
/// native smoke test must create and shut one down to drive the real window, so
/// every test that builds a window — Settings, the recovery panel, the real web
/// view recovery — has to run before it. Those tests use a dispatcher of their own
/// and never create an <c>Application</c>.
/// </remarks>
public sealed class NativeUiSmokeLastOrderer : ITestCollectionOrderer
{
    public IEnumerable<ITestCollection> OrderTestCollections(IEnumerable<ITestCollection> testCollections) =>
        testCollections
            .OrderBy(collection => collection.DisplayName.Contains(nameof(NativeUiSmokeTests), StringComparison.Ordinal) ? 1 : 0)
            .ThenBy(collection => collection.DisplayName, StringComparer.Ordinal);
}
