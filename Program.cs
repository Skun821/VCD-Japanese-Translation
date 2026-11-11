using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("Viscera Cleanup Detail 日本語導入ツール")]
[assembly: AssemblyProduct("Viscera Cleanup Detail Japanese Localization")]
[assembly: AssemblyVersion("1.1.4.0")]
[assembly: AssemblyFileVersion("1.1.4.0")]

namespace VisceraJapaneseInstaller
{
    // ---------------------------------------------------------------- payload / install logic
    internal sealed class Original { public string Rel, Sha; public long Size; }
    internal sealed class PatchEntry { public string Target, Source, SourceSha, TargetSha, PatchName; public long TargetSize; public bool NewFile; }
    internal sealed class FileEntry { public string Target, Name, Sha; public long Size; }

    internal sealed class Payload
    {
        public string Version = "";
        public readonly Dictionary<string, Original> Originals = new Dictionary<string, Original>(StringComparer.OrdinalIgnoreCase);
        public readonly List<PatchEntry> Patches = new List<PatchEntry>();
        public readonly List<FileEntry> Files = new List<FileEntry>();

        public static byte[] Resource(string name)
        {
            using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
            {
                if (s == null) throw new InvalidOperationException("内部データがありません: " + name);
                var ms = new MemoryStream(); s.CopyTo(ms); return ms.ToArray();
            }
        }

        public static Payload Load()
        {
            var p = new Payload();
            string text = Encoding.UTF8.GetString(Resource("manifest.tsv"));
            foreach (string line in text.Split(new[] { '\n' }))
            {
                if (line.Length == 0) continue;
                string[] c = line.Split(new[] { '\t' });
                switch (c[0])
                {
                    case "V": p.Version = c[1]; break;
                    case "O": p.Originals[c[1]] = new Original { Rel = c[1], Sha = c[2], Size = long.Parse(c[3]) }; break;
                    case "P": p.Patches.Add(new PatchEntry { Target = c[1], Source = c[2], SourceSha = c[3], TargetSha = c[4], TargetSize = long.Parse(c[5]), PatchName = c[6], NewFile = c[7] == "1" }); break;
                    case "F": p.Files.Add(new FileEntry { Target = c[1], Name = c[2], Sha = c[3], Size = long.Parse(c[4]) }); break;
                }
            }
            return p;
        }
    }

    internal static class Core
    {
        public const string BackupDir = "VCD_JPN_Backup";
        public const string OriginalDir = "Original";
        public const string StateName = "VCD_JPN_install_state.json";

        public static string Sha(string path)
        {
            using (var f = File.OpenRead(path)) using (var h = SHA256.Create()) return Hex(h.ComputeHash(f));
        }
        public static string Sha(byte[] b) { using (var h = SHA256.Create()) return Hex(h.ComputeHash(b)); }
        static string Hex(byte[] b) { var sb = new StringBuilder(b.Length * 2); foreach (byte x in b) sb.Append(x.ToString("X2")); return sb.ToString(); }
        public static string N(string rel) { return rel.Replace('\\', Path.DirectorySeparatorChar); }
        static string TrySha(string path) { try { return File.Exists(path) ? Sha(path) : null; } catch { return null; } }

        public static string CheckGame(string game)
        {
            if (string.IsNullOrWhiteSpace(game) || !Directory.Exists(game)) return "ゲームフォルダーを選択してください。";
            if (!File.Exists(Path.Combine(game, "Binaries", "Win32", "UDK.exe")))
                return "このフォルダーには Binaries\\Win32\\UDK.exe がありません。Viscera Cleanup Detail のフォルダーを選んでください。";
            if (!Directory.Exists(Path.Combine(game, "UDKGame", "Localization", "INT")))
                return "このフォルダーは Viscera Cleanup Detail のゲーム本体ではないようです。";
            return null;
        }

