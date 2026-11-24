using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;
using SecureMemo.Core;

namespace SecureMemo.App;

public sealed partial class IconEditor : ContentDialog
{
    private readonly WindowId owner;
    private readonly string kind;
    private bool syncing, closed;
    private byte[]? png;
    private static readonly IconChoice Imported = new("image", "自定义图片", "\uEB9F");
    private IconChoice[] Options => png == null ? RecordAppearance.Choices : [.. RecordAppearance.Choices, Imported];

    internal IconEditor(WindowId owner, Memo entry, RecordAppearance appearance)
    {
        this.owner = owner;
        kind = entry.Kind;
        InitializeComponent();
        PreviewTitle.Text = entry.ToString();
        ShowDraft(appearance, appearance.Image == null ? null : entry.IconPng);
        Closed += (_, _) => closed = true;
    }

    private void ShowDraft(RecordAppearance appearance, byte[]? image)
    {
        syncing = true;
        png = image;
        Preview.Appearance = appearance;
        ColorButton.Content = image == null ? "图标颜色" : "图片底色";
        Symbols.ItemsSource = Options;
        Symbols.SelectedItem = png == null ? appearance.Choice : Imported;
        Colors.Color = appearance.Color;
        syncing = false;
        Error.Visibility = Visibility.Collapsed;
    }

    private void SymbolChanged(object sender, SelectionChangedEventArgs e)
    {
        if (syncing || Symbols.SelectedItem is not IconChoice symbol || symbol == Imported) return;
        ShowDraft(new(symbol, Colors.Color, null), null);
    }

    private void ColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (!syncing && Preview?.Appearance is { } old)
            Preview.Appearance = new(old.Choice, args.NewColor, old.Image);
    }

    private void ResetClick(object sender, RoutedEventArgs e) =>
        ShowDraft(RecordAppearance.From(new Memo { Kind = kind }), null);

    private async void ImportClick(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        IsPrimaryButtonEnabled = button.IsEnabled = false;
        try
        {
            var picker = new FileOpenPicker(owner);
            foreach (string ext in new[] { ".png", ".jpg", ".jpeg" }) picker.FileTypeFilter.Add(ext);
            var file = await picker.PickSingleFileAsync();
            if (file == null || closed) return;
            await ImportFileAsync(file.Path);
        }
        catch (Exception ex) { if (!closed) { Error.Text = "图片导入失败：" + ex.Message; Error.Visibility = Visibility.Visible; } }
        finally { IsPrimaryButtonEnabled = button.IsEnabled = true; }
    }

    private async Task ImportFileAsync(string path)
    {
        var data = await RecordAppearance.ImportAsync(path);
        if (closed) return;
        var image = RecordAppearance.LoadImage(data) ?? throw new InvalidDataException("无法读取图标。");
        ShowDraft(new(Preview.Appearance!.Choice, Colors.Color, image), data);
    }

    internal RecordAppearance Apply(Memo entry)
    {
        var appearance = Preview.Appearance!;
        entry.IconName = appearance.Choice.Name;
        entry.IconColor = appearance.Color.R << 16 | appearance.Color.G << 8 | appearance.Color.B;
        entry.IconPng = png;
        return appearance;
    }
}
