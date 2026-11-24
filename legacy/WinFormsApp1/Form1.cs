using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32;
using SecureMemo.Core;

namespace WinFormsApp1;

public partial class Form1 : Form
{
    private VaultStore? store;
    private Session? session;
    private Memo? current;
    private bool populating, dirty, unlocking, lockRequested;
    private string? restoreFile;
    private DateTime lastActivity = DateTime.UtcNow;
    private readonly Panel gate = new() { Dock = DockStyle.Fill };
    private readonly Panel workspace = new() { Dock = DockStyle.Fill, Visible = false };
    private readonly Label heading = new() { AutoSize = true, Font = new Font("Microsoft YaHei UI", 20, FontStyle.Bold) };
    private readonly Label message = new() { AutoSize = true, MaximumSize = new Size(570, 0) };
    private readonly TextBox pass = SecretBox(), confirm = SecretBox();
    private readonly Label confirmLabel = Caption("再次输入口令");
    private readonly Button enter = ActionButton("创建保险库"), restore = ActionButton("从加密备份恢复");
    private readonly TextBox search = new() { PlaceholderText = "搜索标题、账号或标签", Dock = DockStyle.Top };
    private readonly ListBox list = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    private readonly FlowLayoutPanel editor = Stack();
    private readonly TextBox title = Field(), user = Field(), site = Field(), tags = Field(), secret = SecretBox();
    private readonly TextBox body = new() { Multiline = true, ScrollBars = ScrollBars.Vertical, Width = 440, Height = 130 };
    private readonly FlowLayoutPanel credentials = Stack();
    private readonly Label status = new() { Dock = DockStyle.Bottom, Height = 30 };
    private readonly System.Windows.Forms.Timer saveTimer = new() { Interval = 900 };
    private readonly System.Windows.Forms.Timer idleTimer = new() { Interval = 5000 };
    private readonly System.Windows.Forms.Timer clipboardTimer = new() { Interval = 30000 };
    private uint clipboardSequence;
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();

    public Form1() : this(() => new VaultStore(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SecureMemo"),
        @"Software\SecureMemo\Vaults")) { }

