using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Kytto.Core.Clients;
using Kytto.Core.Portable;
using Microsoft.Win32;

namespace Kytto.App;

/// <summary>
/// The native dialogs portable setup uses: a file chooser, a confirmation, and a
/// window of masked fields (§7.13).
/// </summary>
/// <remarks>
/// Native on purpose and not a web sheet. Everything typed here is a secret, a URL or a
/// private path, and the page is not allowed to see any of them: they go from these
/// controls to the service's plan and nowhere else.
/// </remarks>
internal sealed class WpfPortableSetupPrompts : IPortableSetupPrompts
{
    private const string JsonFilter = "Kytto setup (*.json)|*.json|All files (*.*)|*.*";

    private static Window? Owner =>
        Application.Current?.Windows.OfType<Window>().FirstOrDefault(window => window.IsActive)
        ?? Application.Current?.MainWindow;

    public bool? ConfirmExport()
    {
        var window = new PortableExportWindow(Owner);
        window.ShowDialog();
        return window.Accepted ? window.IncludeReviewedContracts : null;
    }

    public string? ChooseExportFile()
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export portable setup",
            Filter = JsonFilter,
            FileName = "kytto-setup.json",
            DefaultExt = ".json",
            AddExtension = true,
            OverwritePrompt = true,
        };
        return Owner is { } owner ? dialog.ShowDialog(owner) == true ? dialog.FileName : null
            : dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? ChooseImportFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import portable setup",
            Filter = JsonFilter,
            CheckFileExists = true,
            Multiselect = false,
        };
        return Owner is { } owner ? dialog.ShowDialog(owner) == true ? dialog.FileName : null
            : dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public bool CollectInputs(
        PortableSetupSummary summary,
        IReadOnlyList<(ClientId Id, string DisplayName)> targets,
        Func<PortableImportSelection, string?> submit)
    {
        var window = new PortableImportWindow(Owner, summary, targets, submit);
        window.ShowDialog();
        return window.Accepted;
    }
}

/// <summary>Says what an export contains and leaves out, and asks about fingerprints.</summary>
internal sealed class PortableExportWindow : Window
{
    private readonly CheckBox _contracts = new()
    {
        Content = "Include reviewed contract fingerprints",
        Margin = new Thickness(0, 14, 0, 0),
        IsChecked = false,
    };

    internal PortableExportWindow(Window? owner)
    {
        Title = "Export portable setup";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
        if (owner is not null) Owner = owner;

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 0),
        };
        var choose = new Button { Content = "Choose file…", IsDefault = true, MinWidth = 96, Margin = new Thickness(0, 0, 8, 0) };
        choose.Click += (_, _) =>
        {
            Accepted = true;
            Close();
        };
        buttons.Children.Add(choose);
        buttons.Children.Add(new Button { Content = "Cancel", IsCancel = true, MinWidth = 76 });

        _contracts.SetValue(
            AutomationProperties.HelpTextProperty,
            "Fingerprints let the destination compare its own check with yours. They carry no tool text, and they stay pending there until reviewed.");

        Content = new StackPanel
        {
            Margin = new Thickness(22),
            Children =
            {
                new TextBlock
                {
                    Text = "Exports writable server definitions and profiles. Credentials, URLs, private paths " +
                           "and unknown arguments become inputs the destination asks for; installed plugins " +
                           "and extensions are not copied. Nothing is read from the credential store.",
                    TextWrapping = TextWrapping.Wrap,
                },
                _contracts,
                buttons,
            },
        };
    }

    internal bool IncludeReviewedContracts => _contracts.IsChecked == true;

    /// <summary>
    /// Whether "Choose file…" was pressed. Not <c>DialogResult</c>: that exists only
    /// while a window is shown modally, and the answer should not depend on it.
    /// </summary>
    internal bool Accepted { get; private set; }
}

/// <summary>
/// Destinations and the inputs the file could not carry. Secrets, URLs and plain
/// values are masked; only paths are shown, because only a path is chosen by browsing.
/// </summary>
internal sealed class PortableImportWindow : Window
{
    private readonly Func<PortableImportSelection, string?> _submit;
    private readonly List<(ClientId Id, CheckBox Box)> _targets = [];
    private readonly List<(PortableRequirement Requirement, Control Field)> _fields = [];
    private readonly TextBlock _error = new()
    {
        Foreground = System.Windows.Media.Brushes.Firebrick,
        TextWrapping = TextWrapping.Wrap,
        Visibility = Visibility.Collapsed,
        Margin = new Thickness(0, 10, 0, 0),
    };

