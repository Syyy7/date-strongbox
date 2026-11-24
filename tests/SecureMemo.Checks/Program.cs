using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.Win32;
using SecureMemo.Core;
using WinFormsApp1;

internal static class Program
{
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static void Expect<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }

    [STAThread]
    private static void Main()
    {
        string id = Guid.NewGuid().ToString("N");
        string root = Path.Combine(Path.GetTempPath(), "SecureMemo-check-" + id);
        string registry = @"Software\SecureMemoChecks-" + id;
        string password = " 测试🔐口令 ";
        string example = "fictional-password-7391";
        Directory.CreateDirectory(root);
        try
        {
            string file = Path.Combine(root, "vault.json");
            string backup = Path.Combine(root, "roundtrip.smbak");
            using (var store = new VaultStore(root, registry))
            {
                Expect<IOException>(() => new VaultStore(root, registry).Dispose());
                using var s = store.Create(password);
                s.Entries.Add(new Memo { Kind = "账号", Title = "演示", Password = example });
                s.Entries.Add(new Memo { Body = "第一行\n第二行" });
                store.Save(s);
                string first = File.ReadAllText(file);
                store.Save(s);
                string second = File.ReadAllText(file);
                Require(first != second && !second.Contains(example), "Ciphertext or randomization");
                store.Export(s, backup);
                Expect<AuthenticationTagMismatchException>(() => store.Open("wrong").Dispose());
                var obj = JsonNode.Parse(second)!.AsObject();
                var ct = Convert.FromBase64String((string)obj["ciphertext"]!);
                ct[0] ^= 1;
                obj["ciphertext"] = Convert.ToBase64String(ct);
                File.WriteAllText(file, obj.ToJsonString());
                Expect<InvalidDataException>(() => store.Open(password).Dispose());
                File.WriteAllText(file, second);
            }
            using (var reopened = new VaultStore(root, registry))
            using (var data = reopened.Open(password))
                Require(data.Entries[0].Password == example && data.Entries[1].Body.Contains('\n'), "Reopen");
            using (var other = new VaultStore(Path.Combine(root, "other"), registry + @"\other"))
            using (var data = other.Restore(backup, password))
                Require(data.Entries.Count == 2 && data.Entries[0].Password == example, "Restore");

            Application.EnableVisualStyles();
            System.Diagnostics.Trace.Listeners.Add(new System.Diagnostics.TextWriterTraceListener(Console.Out));
            using var form = new Form1(() => new VaultStore(Path.Combine(root, "ui"), registry + @"\ui"));
            Control.CheckForIllegalCrossThreadCalls = true;
            Exception? uiFailure = null;
            form.Shown += async (_, _) =>
            {
                try
                {
                    var all = Descendants(form).ToArray();
                    var inputs = all.OfType<TextBox>().Where(t => t.Visible && t.UseSystemPasswordChar).ToArray();
                    Require(inputs.Length == 2, "Create screen");
                    foreach (var input in inputs) input.Text = password;
                    Button Find(string caption) => all.OfType<Button>().Single(b => b.Text == caption && b.Visible);
                    Find("创建保险库").PerformClick();
                    await WaitUntil(() => all.OfType<Button>().Any(b => b.Text == "新建账号" && b.Visible));
                    Find("新建账号").PerformClick();
                    var pw = all.OfType<TextBox>().Single(t => t.Visible && t.UseSystemPasswordChar);
                    pw.Text = example;
                    Find("保存").PerformClick();
                    Require(File.Exists(Path.Combine(root, "ui", "vault.json.bak")), "UI did not commit the edited snapshot");
                    Find("锁定").PerformClick();
                    all.OfType<TextBox>().Single(t => t.Visible && t.UseSystemPasswordChar).Text = password;
                    Find("解锁").PerformClick();
                    await WaitUntil(() => pw.Visible);
                    Require(pw.Text == example && form.Text == "本地加密备忘录", "UI round trip");
                }
                catch (Exception ex) { uiFailure = ex; }
                finally { form.Close(); }
            };
            Application.Run(form);
            if (uiFailure != null) throw new Exception("UI check failed", uiFailure);
            Console.WriteLine("PASS: create, exclusive lock, save, randomized ciphertext, wrong password, tamper rejection, reopen, portable backup, GUI create/edit/save/lock/unlock.");
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(registry, false);
            Directory.Delete(root, true);
        }
    }

    private static IEnumerable<Control> Descendants(Control control) =>
        control.Controls.Cast<Control>().SelectMany(c => Descendants(c).Prepend(c));

    private static async Task WaitUntil(Func<bool> ready)
    {
        var timeout = System.Diagnostics.Stopwatch.StartNew();
        while (!ready() && timeout.Elapsed < TimeSpan.FromSeconds(15))
            await Task.Delay(20);
        Require(ready(), "UI operation timed out");
    }
}
