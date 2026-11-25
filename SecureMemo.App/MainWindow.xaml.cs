using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.Win32;
using Microsoft.Windows.Storage.Pickers;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using SecureMemo.Core;

namespace SecureMemo.App;

public sealed class RecordRow : INotifyPropertyChanged
{
    internal Memo Entry { get; }
    internal RecordRow(Memo entry) => Entry = entry;
    public string Name => Entry.ToString();
    public string Kind => Entry.Kind;
    private RecordAppearance? appearance;
    public RecordAppearance Appearance => appearance ??= RecordAppearance.From(Entry);
    internal void UpdateAppearance(RecordAppearance value)
    {
        appearance = value;
        PropertyChanged?.Invoke(this, new(nameof(Appearance)));
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    internal void Rename() => PropertyChanged?.Invoke(this, new(nameof(Name)));
}

public sealed partial class MainWindow : Window
{
    private VaultStore? store;
    private readonly Func<VaultStore> openStore;
    private Session? session;
    private RecordRow? selected;
    private AppearanceMetrics Metrics => (AppearanceMetrics)Application.Current.Resources["Appearance"];
    private bool binding, dirty, unlocking, lockedDuringAuth, narrowDetail;
    private DateTime activity = DateTime.UtcNow;
    private uint clipboardSequence;
    private ContentDialog? dialog;
    private readonly DispatcherTimer saveTimer = new() { Interval = TimeSpan.FromMilliseconds(900) };
    private readonly DispatcherTimer idleTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly DispatcherTimer clipboardTimer = new() { Interval = TimeSpan.FromSeconds(30) };

    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    public MainWindow() : this(() => new VaultStore(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SecureMemo"),
        @"Software\SecureMemo\Vaults")) { }