        public static byte[] ApplyDelta(byte[] src, byte[] patch)
        {
            if (patch.Length < 86 || Encoding.ASCII.GetString(patch, 0, 5) != "VCDP1" || patch[5] != 0) throw new InvalidDataException("パッチ形式が不正です。");
            ulong sl = BitConverter.ToUInt64(patch, 6); ulong tl = BitConverter.ToUInt64(patch, 46);
            string ssha = BitConverter.ToString(patch, 14, 32).Replace("-", ""); string tsha = BitConverter.ToString(patch, 54, 32).Replace("-", "");
            if ((ulong)src.LongLength != sl || Sha(src) != ssha) throw new InvalidDataException("元ファイルが一致しません。");
            byte[] body;
            using (var ms = new MemoryStream(patch, 88, patch.Length - 88 - 4))   // zlib: skip 2-byte header and adler32
            using (var ds = new DeflateStream(ms, CompressionMode.Decompress))
            using (var o = new MemoryStream()) { ds.CopyTo(o); body = o.ToArray(); }
            var outp = new MemoryStream((int)tl); int i = 0;
            while (true)
            {
                byte op = body[i++];
                if (op == 0) break;
                if (op == 1)
                {
                    long off = (long)BitConverter.ToUInt64(body, i); int n = (int)BitConverter.ToUInt32(body, i + 8); i += 12;
                    if (off + n > src.LongLength) throw new InvalidDataException("パッチの参照範囲が不正です。");
                    outp.Write(src, (int)off, n);
                }
                else if (op == 2)
                {
                    int n = (int)BitConverter.ToUInt32(body, i); i += 4; outp.Write(body, i, n); i += n;
                }
                else throw new InvalidDataException("未知のパッチ命令です。");
            }
            byte[] result = outp.ToArray();
            if ((ulong)result.LongLength != tl || Sha(result) != tsha) throw new InvalidDataException("生成結果のハッシュが一致しません。");
            return result;
        }