    internal PortableImportWindow(
        Window? owner,
        PortableSetupSummary summary,
        IReadOnlyList<(ClientId Id, string DisplayName)> targets,
        Func<PortableImportSelection, string?> submit)
    {
        _submit = submit;
        Title = "Prepare portable import";
        Width = 580;
        MinWidth = 480;
        MinHeight = 320;
        SizeToContent = SizeToContent.Height;
        MaxHeight = Math.Max(420, SystemParameters.WorkArea.Height * 0.9);
        ResizeMode = ResizeMode.CanResize;
        ShowInTaskbar = false;
        WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
        if (owner is not null) Owner = owner;

        var form = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
        if (summary.ServerNames.Count > 0)
        {
            form.Children.Add(Heading("Import disabled servers into:"));
            foreach (var (id, name) in targets)
            {
                var box = new CheckBox { Content = name, Margin = new Thickness(0, 2, 0, 2), IsChecked = _targets.Count == 0 };
                _targets.Add((id, box));
                form.Children.Add(box);
            }
        }

        foreach (var requirement in summary.Requirements)
        {
            var label = new TextBlock
            {
                Text = requirement.Label,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 14, 0, 4),
                FontSize = 11,
            };
            form.Children.Add(label);
            Control field;
            if (requirement.IsSecret)
            {
                field = new PasswordBox { MinHeight = 24 };
                form.Children.Add(field);
            }
            else
            {
                var text = new TextBox { MinHeight = 24, VerticalContentAlignment = VerticalAlignment.Center };
                field = text;
                form.Children.Add(PathRow(text));
            }
            AutomationProperties.SetName(field, requirement.Label);
            AutomationProperties.SetHelpText(
                field,
                requirement.IsSecret ? "Required value, kept native" : "Path on this computer");
            _fields.Add((requirement, field));
        }
        form.Children.Add(_error);

        var preview = new Button { Content = "Preview", IsDefault = true, MinWidth = 84, Margin = new Thickness(0, 0, 8, 0) };
        preview.Click += OnPreview;
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0),
            Children = { preview, new Button { Content = "Cancel", IsCancel = true, MinWidth = 76 } },
        };

        var intro = new TextBlock
        {
            Text = string.Create(
                CultureInfo.CurrentCulture,
                $"{summary.ServerNames.Count} servers · {summary.ProfileNames.Count} profiles. ") +
                "Choose destinations and fill in what this setup could not carry; inputs for a copy that goes " +
                "to none of them can stay empty. Each label says where the value is used. Nothing is written " +
                "yet — the next screen previews the import. Imported servers start switched off, and existing " +
                "names are never overwritten.",
            TextWrapping = TextWrapping.Wrap,
        };

        var layout = new Grid { Margin = new Thickness(22) };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.Children.Add(intro);
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = form,
            Margin = new Thickness(0, 12, 0, 0),
        };
        Grid.SetRow(scroll, 1);
        layout.Children.Add(scroll);
        Grid.SetRow(buttons, 2);
        layout.Children.Add(buttons);
        Content = layout;
    }

    /// <summary>Whether Preview produced a plan; false when the window was cancelled.</summary>
    internal bool Accepted { get; private set; }

    private static TextBlock Heading(string text) => new()
    {
        Text = text,
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(0, 14, 0, 4),
    };

    /// <summary>A path box with the two ways to fill it by browsing.</summary>
    private static Grid PathRow(TextBox box)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var file = new Button { Content = "File…", Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(10, 2, 10, 2) };
        file.Click += (_, _) =>
        {
            var dialog = new OpenFileDialog { CheckFileExists = false, Title = "Choose the file on this computer" };
            if (dialog.ShowDialog() == true) box.Text = dialog.FileName;
        };
        var folder = new Button { Content = "Folder…", Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(10, 2, 10, 2) };
        folder.Click += (_, _) =>
        {
            var dialog = new OpenFolderDialog { Title = "Choose the folder on this computer" };
            if (dialog.ShowDialog() == true) box.Text = dialog.FolderName;
        };
        Grid.SetColumn(file, 1);
        Grid.SetColumn(folder, 2);
        row.Children.Add(box);
        row.Children.Add(file);
        row.Children.Add(folder);
        return row;
    }

    private void OnPreview(object sender, RoutedEventArgs args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (requirement, field) in _fields)
        {
            // Paths are trimmed — a stray space from a paste is not part of one — and
            // everything else is taken exactly as typed.
            values[requirement.Id] = field switch
            {
                PasswordBox secret => secret.Password,
                TextBox path => path.Text.Trim(),
                _ => "",
            };
        }
        var selection = new PortableImportSelection(
            _targets.Where(target => target.Box.IsChecked == true).Select(target => target.Id).ToArray(),
            values);

        // A refusal leaves the window, and every field in it, exactly as it was.
        var error = _submit(selection);
        if (error is null)
        {
            Accepted = true;
            Close();
            return;
        }
        _error.Text = error;
        _error.Visibility = Visibility.Visible;
        _error.BringIntoView();
    }

    protected override void OnClosed(EventArgs args)
    {
        // Typed values should not outlive the window that collected them.
        foreach (var (_, field) in _fields)
        {
            if (field is PasswordBox secret) secret.Clear();
            else if (field is TextBox path) path.Clear();
        }
        base.OnClosed(args);
    }
}
