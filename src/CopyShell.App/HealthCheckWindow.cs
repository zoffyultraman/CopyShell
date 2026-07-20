using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CopyShell.App;

internal sealed class HealthCheckWindow : Window
{
    public event EventHandler? InterfaceLoaded;

    public HealthCheckWindow(int stage)
    {
        AppDiagnostics.Write(
            $"Building staged health-check interface. Stage={stage}.");

        var content = new StackPanel
        {
            Padding = new Thickness(24),
            Spacing = 12
        };
        var root = new ScrollViewer
        {
            Content = content
        };
        root.Loaded += OnRootLoaded;
        Content = root;

        content.Children.Add(new TextBlock
        {
            FontSize = 24,
            FontWeight = FontWeights.SemiBold,
            Text = "CopyShell startup check"
        });

        if (stage >= 1)
        {
            var list = new ListView
            {
                MinHeight = 100,
                SelectionMode = ListViewSelectionMode.None
            };
            list.Items.Add(@"C:\CopyShell\Source");
            content.Children.Add(list);
        }

        if (stage >= 2)
        {
            content.Children.Add(new TextBox
            {
                Header = "Destination",
                Text = @"C:\CopyShell\Destination"
            });
            content.Children.Add(new Button
            {
                Content = "Browse"
            });
        }

        if (stage >= 3)
        {
            var comboBox = new ComboBox
            {
                Header = "Conflict strategy"
            };
            comboBox.Items.Add(new ComboBoxItem
            {
                Content = "Overwrite"
            });
            comboBox.Items.Add(new ComboBoxItem
            {
                Content = "Skip"
            });
            comboBox.SelectedIndex = 0;
            content.Children.Add(comboBox);
            content.Children.Add(new CheckBox
            {
                Content = "Restartable",
                IsChecked = true
            });
        }

        if (stage >= 4)
        {
            content.Children.Add(new TextBox
            {
                AcceptsReturn = true,
                IsReadOnly = true,
                Text = "CopyShell log"
            });
        }

        if (stage >= 5)
        {
            var queue = new ListView
            {
                MinHeight = 100,
                SelectionMode = ListViewSelectionMode.Single
            };
            queue.Items.Add("Pending copy task");
            content.Children.Add(queue);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8
            };
            buttons.Children.Add(new Button
            {
                Content = "Pause"
            });
            buttons.Children.Add(new Button
            {
                Content = "Resume"
            });
            content.Children.Add(buttons);
        }
    }

    private void OnRootLoaded(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement root)
        {
            root.Loaded -= OnRootLoaded;
        }

        AppDiagnostics.Write("Staged health-check interface loaded.");
        InterfaceLoaded?.Invoke(this, EventArgs.Empty);
    }
}
