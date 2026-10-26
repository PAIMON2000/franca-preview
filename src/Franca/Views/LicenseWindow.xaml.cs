using System.IO;
using System.Text;
using System.Windows;
using Franca.Services;

namespace Franca.Views;

public partial class LicenseWindow : Window
{
    public LicenseWindow()
    {
        InitializeComponent();
        Title = $"Franca — {UiStrings.Get("splash.notices")}";
        var content = new StringBuilder();
        foreach (var path in new[] { "LICENSE", "THIRD-PARTY-NOTICES.md" }
                     .Concat(Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "LICENSES"), "*.txt").Order()))
        {
            var fullPath = Path.IsPathRooted(path) ? path : Path.Combine(AppContext.BaseDirectory, path);
            content.AppendLine($"──────── {Path.GetFileName(path)} ────────");
            content.AppendLine(File.ReadAllText(fullPath)).AppendLine();
        }
        LicenseContent.Text = content.ToString();
    }
}