        static void WriteAtomic(string dst, byte[] data, string sha)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dst));
            string tmp = dst + ".vcdjpn.tmp";
            File.WriteAllBytes(tmp, data);
            if (Sha(tmp) != sha) { File.Delete(tmp); throw new IOException("書き込み確認に失敗しました: " + dst); }
            if (File.Exists(dst))
            {
                File.SetAttributes(dst, FileAttributes.Normal);
                try { File.Replace(tmp, dst, null); }
                catch (IOException) { File.Delete(dst); File.Move(tmp, dst); }
                catch (PlatformNotSupportedException) { File.Delete(dst); File.Move(tmp, dst); }
            }
            else File.Move(tmp, dst);
        }

        static string FindOriginal(string game, Original o)
        {
            foreach (string c in new[] { Path.Combine(game, BackupDir, OriginalDir, N(o.Rel)), Path.Combine(game, N(o.Rel)) })
                if (File.Exists(c) && new FileInfo(c).Length == o.Size && TrySha(c) == o.Sha) return c;
            string root = Path.Combine(game, BackupDir);
            if (!Directory.Exists(root)) return null;
            string name = Path.GetFileName(N(o.Rel));
            foreach (string c in Directory.EnumerateFiles(root, name, SearchOption.AllDirectories))
                if (new FileInfo(c).Length == o.Size && TrySha(c) == o.Sha) return c;
            return null;
        }

        public static bool AnyInstalled(string game, Payload p)
        {
            if (File.Exists(Path.Combine(game, StateName))) return true;
            return p.Files.Any(f => File.Exists(Path.Combine(game, N(f.Target))));
        }

        public static bool IsCurrent(string game, Payload p)
        {
            foreach (var pe in p.Patches) if (TrySha(Path.Combine(game, N(pe.Target))) != pe.TargetSha) return false;
            foreach (var f in p.Files) if (TrySha(Path.Combine(game, N(f.Target))) != f.Sha) return false;
            return true;
        }

        public static void Install(string game, Payload p, Action<string> log)
        {
            log("[1/4] 元ファイルを確認しています…");
            var src = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var missing = new List<string>();
            foreach (var o in p.Originals.Values)
            {
                string f = FindOriginal(game, o);
                if (f != null) { src[o.Rel] = f; continue; }
                bool upToDate = p.Patches.Where(x => x.Source.Equals(o.Rel, StringComparison.OrdinalIgnoreCase))
                                         .All(x => TrySha(Path.Combine(game, N(x.Target))) == x.TargetSha);
                if (!upToDate) missing.Add(o.Rel);
            }
            if (missing.Count > 0)
                throw new InvalidOperationException("次のファイルが、元のゲームの状態でも本ツールが入れた状態でもありません。\r\n  " +
                    string.Join("\r\n  ", missing) + "\r\n\r\n他のModで変更されているか、ゲームが更新された可能性があります。\r\n" +
                    "Steamでゲームを右クリック →「プロパティ」→「インストール済みファイル」→「ゲームファイルの整合性を確認」を実行してから、もう一度導入してください。\r\nゲームファイルは変更していません。");

            log("[2/4] 元ファイルをバックアップしています…");
            string broot = Path.Combine(game, BackupDir, OriginalDir);
            foreach (string rel in src.Keys.ToList())
            {
                string dst = Path.Combine(broot, N(rel));
                if (string.Equals(Path.GetFullPath(src[rel]), Path.GetFullPath(dst), StringComparison.OrdinalIgnoreCase)) continue;
                WriteAtomic(dst, File.ReadAllBytes(src[rel]), p.Originals[rel].Sha);
                src[rel] = dst;
                log("  保存: " + rel);
            }

            log("[3/4] 日本語化ファイルを書き込んでいます…");
            var done = new List<string>();
            foreach (var pe in p.Patches)
            {
                string dst = Path.Combine(game, N(pe.Target));
                if (TrySha(dst) == pe.TargetSha) { log("  最新: " + pe.Target); done.Add(pe.Target); continue; }
                byte[] result = ApplyDelta(File.ReadAllBytes(src[pe.Source]), Payload.Resource("patches/" + pe.PatchName));
                WriteAtomic(dst, result, pe.TargetSha);
                log("  更新: " + pe.Target);
                done.Add(pe.Target);
            }
            foreach (var f in p.Files)
            {
                WriteAtomic(Path.Combine(game, N(f.Target)), Payload.Resource("jpn/" + f.Name), f.Sha);
                done.Add(f.Target);
            }
            log("  翻訳ファイル " + p.Files.Count + " 個を上書きしました。");

            log("[4/4] 導入記録を保存しています…");
            string sp = Path.Combine(game, StateName);
            if (File.Exists(sp))
            {
                string old = File.ReadAllText(sp);
                if (Regex.IsMatch(old, "\"SchemaVersion\"\\s*:\\s*1\\b"))
                {
                    Directory.CreateDirectory(Path.Combine(game, BackupDir));
                    File.Copy(sp, Path.Combine(game, BackupDir, "previous_install_state_" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json"), true);
                }
            }
            var sb = new StringBuilder();
            sb.Append("{\r\n  \"SchemaVersion\": 2,\r\n  \"Product\": \"Viscera Cleanup Detail Japanese localization\",\r\n");
            sb.Append("  \"Version\": \"" + p.Version + "\",\r\n  \"InstalledUtc\": \"" + DateTime.UtcNow.ToString("o") + "\",\r\n");
            sb.Append("  \"GameRoot\": \"" + game.Replace("\\", "\\\\") + "\",\r\n  \"BackupRoot\": \"" + broot.Replace("\\", "\\\\") + "\",\r\n");
            sb.Append("  \"LanguageActivation\": \"Launch with -language=JPN\",\r\n  \"Files\": [\r\n");
            sb.Append(string.Join(",\r\n", done.Select(d => "    \"" + d.Replace("\\", "\\\\") + "\"")));
            sb.Append("\r\n  ]\r\n}\r\n");
            File.WriteAllText(sp, sb.ToString(), new UTF8Encoding(false));
        }

        public static void Uninstall(string game, Payload p, Action<string> log)
        {
            var restore = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var missing = new List<string>();
            foreach (var pe in p.Patches)
            {
                if (pe.NewFile) continue;
                if (TrySha(Path.Combine(game, N(pe.Target))) == pe.SourceSha) continue;
                string f = FindOriginal(game, p.Originals[pe.Source]);
                if (f != null) restore[pe.Target] = f; else missing.Add(pe.Target);
            }
            if (missing.Count > 0)
                throw new InvalidOperationException("次のファイルの元データ（バックアップ）が見つかりません。\r\n  " + string.Join("\r\n  ", missing) +
                    "\r\n\r\n何も変更していません。Steamの「ゲームファイルの整合性を確認」で元に戻せます。");
            foreach (var pe in p.Patches)
            {
                string dst = Path.Combine(game, N(pe.Target));
                if (pe.NewFile) { if (File.Exists(dst)) { File.Delete(dst); log("  削除: " + pe.Target); } continue; }
                string from;
                if (restore.TryGetValue(pe.Target, out from)) { WriteAtomic(dst, File.ReadAllBytes(from), pe.SourceSha); log("  復元: " + pe.Target); }
            }
            foreach (var f in p.Files)
            {
                string dst = Path.Combine(game, N(f.Target));
                if (File.Exists(dst)) { File.Delete(dst); log("  削除: " + f.Target); }
            }
            try { Directory.Delete(Path.Combine(game, "UDKGame", "Localization", "JPN")); } catch { }
            try { File.Delete(Path.Combine(game, StateName)); } catch { }
        }
    }

    // ---------------------------------------------------------------- UI (same layout as the earlier installer)
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            Payload payload;
            try { payload = Payload.Load(); }
            catch (Exception ex) { MessageBox.Show(ex.Message, "起動エラー", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
            if (args.Length == 3 && args[0] == "--cli")
            {
                try
                {
                    if (args[1] == "install") Core.Install(args[2], payload, Console.WriteLine); else Core.Uninstall(args[2], payload, Console.WriteLine);
                    Console.WriteLine("OK");
                }
                catch (Exception ex) { Console.WriteLine("ERROR: " + ex.Message); Environment.ExitCode = 1; }
                return;
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            if (args.Length == 3 && args[0] == "--operation" && (args[1] == "install" || args[1] == "uninstall"))
            {
                if (!IsAdministrator())
                {
                    try { StartElevatedOperation(args[1], args[2]); }
                    catch (System.ComponentModel.Win32Exception ex) { if (ex.NativeErrorCode == 1223) MessageBox.Show("UAC確認がキャンセルされました。ファイルは変更されていません。", "キャンセル", MessageBoxButtons.OK, MessageBoxIcon.Information); }
                    return;
                }
                Application.Run(new OperationForm(payload, args[1], args[2]));
                return;
            }
            Application.Run(new MainForm(payload));
        }

        internal static string FindSteamGame()
        {
            var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
                {
                    var steam = key == null ? null : key.GetValue("SteamPath") as string;
                    if (!string.IsNullOrWhiteSpace(steam)) roots.Add(steam.Replace('/', '\\'));
                }
            }
            catch { }
            roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"));
            var libraries = new HashSet<string>(roots, StringComparer.OrdinalIgnoreCase);
            foreach (string root in roots.ToArray())
            {
                string vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
                if (!File.Exists(vdf)) continue;
                foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase))
                    libraries.Add(m.Groups[1].Value.Replace("\\\\", "\\"));
            }
            foreach (string library in libraries)
            {
                string manifest = Path.Combine(library, "steamapps", "appmanifest_246900.acf");
                string installDir = "Viscera";
                if (File.Exists(manifest))
                {
                    var m = Regex.Match(File.ReadAllText(manifest), "\"installdir\"\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase);
                    if (m.Success) installDir = m.Groups[1].Value;
                }
                string game = Path.Combine(library, "steamapps", "common", installDir);
                if (Core.CheckGame(game) == null) return game;
            }
            return null;
        }

        internal static bool IsAdministrator()
        {
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }

        internal static void StartElevatedOperation(string operation, string gameRoot)
        {
            var info = new ProcessStartInfo(Application.ExecutablePath)
            {
                UseShellExecute = true, Verb = "runas", WorkingDirectory = Path.GetDirectoryName(Application.ExecutablePath),
                Arguments = "--operation " + operation + " \"" + gameRoot.TrimEnd('\\') + "\""
            };
            Process.Start(info);
        }

        internal static bool GameRunning()
        {
            try { return Process.GetProcessesByName("UDK").Length > 0; } catch { return false; }
        }
    }

    internal sealed class MainForm : Form
    {
        private readonly Payload payload;
        private readonly TextBox gamePath = new TextBox { ReadOnly = true };
        private readonly Label status = new Label { AutoSize = false };
        private readonly Button install = new Button { Text = "日本語を導入" };
        private readonly Button uninstall = new Button { Text = "日本語を解除" };

        internal MainForm(Payload payload)
        {
            this.payload = payload;
            Text = "Viscera Cleanup Detail 日本語導入ツール " + payload.Version;
            ClientSize = new Size(760, 360); MinimumSize = new Size(780, 400);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Yu Gothic UI", 10F);
            FormBorderStyle = FormBorderStyle.FixedSingle; MaximizeBox = false;

            var title = new Label { Text = "Viscera Cleanup Detail — 日本語導入", AutoSize = true, Font = new Font(Font, FontStyle.Bold), Left = 24, Top = 20 };
            var sub = new Label { Text = "本編 / Santa's Rampage / Shadow Warrior 対応", AutoSize = true, Left = 26, Top = 46, ForeColor = Color.DimGray };
            var pathLabel = new Label { Text = "Steamのゲームフォルダー", AutoSize = true, Left = 24, Top = 76 };
            gamePath.SetBounds(24, 100, 570, 30);
            var detect = new Button { Text = "Steamから検出" }; detect.SetBounds(610, 98, 125, 32);
            var browse = new Button { Text = "選択…" }; browse.SetBounds(610, 136, 125, 32);
            status.SetBounds(24, 176, 710, 76); status.BorderStyle = BorderStyle.FixedSingle; status.Padding = new Padding(6);
            install.SetBounds(24, 266, 150, 40);
            uninstall.SetBounds(190, 266, 150, 40);
            var language = new Label { Text = "導入後の起動オプション: -language=JPN", AutoSize = true, Left = 360, Top = 276 };
            var copy = new Button { Text = "コピー" }; copy.SetBounds(640, 270, 95, 32);
            var note = new Label { Text = "以前の日本語化が入っていても、すべて上書きします。元ファイルは VCD_JPN_Backup\\Original に保存されます。", AutoSize = true, Left = 24, Top = 320, ForeColor = Color.DimGray };

            Controls.AddRange(new Control[] { title, sub, pathLabel, gamePath, detect, browse, status, install, uninstall, language, copy, note });
            detect.Click += (s, e) => DetectGame();
            browse.Click += (s, e) => BrowseGame();
            install.Click += (s, e) => Begin("install");
            uninstall.Click += (s, e) => Begin("uninstall");
            copy.Click += (s, e) => { Clipboard.SetText("-language=JPN"); MessageBox.Show(this, "-language=JPN をコピーしました。\r\nSteamでゲームを右クリック →「プロパティ」→「一般」→「起動オプション」に貼り付けてください。", "コピー完了", MessageBoxButtons.OK, MessageBoxIcon.Information); };
            Shown += (s, e) => DetectGame();
        }

        private void DetectGame()
        {
            string found = Program.FindSteamGame();
            if (found == null) { status.Text = "SteamのViscera Cleanup Detailが見つかりません。「選択…」でゲームフォルダーを選んでください。"; install.Enabled = uninstall.Enabled = false; return; }
            gamePath.Text = found;
            RefreshStatus();
        }

        private void BrowseGame()
        {
            using (var dialog = new FolderBrowserDialog { Description = "Viscera Cleanup Detail のゲームフォルダー（Viscera）を選んでください。" })
            {
                if (Directory.Exists(gamePath.Text)) dialog.SelectedPath = gamePath.Text;
                if (dialog.ShowDialog(this) == DialogResult.OK) { gamePath.Text = dialog.SelectedPath; RefreshStatus(); }
            }
        }

        private void RefreshStatus()
        {
            string root = gamePath.Text;
            install.Enabled = uninstall.Enabled = false;
            string err = Core.CheckGame(root);
            if (err != null) { status.Text = err; return; }
            Cursor = Cursors.WaitCursor;
            try
            {
                bool any = Core.AnyInstalled(root, payload);
                if (any && Core.IsCurrent(root, payload))
                    status.Text = "この版（" + payload.Version + "）の日本語化が導入済みです。\r\n「日本語を導入」で入れ直すこともできます。解除すると英語版に戻ります。";
                else if (any)
                    status.Text = "以前の日本語化が見つかりました。\r\n「日本語を導入」で、この版（" + payload.Version + "）にすべて上書きします。";
                else
                    status.Text = "導入できます。導入時に元ファイルのバックアップを作成し、解除で元に戻せます。";
                install.Enabled = true;
                uninstall.Enabled = any;
            }
            finally { Cursor = Cursors.Default; }
        }

        private void Begin(string operation)
        {
            if (string.IsNullOrWhiteSpace(gamePath.Text)) return;
            if (MessageBox.Show(this,
                operation == "install"
                    ? "Steamのゲームフォルダーへ日本語化を導入します。\r\n以前の日本語化ファイルはすべて上書きされます。続けますか？"
                    : "バックアップから元ファイルへ戻し、日本語化を解除します。続けますか？",
                "確認", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            try
            {
                if (Program.IsAdministrator())
                    using (var form = new OperationForm(payload, operation, gamePath.Text)) form.ShowDialog(this);
                else
                {
                    Program.StartElevatedOperation(operation, gamePath.Text);
                    Enabled = false;
                    var t = new System.Windows.Forms.Timer { Interval = 1000 };
                    t.Tick += (s2, e2) =>
                    {
                        if (Process.GetProcessesByName(Path.GetFileNameWithoutExtension(Application.ExecutablePath)).Length > 1) return;
                        t.Stop(); t.Dispose(); Enabled = true; RefreshStatus(); Activate();
                    };
                    t.Start();
                    return;
                }
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                if (ex.NativeErrorCode == 1223) MessageBox.Show(this, "管理者権限の確認がキャンセルされました。ゲームファイルは変更されていません。", "キャンセル", MessageBoxButtons.OK, MessageBoxIcon.Information);
                else MessageBox.Show(this, ex.Message, "起動エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "起動エラー", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            RefreshStatus();
        }
    }

    internal sealed class OperationForm : Form
    {
        private readonly Payload payload;
        private readonly string operation, gameRoot;
        private readonly TextBox log = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill };
        private readonly Button close = new Button { Text = "閉じる", Dock = DockStyle.Bottom, Height = 36, Enabled = false };

        internal OperationForm(Payload payload, string operation, string gameRoot)
        {
            this.payload = payload; this.operation = operation; this.gameRoot = gameRoot;
            Text = operation == "install" ? "日本語導入中" : "日本語解除中";
            Width = 720; Height = 440; StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Yu Gothic UI", 9.5F);
            Controls.Add(log); Controls.Add(close);
            close.Click += (s, e) => Close();
            Shown += (s, e) => new Thread(Run) { IsBackground = true }.Start();
        }

        private void Append(string text)
        {
            if (InvokeRequired) { BeginInvoke(new Action<string>(Append), text); return; }
            log.AppendText(text + "\r\n");
        }

        private void Run()
        {
            bool ok = false; string error = null;
            Append((operation == "install" ? "導入" : "解除") + "を実行します。\r\nゲーム: " + gameRoot + "\r\n");
            try
            {
                while (Program.GameRunning())
                {
                    var r = (DialogResult)Invoke(new Func<DialogResult>(() => MessageBox.Show(this, "ゲームが起動中です。ゲームを終了してから「再試行」を押してください。", "ゲーム起動中", MessageBoxButtons.RetryCancel, MessageBoxIcon.Warning)));
                    if (r != DialogResult.Retry) throw new OperationCanceledException("ゲームが起動中のため中止しました。ファイルは変更されていません。");
                }
                if (operation == "install") Core.Install(gameRoot, payload, Append); else Core.Uninstall(gameRoot, payload, Append);
                ok = true;
            }
            catch (Exception ex) { error = ex.Message; }
            BeginInvoke(new Action(() =>
            {
                close.Enabled = true;
                if (ok && operation == "install")
                {
                    Append("\r\n完了しました。\r\nSteamでゲームを右クリック →「プロパティ」→「一般」→「起動オプション」に -language=JPN と入力してください。");
                    MessageBox.Show(this, "日本語化の導入が完了しました。\r\n\r\nSteamの起動オプションに\r\n-language=JPN\r\nを設定してからゲームを起動してください。", "完了", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                else if (ok)
                {
                    Append("\r\n完了しました。英語版に戻りました。起動オプションの -language=JPN は削除してください。");
                    MessageBox.Show(this, "日本語化を解除しました。\r\n起動オプションの -language=JPN は削除してください。", "完了", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                else
                {
                    Append("\r\n中止しました。\r\n" + error);
                    MessageBox.Show(this, error, "中止しました", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }));
        }
    }
}