    internal MainWindow(Func<VaultStore> openStore)
    {
        this.openStore = openStore;
        InitializeComponent();
        Pass.Editor.KeyDown += GateKey;
        Confirm.Editor.KeyDown += GateKey;
        Secret.Editor.PasswordChanged += PasswordEdited;
        Activated += (_, e) =>
        {
            if (e.WindowActivationState == WindowActivationState.Deactivated) HoldPasswordField.HideActive();
        };
        GateError.RegisterPropertyChangedCallback(TextBlock.TextProperty, (_, _) =>
            GateError.Visibility = Show(!string.IsNullOrEmpty(GateError.Text)));
        Root.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(UpdateGlassLight), true);
        Root.ActualThemeChanged += (_, _) => UpdateCaptionTheme();
        Root.Loaded += (_, _) => UpdateCaptionTheme();
        Settings.ThemeChanged += theme => { Active(); Root.RequestedTheme = theme; };
        Settings.SizesChanged += (font, scale) =>
        {
            Metrics.Set(font, scale);
            Adapt();
        };
        Settings.ExportRequested += () => RunSetting(ExportBackup);
        Settings.ImportRequested += () => RunSetting(ImportBackup);
        Settings.PasswordRequested += () => RunSetting(ChangePassword);
        Settings.ReturnRequested += CloseSettings;
        Root.RequestedTheme = Settings.LoadPreference();
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1080, 760));
        saveTimer.Tick += (_, _) => { saveTimer.Stop(); Save(); };
        idleTimer.Tick += (_, _) => { if (DateTime.UtcNow - activity >= TimeSpan.FromMinutes(5)) Lock(); };
        clipboardTimer.Tick += (_, _) => ClearClipboard();
        Root.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler((_, _) => Active()), true);
        PointerEventHandler touch = (_, _) => Active();
        Root.AddHandler(UIElement.PointerPressedEvent, touch, true);
        Root.AddHandler(UIElement.PointerMovedEvent, touch, true);
        Root.AddHandler(UIElement.PointerWheelChangedEvent, touch, true);
        AppWindow.Closing += (_, e) => e.Cancel = unlocking || !Save();
        Closed += (_, _) =>
        {
            SystemEvents.SessionSwitch -= SessionChanged;
            saveTimer.Stop(); idleTimer.Stop(); ClearClipboard(); clipboardTimer.Stop();
            session?.Dispose(); session = null; store?.Dispose();
        };
        ConnectStore();
    }

    private void ConnectStore()
    {
        try
        {
            store = openStore();
            ResetGate();
            SystemEvents.SessionSwitch += SessionChanged;
        }
        catch (Exception ex)
        {
            Pass.Visibility = Confirm.Visibility = Visibility.Collapsed;
            Enter.Content = "重试";
            GateTitle.Text = "无法打开保险库";
            GateHint.Text = "请关闭其他备忘录窗口，并确认当前用户可以访问保险库目录。";
            GateError.Text = ErrorText(ex);
        }
    }

    private static Visibility Show(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;
    private readonly Windows.UI.ViewManagement.UISettings visualSettings = new();
    private void UpdateGlassLight(object sender, PointerRoutedEventArgs e)
    {
        if (!visualSettings.AnimationsEnabled || !visualSettings.AdvancedEffectsEnabled ||
            Glow.Fill is not RadialGradientBrush light || Root.ActualWidth < 1 || Root.ActualHeight < 1) return;
        var p = e.GetCurrentPoint(Root).Position;
        var center = new Windows.Foundation.Point(.20 + .10 * p.X / Root.ActualWidth,
                                                  .15 + .10 * p.Y / Root.ActualHeight);
        light.Center = light.GradientOrigin = center;
    }
    private void UpdateCaptionTheme()
    {
        const int immersiveDarkMode = 20;
        int dark = Root.ActualTheme == ElementTheme.Dark ? 1 : 0;
        DwmSetWindowAttribute(WinRT.Interop.WindowNative.GetWindowHandle(this), immersiveDarkMode, ref dark, sizeof(int));
    }
    private void OpenSettings(object sender, RoutedEventArgs e)
    {
        if (unlocking) return;
        HoldPasswordField.HideActive();
        Settings.Configure(InWorkspace && session != null, Root.ActualWidth);
        MainContent.Visibility = Visibility.Collapsed;
        Settings.Visibility = Visibility.Visible;
        Settings.FocusBack();
    }
    private void CloseSettings()
    {
        Settings.Visibility = Visibility.Collapsed;
        MainContent.Visibility = Visibility.Visible;
        (InWorkspace ? WorkspaceSettings : GateSettings).Focus(FocusState.Programmatic);
    }
    private bool InWorkspace => Workspace.Visibility == Visibility.Visible;
    private void Active() => activity = DateTime.UtcNow;
    private void ResetGate()
    {
        Settings.Visibility = Visibility.Collapsed;
        MainContent.Visibility = Visibility.Visible;
        Workspace.Visibility = Visibility.Collapsed;
        Gate.Visibility = Visibility.Visible;
        bool create = !store!.Exists;
        GateTitle.Text = create ? "创建本地保险库" : "解锁保险库";
        GateHint.Text = create ? "为本机记录设置密码。已有备份可在建库后从设置导入。" :
            "输入密码，查看你的账号与笔记。";
        Enter.Content = create ? "创建保险库" : "解锁";
        Confirm.Visibility = Show(create);
        Pass.Visibility = Visibility.Visible;
        Pass.Password = Confirm.Password = GateError.Text = "";
        Pass.Editor.Focus(FocusState.Programmatic);
    }

    private async void GateKey(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter) { e.Handled = true; await Authenticate(); }
    }
    private async void EnterClick(object sender, RoutedEventArgs e)
    {
        if (store == null) { ConnectStore(); return; }
        await Authenticate();
    }

    private async Task Authenticate()
    {
        if (unlocking || store == null || InWorkspace) return;
        bool create = !store.Exists;
        string password = Pass.Password;
        if (password.Length == 0 || (create && password != Confirm.Password))
        { GateError.Text = "请输入密码，并检查两次输入是否一致。"; return; }
        unlocking = true; lockedDuringAuth = false;
        GateSettings.IsEnabled = Enter.IsEnabled = Pass.IsEnabled = Confirm.IsEnabled = false;
        Pass.Password = Confirm.Password = GateError.Text = "";
        Progress.Visibility = Visibility.Visible;
        try
        {
            var opened = await Task.Run(() => create ? store.Create(password) : store.Open(password));
            if (session == null) session = opened; else opened.Dispose();
            if (lockedDuringAuth) { Lock(); return; }
            Gate.Visibility = Visibility.Collapsed;
            Workspace.Visibility = Visibility.Visible;
            narrowDetail = false;
            RefreshList(session.Entries.FirstOrDefault());
            Active(); idleTimer.Start();
            Status.Text = dirty ? "仍有待保存修改，请重试。" : "已解锁";
        }
        catch (Exception ex) { if (InWorkspace) Lock(); GateError.Text = ErrorText(ex); }
        finally
        {
            unlocking = false;
            GateSettings.IsEnabled = Enter.IsEnabled = Pass.IsEnabled = Confirm.IsEnabled = true;
            Progress.Visibility = Visibility.Collapsed;
            if (!InWorkspace) Pass.Editor.Focus(FocusState.Programmatic);
        }
    }

    private void RefreshList(Memo? preferred)
    {
        if (binding || session == null || !InWorkspace) return;
        binding = true;
        var term = Search.Text.Trim();
        var rows = session.Entries.Where(x => x.Title.Contains(term, StringComparison.OrdinalIgnoreCase)
            || x.User.Contains(term, StringComparison.OrdinalIgnoreCase) || x.Tags.Contains(term, StringComparison.OrdinalIgnoreCase))
            .Select(x => new RecordRow(x)).ToList();
        Records.ItemsSource = rows;
        var row = rows.FirstOrDefault(r => r.Entry == preferred) ?? rows.FirstOrDefault();
        Records.SelectedItem = row;
        Count.Text = term.Length == 0 ? $"全部记录 · {rows.Count}" : $"搜索结果 · {rows.Count}";
        ListEmpty.Text = term.Length == 0 ? "暂无记录" : "没有匹配的记录";
        ListEmpty.Visibility = Show(rows.Count == 0);
        EmptyText.Text = term.Length == 0 ? "创建第一条记录" : "没有匹配的记录";
        binding = false;
        Display(row);
    }
    private void Display(RecordRow? row)
    {
        binding = true; selected = row;
        HoldPasswordField.HideActive();
        EditorBadge.Appearance = row?.Appearance;
        var m = row?.Entry;
        Editor.Visibility = SaveButton.Visibility = Show(m != null);
        Empty.Visibility = Show(m == null);
        AccountCard.Visibility = Show(m?.Kind == "账号");
        EditorKind.Text = m?.Kind ?? ""; Body.Header = m?.Kind == "账号" ? "备注" : "正文";
        RecordTitle.Text = m?.Title ?? ""; Username.Text = m?.User ?? ""; Site.Text = m?.Site ?? "";
        Secret.Password = m?.Password ?? ""; Tags.Text = m?.Tags ?? ""; Body.Text = m?.Body ?? "";
        binding = false;
        Adapt();
    }
    private void Edited()
    {
        if (binding || selected == null || !InWorkspace) return;
        var m = selected.Entry;
        if (m.Title != RecordTitle.Text) { m.Title = RecordTitle.Text; selected.Rename(); }
        m.User = Username.Text; m.Site = Site.Text; m.Password = Secret.Password;
        m.Tags = Tags.Text; m.Body = Body.Text;
        MarkDirty();
    }
    private void MarkDirty()
    {
        dirty = true; Active(); Status.Text = "等待保存…";
        saveTimer.Stop(); saveTimer.Start();
    }
    private void FieldChanged(object sender, TextChangedEventArgs e)
    {
        // WinUI can deliver a queued TextChanged after selection changed.
        if (selected is not { Entry: var m } || binding) return;
        if (m.Title != RecordTitle.Text || m.User != Username.Text || m.Site != Site.Text ||
            m.Tags != Tags.Text || m.Body != Body.Text) Edited();
    }
    private void PasswordEdited(object sender, RoutedEventArgs e)
    { if (selected != null && selected.Entry.Password != Secret.Password) Edited(); }
    private void SearchChanged(object sender, TextChangedEventArgs e) => RefreshList(selected?.Entry);
    private void SelectionChanged(object sender, SelectionChangedEventArgs e)
    { if (!binding) Display(Records.SelectedItem as RecordRow); }
    private void RecordClick(object sender, ItemClickEventArgs e) { narrowDetail = true; Adapt(); }
    private void BackClick(object sender, RoutedEventArgs e) { HoldPasswordField.HideActive(); narrowDetail = false; Adapt(); }
    private void LayoutChanged(object sender, SizeChangedEventArgs e) => Adapt();
    private void Adapt()
    {
        if (Rail == null) return;
        double railWidth = Math.Clamp(275 * Metrics.Scale / 100d + (Metrics.Body - AppearanceMetrics.DefaultFontSize) * 3, 230, 325);
        bool narrow = Root.ActualWidth < railWidth + Math.Max(440, 270 + Metrics.Body * 12);
        bool stackActions = narrow && Root.ActualWidth < Math.Max(400, 250 + Metrics.Body * 10);
        WorkspaceEmblem.Visibility = Show(Root.ActualWidth >= 240 + 5 * Metrics.Subtitle + Metrics.BadgeSide);
        Grid.SetRow(PasswordActions, stackActions ? 1 : 0);
        Grid.SetColumn(PasswordActions, stackActions ? 0 : 1);
        Grid.SetColumnSpan(Secret, stackActions ? 2 : 1);
        Grid.SetColumnSpan(PasswordActions, stackActions ? 2 : 1);
        bool showRail = !narrow || !narrowDetail;
        Rail.Visibility = Show(showRail);
        Detail.Visibility = Show(!narrow || narrowDetail);
        RailColumn.Width = narrow ? new GridLength(showRail ? 1 : 0, GridUnitType.Star) : new GridLength(railWidth);
        DetailColumn.Width = new GridLength(narrow && showRail ? 0 : 1, GridUnitType.Star);
        Back.Visibility = Show(narrow);
    }
    private void Add(string kind)
    {
        if (session == null || !InWorkspace) return;
        var m = new Memo { Kind = kind };
        session.Entries.Insert(0, m);
        narrowDetail = true;
        Search.Text = "";
        RefreshList(m); MarkDirty();
        RecordTitle.Focus(FocusState.Programmatic);
    }
    private void AddAccount(object sender, RoutedEventArgs e) => Add("账号");
    private void AddNote(object sender, RoutedEventArgs e) => Add("笔记");

    private void PrepareRecordMenu(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue || args.Item is not RecordRow row)
        { args.ItemContainer.ContextFlyout = null; return; }
        var menu = new MenuFlyout();
        var edit = new MenuFlyoutItem { Text = "自定义图标", Icon = new SymbolIcon(Symbol.Edit) };
        edit.Click += async (_, _) => await EditIconAsync(row);
        var delete = new MenuFlyoutItem { Text = "删除", Icon = new SymbolIcon(Symbol.Delete) };
        delete.Click += async (_, _) => await DeleteAsync(row);
        menu.Items.Add(edit); menu.Items.Add(delete);
        args.ItemContainer.ContextFlyout = menu;
    }

    private async void EditIconClick(object sender, RoutedEventArgs e) => await EditIconAsync(selected);
    private void StyleDialog(ContentDialog target, bool deleting = false)
    {
        var styles = Application.Current.Resources;
        target.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("ms-appx:///GlassButtonColors.xaml") });
        target.Resources["ControlCornerRadius"] = new CornerRadius(13);
        target.PrimaryButtonStyle = (Style)styles[deleting ? "GlassDeleteButton" : "GlassPrimaryButton"];
        target.CloseButtonStyle = (Style)styles["GlassButton"];
    }

    private async Task EditIconAsync(RecordRow? row)
    {
        if (row == null || dialog != null || !InWorkspace || session?.Entries.Contains(row.Entry) != true) return;
        var editor = new IconEditor(AppWindow.Id, row.Entry, row.Appearance)
            { XamlRoot = Root.XamlRoot, RequestedTheme = Root.RequestedTheme };
        StyleDialog(editor);
        dialog = editor;
        try
        {
            if (await editor.ShowAsync() != ContentDialogResult.Primary || !InWorkspace || session?.Entries.Contains(row.Entry) != true) return;
            var appearance = editor.Apply(row.Entry);
            row.UpdateAppearance(appearance);
            if (selected == row) EditorBadge.Appearance = appearance;
            MarkDirty();
        }
        finally { dialog = null; }
    }

    private async void DeleteClick(object sender, RoutedEventArgs e) => await DeleteAsync(selected);
    private async Task DeleteAsync(RecordRow? row)
    {
        if (row == null || dialog != null || !InWorkspace || session?.Entries.Contains(row.Entry) != true) return;
        var item = row.Entry;
        dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = "删除这条记录？",
            RequestedTheme = Root.RequestedTheme,
            Content = item.ToString(), PrimaryButtonText = "删除", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close };
        StyleDialog(dialog, deleting: true);
        try
        {
            if (await dialog.ShowAsync() != ContentDialogResult.Primary || !InWorkspace || session == null) return;
            session.Entries.Remove(item);
            if (selected?.Entry == item) narrowDetail = false;
            RefreshList(selected?.Entry); MarkDirty();
        }
        finally { dialog = null; }
    }

    private bool Save()
    {
        if (!dirty || session == null) return true;
        try { store!.Save(session); dirty = false; Status.Text = $"已保存 · {DateTime.Now:HH:mm:ss}"; return true; }
        catch (Exception ex) { Status.Text = "保存失败：" + ErrorText(ex); return false; }
    }
    private void SaveClick(object sender, RoutedEventArgs e) => Save();
    private void SaveShortcut(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    { if (InWorkspace) { Save(); args.Handled = true; } }
    private void LockShortcut(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    { if (InWorkspace) { Lock(); args.Handled = true; } }
    private void LockClick(object sender, RoutedEventArgs e) => Lock();

    private void Lock()
    {
        HoldPasswordField.HideActive();
        if (session == null) return;
        Workspace.Visibility = Visibility.Collapsed;
        dialog?.Hide(); saveTimer.Stop(); idleTimer.Stop(); ClearClipboard();
        bool saved = Save();
        if (saved) { session.Dispose(); session = null; }
        binding = true; Search.Text = ""; Records.ItemsSource = null; binding = false;
        Display(null); Status.Text = "";
        ResetGate();
        if (!saved) GateError.Text = "保存失败，修改仍在内存中。请解锁后重试。";
    }
    private void SessionChanged(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason == SessionSwitchReason.SessionLock)
            DispatcherQueue.TryEnqueue(() => { lockedDuringAuth = true; Lock(); });
    }

    private bool IsActiveSetting(Session owner) =>
        ReferenceEquals(session, owner) && InWorkspace && Settings.Visibility == Visibility.Visible;

    private async void RunSetting(Func<Session, Task> operation)
    {
        if (session is not { } owner || !IsActiveSetting(owner) || !Settings.IsEnabled || dialog != null) return;
        Settings.IsEnabled = false;
        Settings.Report("");
        try
        {
            if (!Save()) { Settings.Report(Status.Text); return; }
            await operation(owner);
        }
        catch (Exception ex) { if (IsActiveSetting(owner)) Settings.Report(ErrorText(ex)); }
        finally { Settings.IsEnabled = true; }
    }

    private async Task ExportBackup(Session owner)
    {
        var picker = new FileSavePicker(AppWindow.Id) { SuggestedFileName = $"备忘录-{DateTime.Today:yyyyMMdd}" };
        picker.FileTypeChoices.Add("加密备份", new List<string> { ".smbak" });
        var file = await picker.PickSaveFileAsync();
        if (file == null || !IsActiveSetting(owner)) return;
        store!.Export(owner, file.Path);
        Settings.Report("完整加密备份已导出。");
    }

    private async Task ImportBackup(Session owner)
    {
        var picker = new FileOpenPicker(AppWindow.Id);
        picker.FileTypeFilter.Add(".smbak");
        var file = await picker.PickSingleFileAsync();
        if (file == null || !IsActiveSetting(owner)) return;
        var passwords = await AskPasswords(false);
        if (passwords == null || !IsActiveSetting(owner)) return;
        Settings.Report("正在验证备份…");
        var incoming = await Task.Run(() => VaultStore.ReadBackup(file.Path, passwords.Value.Current));
        if (!IsActiveSetting(owner)) return;
        int added = store!.Import(owner, incoming);
        RefreshList(selected?.Entry);
        Settings.Report($"已导入 {added} 条，跳过已有记录 {incoming.Count - added} 条。");
    }

    private async Task ChangePassword(Session owner)
    {
        var passwords = await AskPasswords(true);
        if (passwords == null || !IsActiveSetting(owner)) return;
        var id = owner.Envelope.Id;
        Settings.Report("正在验证并更新密码…");
        var replacement = await Task.Run(() => store!.PreparePasswordChange(id, passwords.Value.Current, passwords.Value.New));
        if (!IsActiveSetting(owner)) return;
        store!.CommitPasswordChange(owner, replacement);
        Settings.Report("保险库密码已更新。以前导出的备份仍使用当时的密码。");
    }

    private async Task<(string Current, string New)?> AskPasswords(bool changing)
    {
        var type = (AppearanceMetrics)Application.Current.Resources["Appearance"];
        var panel = new StackPanel { Spacing = 12 };
        HoldPasswordField Field(string name) => new() { Header = name };
        var current = Field(changing ? "当前密码" : "备份密码");
        panel.Children.Add(current);
        var next = Field("新密码");
        var repeat = Field("确认新密码");
        if (changing) { panel.Children.Add(next); panel.Children.Add(repeat); }
        var message = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = type.Body,
            Text = changing ? "" : "将新记录添加到当前库，已存在的同一条记录将被跳过。" };
        panel.Children.Add(message);
        var prompt = new ContentDialog { Title = changing ? "修改保险库密码" : "导入加密备份", Content = panel,
            XamlRoot = Root.XamlRoot, RequestedTheme = Root.RequestedTheme, PrimaryButtonText = changing ? "修改密码" : "导入",
            CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary };
        StyleDialog(prompt);
        prompt.Closing += (_, _) => HoldPasswordField.HideActive();
        prompt.PrimaryButtonClick += (_, args) =>
        {
            if (current.Password.Length != 0 && (!changing || (next.Password.Length != 0 && next.Password == repeat.Password))) return;
            args.Cancel = true;
            message.Text = changing ? "请填写当前密码，并确保两次输入的新密码相同。" : "请输入备份密码。";
        };
        dialog = prompt;
        try
        {
            if (await prompt.ShowAsync() != ContentDialogResult.Primary) return null;
            return (current.Password, next.Password);
        }
        finally { current.Password = next.Password = repeat.Password = ""; dialog = null; }
    }

    private void CopyClick(object sender, RoutedEventArgs e)
    {
        bool username = ReferenceEquals(sender, CopyUsername);
        string text = username ? Username.Text : Secret.Password;
        if (text.Length == 0) { Status.Text = username ? "账号为空。" : "密码为空。"; return; }
        try
        {
            var data = new DataPackage(); data.SetText(text);
            if (!Clipboard.SetContentWithOptions(data, new ClipboardContentOptions { IsAllowedInHistory = false, IsRoamable = false }))
            { Status.Text = "剪贴板正被占用。"; return; }
            clipboardSequence = GetClipboardSequenceNumber();
            clipboardTimer.Stop(); clipboardTimer.Start();
            Status.Text = username ? "账号已复制，30 秒后清除" : "密码已复制，30 秒后清除";
        }
        catch (Exception) { Status.Text = "无法访问剪贴板。"; }
    }
    private void ClearClipboard()
    {
        clipboardTimer.Stop();
        if (clipboardSequence == 0) return;
        try { if (GetClipboardSequenceNumber() == clipboardSequence) Clipboard.Clear(); }
        catch (Exception) { clipboardTimer.Start(); return; }
        clipboardSequence = 0;
    }
    private static string ErrorText(Exception ex) => ex switch
    {
        AuthenticationTagMismatchException => "密码错误或密钥信息已损坏。",
        System.Text.Json.JsonException => "无法读取此版本的保险库或备份。",
        CryptographicException => "密钥或加密数据验证失败。",
        _ => ex.Message
    };
}
