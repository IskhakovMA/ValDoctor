using System.Windows;
using RiotFix.App.Services;

namespace RiotFix.App;

/// <summary>Technical report window: everything that is deliberately kept off the main screen.</summary>
public partial class DetailsWindow : Window
{
    public DetailsWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
    }
}
