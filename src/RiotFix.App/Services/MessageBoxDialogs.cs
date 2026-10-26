using System.Windows;
using RiotFix.App.ViewModels;

namespace RiotFix.App.Services;

internal sealed class MessageBoxDialogs(Window owner) : IDialogs
{
    public bool Confirm(string title, string message) =>
        MessageBox.Show(owner, message, title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;

    public void Info(string title, string message) =>
        MessageBox.Show(owner, message, title, MessageBoxButton.OK, MessageBoxImage.Information);
}