    internal Form1(Func<VaultStore> openStore)
    {
        InitializeComponent();
        Text = "本地加密备忘录";
        MinimumSize = new Size(840, 610);
        ClientSize = new Size(980, 690);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Microsoft YaHei UI", 10);
        BackColor = Color.FromArgb(247, 249, 249);
        KeyPreview = true;
        BuildGate();
        BuildWorkspace();
        Controls.AddRange([workspace, gate]);
        saveTimer.Tick += (_, _) => { saveTimer.Stop(); Save(); };
        idleTimer.Tick += (_, _) => { if (DateTime.UtcNow - lastActivity >= TimeSpan.FromMinutes(5)) LockVault(); };
        clipboardTimer.Tick += (_, _) => ClearClipboard();
        KeyDown += (_, e) =>
        {
            lastActivity = DateTime.UtcNow;
            if (workspace.Visible && e.Control && e.KeyCode == Keys.S) { Save(); e.SuppressKeyPress = true; }
            if (workspace.Visible && e.Control && e.KeyCode == Keys.L) { LockVault(); e.SuppressKeyPress = true; }
        };
        foreach (var control in Descendants(workspace))
        {
            control.MouseDown += (_, _) => lastActivity = DateTime.UtcNow;
            control.MouseWheel += (_, _) => lastActivity = DateTime.UtcNow;
            control.MouseMove += (_, _) => lastActivity = DateTime.UtcNow;
        }
        Load += (_, _) =>
        {
            try
            {
                store = openStore();
                ResetGate();
                SystemEvents.SessionSwitch += OnSessionSwitch;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "无法打开保险库目录。请检查其他实例或目录权限。\n" + ex.Message, Text);
                Close();
            }
        };
        FormClosing += (_, e) => { if (unlocking || (dirty && !Save())) e.Cancel = true; };
        FormClosed += (_, _) =>
        {
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            ClearClipboard();
            saveTimer.Dispose(); idleTimer.Dispose(); clipboardTimer.Dispose();
            session?.Dispose(); store?.Dispose();
        };
    }

    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (Control sub in Descendants(child)) yield return sub;
        }
    }

    private static FlowLayoutPanel Stack() => new() { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
    private static Label Caption(string text) => new() { Text = text, AutoSize = true, Margin = new Padding(3, 8, 3, 0) };
    private static TextBox Field() => new() { Width = 440 };
    private static TextBox SecretBox() => new() { Width = 330, UseSystemPasswordChar = true };
    private static Button ActionButton(string text) => new() { Text = text, AutoSize = true, Padding = new Padding(5, 2, 5, 2) };
    private static Button ButtonFor(string name, Action action)
    {
        var button = ActionButton(name);
        button.Click += (_, _) => action();
        return button;
    }

    private void BuildGate()
    {
        var fields = Stack();
        fields.Location = new Point(60, 42);
        fields.Controls.AddRange([heading, Caption("保险库口令"), pass, confirmLabel, confirm, enter, restore, message]);
        gate.Controls.Add(fields);
        enter.Click += async (_, _) => await Authenticate();
        restore.Click += (_, _) =>
        {
            using var dialog = new OpenFileDialog { Filter = "加密备份|*.smbak|所有文件|*.*" };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            restoreFile = dialog.FileName;
            ResetGate();
        };
    }

    private void ResetGate()
    {
        workspace.Visible = false;
        gate.Visible = true;
        bool create = store?.Exists == false && restoreFile == null;
        heading.Text = create ? "创建本地保险库" : restoreFile != null ? "恢复加密备份" : "解锁备忘录";
        confirm.Visible = confirmLabel.Visible = create;
        enter.Text = create ? "创建保险库" : restoreFile != null ? "恢复并打开" : "解锁";
        restore.Visible = store?.Exists == false;
        pass.Clear(); confirm.Clear();
        message.Text = create ? "请保管好口令。它用于打开本机记录和加密备份。" :
            restoreFile != null ? "请输入制作备份时使用的口令。" : "输入口令查看本机记录。";
        AcceptButton = enter;
        pass.Select();
    }

    private async Task Authenticate()
    {
        if (unlocking || store == null) return;
        bool create = !store.Exists && restoreFile == null;
        string password = pass.Text;
        if (password.Length == 0 || (create && password != confirm.Text))
        { message.Text = "请输入口令，并检查两次输入是否一致。"; return; }
        unlocking = true;
        lockRequested = false;
        enter.Enabled = restore.Enabled = false;
        message.Text = "正在验证密钥…";
        pass.Clear(); confirm.Clear();
        try
        {
            var opened = await Task.Run(() => restoreFile != null ? store.Restore(restoreFile, password) :
                create ? store.Create(password) : store.Open(password));
            restoreFile = null;
            if (session == null) session = opened; else opened.Dispose(); // retained only after a failed save
            if (lockRequested) { LockVault(); return; }
            gate.Visible = false; workspace.Visible = true; AcceptButton = null;
            RefreshList(session.Entries.FirstOrDefault());
            lastActivity = DateTime.UtcNow;
            idleTimer.Start();
            status.Text = dirty ? "仍有待保存修改，请重试保存。" : $"已解锁 · {session.Entries.Count} 条记录";
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceError("{0}", ex.GetType().Name + Environment.NewLine + ex.StackTrace);
            if (workspace.Visible) LockVault();
            message.Text = ErrorText(ex);
        }
        finally { unlocking = false; enter.Enabled = restore.Enabled = true; }
    }

    private void BuildWorkspace()
    {
        workspace.Padding = new Padding(16);
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 45, WrapContents = false };
        bar.Controls.AddRange([ButtonFor("新建笔记", () => Add("笔记")), ButtonFor("新建账号", () => Add("账号")),
            ButtonFor("保存", () => Save()), ButtonFor("删除", Remove), ButtonFor("导出备份", Export), ButtonFor("锁定", LockVault)]);
        var split = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 32));
        split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 68));
        split.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var sidebar = new Panel { Dock = DockStyle.Fill };
        sidebar.Controls.Add(list); sidebar.Controls.Add(search);
        var detail = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        split.Controls.Add(sidebar, 0, 0);
        split.Controls.Add(detail, 1, 0);
        editor.Padding = new Padding(12, 0, 0, 0);
        var actions = new FlowLayoutPanel { AutoSize = true };
        var show = ActionButton("显示密码");
        show.Click += (_, _) => { secret.UseSystemPasswordChar = !secret.UseSystemPasswordChar; show.Text = secret.UseSystemPasswordChar ? "显示密码" : "隐藏密码"; };
        secret.TextChanged += (_, _) => { if (populating) { secret.UseSystemPasswordChar = true; show.Text = "显示密码"; } };
        actions.Controls.AddRange([show, ButtonFor("复制密码", CopyPassword)]);
        credentials.Controls.AddRange([Caption("用户名"), user, Caption("网站"), site, Caption("密码"), secret, actions]);
        editor.Controls.AddRange([Caption("标题"), title, Caption("标签"), tags, credentials, Caption("正文 / 备注"), body]);
        detail.Controls.Add(editor);
        workspace.Controls.Add(split); workspace.Controls.Add(bar); workspace.Controls.Add(status);
        search.TextChanged += (_, _) => { if (!populating) RefreshList(current); };
        list.SelectedIndexChanged += (_, _) => { if (!populating) ShowEntry(list.SelectedItem as Memo); };
        foreach (var field in new[] { title, user, site, tags, secret, body }) field.TextChanged += (_, _) => WriteFields();
    }

    private void RefreshList(Memo? preferred)
    {
        populating = true;
        list.BeginUpdate();
        list.Items.Clear();
        if (session != null)
        {
            var term = search.Text.Trim();
            foreach (var item in session.Entries.Where(x => new[] { x.Title, x.User, x.Tags }
                .Any(s => s.Contains(term, StringComparison.OrdinalIgnoreCase)))) list.Items.Add(item);
        }
        if (preferred != null && list.Items.Contains(preferred)) list.SelectedItem = preferred;
        else if (list.Items.Count > 0) list.SelectedIndex = 0;
        list.EndUpdate();
        populating = false;
        ShowEntry(list.SelectedItem as Memo);
    }

    private void ShowEntry(Memo? item)
    {
        populating = true;
        current = item;
        editor.Visible = item != null;
        credentials.Visible = item?.Kind == "账号";
        title.Text = item?.Title ?? ""; tags.Text = item?.Tags ?? ""; body.Text = item?.Body ?? "";
        site.Text = item?.Site ?? ""; user.Text = item?.User ?? ""; secret.Text = item?.Password ?? "";
        secret.UseSystemPasswordChar = true;
        populating = false;
    }

    private void WriteFields()
    {
        if (populating || current == null) return;
        current.Title = title.Text; current.Tags = tags.Text; current.Body = body.Text;
        current.Site = site.Text; current.User = user.Text; current.Password = secret.Text;
        if (list.SelectedIndex >= 0)
        {
            populating = true;
            list.Items[list.SelectedIndex] = current;
            populating = false;
        }
        MarkDirty();
    }

    private void MarkDirty()
    {
        dirty = true; status.Text = "等待保存…";
        saveTimer.Stop(); saveTimer.Start();
    }

    private bool Save()
    {
        if (!dirty || session == null) return true;
        try { store!.Save(session); dirty = false; status.Text = $"已保存 · {DateTime.Now:HH:mm:ss}"; return true; }
        catch (Exception ex) { status.Text = "保存失败：" + ErrorText(ex); return false; }
    }

    private void Add(string kind)
    {
        if (session == null) return;
        search.Clear();
        var entry = new Memo { Kind = kind };
        session.Entries.Insert(0, entry);
        RefreshList(entry);
        MarkDirty(); title.Focus();
    }

    private void Remove()
    {
        if (current == null || session == null || MessageBox.Show(this, $"删除「{current}」？", Text,
            MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        session.Entries.Remove(current);
        RefreshList(null); MarkDirty();
    }

    private void Export()
    {
        if (session == null || !Save()) return;
        using var picker = new SaveFileDialog { Filter = "加密备份|*.smbak", FileName = $"备忘录-{DateTime.Today:yyyyMMdd}.smbak" };
        if (picker.ShowDialog(this) != DialogResult.OK || !workspace.Visible || session == null) return;
        try { store!.Export(session, picker.FileName); status.Text = "完整加密备份已导出"; }
        catch (Exception ex) { status.Text = ErrorText(ex); }
    }

    private void CopyPassword()
    {
        if (secret.Text.Length == 0) return;
        try
        {
            var data = new DataObject();
            data.SetText(secret.Text);
            data.SetData("ExcludeClipboardContentFromMonitorProcessing", new MemoryStream([1, 0, 0, 0]));
            Clipboard.SetDataObject(data, true);
            clipboardSequence = GetClipboardSequenceNumber();
            clipboardTimer.Stop(); clipboardTimer.Start(); status.Text = "密码已复制，30 秒后清除";
        }
        catch (ExternalException) { status.Text = "剪贴板正被占用。"; }
    }

    private void ClearClipboard()
    {
        clipboardTimer.Stop();
        if (clipboardSequence == 0) return;
        try { if (GetClipboardSequenceNumber() == clipboardSequence) Clipboard.Clear(); }
        catch (ExternalException) { clipboardTimer.Start(); return; }
        clipboardSequence = 0;
    }

    private void LockVault()
    {
        if (session == null) return;
        workspace.Visible = false;
        idleTimer.Stop(); saveTimer.Stop(); ClearClipboard();
        bool saved = Save();
        if (saved) { session.Dispose(); session = null; }
        populating = true; search.Clear(); list.Items.Clear(); populating = false;
        ShowEntry(null); ResetGate();
        if (!saved) message.Text = "保存失败，修改仍在内存中。请解锁后重试。";
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason != SessionSwitchReason.SessionLock || IsDisposed || !IsHandleCreated) return;
        BeginInvoke((Action)(() => { lockRequested = true; LockVault(); }));
    }

    private static string ErrorText(Exception ex) => ex switch
    {
        AuthenticationTagMismatchException => "口令错误或密钥信息已损坏。",
        System.Text.Json.JsonException => "无法读取此版本的保险库或备份。",
        CryptographicException => "密钥或加密数据验证失败。",
        _ => ex.Message
    };
}
