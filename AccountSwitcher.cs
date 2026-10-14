using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace CodexAccountSwitcher
{
    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            if (args.Length == 2 && args[0] == "--test-worker") { Thread.Sleep(250); return 0; }
            if (args.Length == 2 && args[0] == "--self-test") return SelfTests.Run(args[1]);
            if (args.Length == 2 && args[0] == "--preview")
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainWindow(DefaultHome(), args[1]));
                return 0;
            }
            if (args.Length == 2 && args[0] == "--check")
            {
                try
                {
                    string home = DefaultHome();
                    Auth active = Store.TryRead(Path.Combine(home, "auth.json"));
                    var profiles = Store.Scan(home);
                    var report = new {
                        Home = home,
                        Profiles = profiles.Select(p => new { p.Name, Valid = p.Error == null, Error = p.Error, HasConfig = File.Exists(Path.Combine(p.Directory, "config.toml")), Current = p.Auth != null && p.Auth.SameUser(active) }).ToArray(),
                        Apps = Desktop.FindApps().Select(a => new { a.Name, a.AppId }).ToArray(),
                        DesktopActivationAvailable = Native.ActivationAvailable(),
                        ProcessTreeAvailable = Native.ProcessTree().Count > 0,
                        ProcessAccess = Desktop.InspectAccess()
                    };
                    File.WriteAllText(args[1], Json.Encode(report), new UTF8Encoding(false));
                    return 0;
                }
                catch { return 1; }
            }
            bool created;
            using (var mutex = new Mutex(true, "Local\\CodexAccountSwitcher.UI.v1", out created))
            {
                if (!created) { MessageBox.Show("账号切换器已经打开，请使用已有窗口。", "Codex 账号切换器"); return 0; }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainWindow(DefaultHome()));
            }
            return 0;
        }
        public static string DefaultHome()
        {
            string beside = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            if (Directory.Exists(Path.Combine(beside, "account"))) return beside;
            string env = Environment.GetEnvironmentVariable("CODEX_HOME");
            if (!String.IsNullOrWhiteSpace(env) && Directory.Exists(Path.Combine(env, "account"))) return Path.GetFullPath(env);
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        }
    }
    static class Json
    {
        public static string Encode(object obj) { return new JavaScriptSerializer().Serialize(obj); }
        public static Dictionary<string, object> Decode(string text)
        {
            try { return new JavaScriptSerializer().DeserializeObject(text) as Dictionary<string, object>; }
            catch { throw new InvalidDataException("JSON 格式不正确。"); }
        }
        public static string String(Dictionary<string, object> obj, string key)
        {
            object value;
            return obj != null && obj.TryGetValue(key, out value) ? value as string ?? "" : "";
        }
    }
    sealed class Auth
    {
        public byte[] Bytes;
        public string Identity, Email, Mode;
        public static Auth Parse(byte[] bytes)
        {
            if (bytes.Length == 0 || bytes.Length > 1024 * 1024) throw new InvalidDataException("登录文件为空或过大。");
            string text;
            try { text = new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF'); }
            catch { throw new InvalidDataException("登录文件必须是 UTF-8 文本。"); }
            var obj = Json.Decode(text);
            if (obj == null) throw new InvalidDataException("登录文件必须是 JSON 对象。");
            string key = Json.String(obj, "OPENAI_API_KEY");
            object value;
            Dictionary<string, object> tokens = obj.TryGetValue("tokens", out value) ? value as Dictionary<string, object> : null;
            string mode = Json.String(obj, "auth_mode");
            if (mode == "apikey" || mode == "api_key" || (tokens == null && !String.IsNullOrWhiteSpace(key)))
            {
                if (String.IsNullOrWhiteSpace(key)) throw new InvalidDataException("API key 登录文件缺少凭据。");
                return new Auth { Bytes = bytes, Mode = "API key", Identity = "key:" + Hash(Encoding.UTF8.GetBytes(key)), Email = "API key 登录" };
            }
            string access = Json.String(tokens, "access_token"), refresh = Json.String(tokens, "refresh_token"), account = Json.String(tokens, "account_id");
            if (String.IsNullOrWhiteSpace(access) || String.IsNullOrWhiteSpace(refresh) || String.IsNullOrWhiteSpace(account))
                throw new InvalidDataException("登录文件缺少 access_token、refresh_token 或 account_id。");
            string email = "", subject = "";
            try
            {
                string jwt = Json.String(tokens, "id_token");
                string[] parts = jwt.Split('.');
                if (parts.Length == 3)
                {
                    string encoded = parts[1].Replace('-', '+').Replace('_', '/');
                    encoded = encoded.PadRight((encoded.Length + 3) / 4 * 4, '=');
                    var claims = Json.Decode(Encoding.UTF8.GetString(Convert.FromBase64String(encoded)));
                    email = Json.String(claims, "email"); subject = Json.String(claims, "sub");
                }
            }
            catch { /* JWT claims are display hints only, never an authentication check. */ }
            // Include the user subject: different members of one workspace can share an account_id.
            string identity = !String.IsNullOrEmpty(subject) ? account + "|" + subject : !String.IsNullOrEmpty(email) ? account + "|" + email.ToLowerInvariant() : "token:" + Hash(Encoding.UTF8.GetBytes(access));
            return new Auth { Bytes = bytes, Identity = identity, Email = email, Mode = "ChatGPT" };
        }
        public bool SameUser(Auth other) { return other != null && Mode == other.Mode && Identity == other.Identity; }
        public static string Hash(byte[] bytes) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", ""); }
    }
    sealed class Profile
    {
        public string Name, Directory, Error;
        public Auth Auth;
    }
    static class Store
    {
        public static void Regular(string path)
        {
            string current = Path.GetFullPath(path);
            while (!String.IsNullOrEmpty(current))
            {
                if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("路径包含链接或重解析点，请使用普通文件夹。");
                current = Path.GetDirectoryName(current);
            }
        }
        public static Auth Read(string path) { Regular(path); return Auth.Parse(File.ReadAllBytes(path)); }
        public static Auth TryRead(string path) { try { return Read(path); } catch { return null; } }
        public static List<Profile> Scan(string home)
        {
            string root = Path.Combine(home, "account");
            Regular(root);
            if (!Directory.Exists(root)) throw new DirectoryNotFoundException("所选目录中没有 account 文件夹。");
            var result = new List<Profile>();
            foreach (string dir in Directory.GetDirectories(root).OrderBy(p => Path.GetFileName(p), StringComparer.CurrentCultureIgnoreCase))
            {
                if (Path.GetFileName(dir).StartsWith(".saving-", StringComparison.OrdinalIgnoreCase)) continue;
                var profile = new Profile { Name = Path.GetFileName(dir), Directory = dir };
                try { profile.Auth = Read(Path.Combine(dir, "auth.json")); }
                catch (FileNotFoundException) { profile.Error = "缺少 auth.json"; }
                catch (Exception e) { profile.Error = SafeError(e); }
                result.Add(profile);
            }
            return result;
        }
        public static string SafeError(Exception e)
        {
            if (e is OperationFailure) return e.Message;
            var native = e as System.ComponentModel.Win32Exception;
            if (native != null) return "Windows 操作失败，错误码 " + native.NativeErrorCode + "（" + (native.NativeErrorCode == 5 ? "进程访问被拒绝，请退出 ChatGPT 后重试，或以与 ChatGPT 相同的权限运行切换器" : "请重试，并查看具体操作步骤") + "）。";
            if (e is COMException) return "启动 ChatGPT 的 Windows 接口失败（0x" + e.HResult.ToString("X8") + "），请从开始菜单手动打开应用。";
            if (e is UnauthorizedAccessException) return "文件访问被拒绝，请检查权限。";
            if (e is InvalidDataException) return e.Message;
            if (e is DirectoryNotFoundException) return "文件夹不存在。";
            if (e is FileNotFoundException) return "缺少所需文件。";
            if (e is IOException) return "文件被占用、路径不安全或无法写入。";
            return "操作失败（" + e.GetType().Name + "），请重试或检查文件权限。";
        }
        public static void AtomicWrite(string path, byte[] bytes)
        {
            Regular(path);
            string temp = path + ".switch-" + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);
            }
            catch (Exception e) { throw new OperationFailure("写入文件 " + path, e); }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        public static byte[] FileCredentialConfig(byte[] bytes)
        {
            string text;
            try { text = new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF'); }
            catch { throw new InvalidDataException("config.toml 必须是 UTF-8 文本。"); }
            // Edit only the root setting, leaving all tables and other settings intact.
            var table = Regex.Match(text, @"(?m)^\s*\[");
            int boundary = table.Success ? table.Index : text.Length;
            string root = text.Substring(0, boundary), rest = text.Substring(boundary);
            var setting = new Regex(@"(?m)^[ \t]*cli_auth_credentials_store[ \t]*=[^\r\n]*");
            string newline = text.Contains("\r\n") ? "\r\n" : "\n";
            if (setting.IsMatch(root)) root = setting.Replace(root, "cli_auth_credentials_store = \"file\"");
            else root = "cli_auth_credentials_store = \"file\"" + newline + root;
            return new UTF8Encoding(false).GetBytes(root + rest);
        }
    }
    sealed class OperationFailure : Exception
    {
        public string Stage;
        public OperationFailure(string stage, Exception cause) : base(stage + "失败。\r\n" + Store.SafeError(cause), cause) { Stage = stage; }
    }
    static class Diagnostics
    {
        public static string Record(string home, string stage, Exception error)
        {
            try
            {
                string path = Path.Combine(home, "AccountSwitcher", "operation-errors.jsonl");
                Store.Regular(path); Directory.CreateDirectory(Path.GetDirectoryName(path));
                var errors = new List<object>();
                for (Exception e = error; e != null; e = e.InnerException)
                {
                    var native = e as System.ComponentModel.Win32Exception;
                    var operation = e as OperationFailure;
                    errors.Add(new { Type = e.GetType().FullName, HResult = e.HResult, NativeErrorCode = native == null ? (int?)null : native.NativeErrorCode, Stage = operation == null ? null : operation.Stage, Stack = e.StackTrace });
                }
                File.AppendAllText(path, Json.Encode(new { TimeUtc = DateTime.UtcNow.ToString("o"), Stage = stage, Errors = errors }) + Environment.NewLine, new UTF8Encoding(false));
                return path;
            }
            catch { return null; }
        }
    }
    static class AccountRecorder
    {
        public static string ValidateName(string input)
        {
            string name = (input ?? "").Trim();
            if (String.IsNullOrEmpty(name)) throw new InvalidDataException("请输入新账号名称，例如 my-account。");
            if (name.Length > 80 || name.StartsWith(".") || name.EndsWith(".") || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || Regex.IsMatch(name, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)", RegexOptions.IgnoreCase))
                throw new InvalidDataException("名称不能包含路径、特殊字符、结尾的点或 Windows 保留名称，长度不能超过 80 字。");
            return name;
        }
        public static string Save(string home, string input)
        {
            string name = ValidateName(input), root = Path.Combine(Path.GetFullPath(home), "account"), target = Path.Combine(root, name);
            Store.Regular(root); Store.Regular(target);
            if (Directory.Exists(target) || File.Exists(target)) throw new InvalidDataException("名称“" + name + "”已存在，请输入一个新名称。");
            string lockPath = Path.Combine(home, "account-switcher.lock"); Store.Regular(lockPath);
            using (var operationLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                Auth auth = null; byte[] config = null;
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    auth = Store.Read(Path.Combine(home, "auth.json"));
                    string configPath = Path.Combine(home, "config.toml"); Store.Regular(configPath);
                    config = File.Exists(configPath) ? File.ReadAllBytes(configPath) : null;
                    if (Store.Read(Path.Combine(home, "auth.json")).Bytes.SequenceEqual(auth.Bytes)) break;
                    auth = null;
                }
                if (auth == null) throw new InvalidDataException("当前登录正在刷新，请稍后再次保存。");
                Directory.CreateDirectory(root);
                string staging = Path.Combine(root, ".saving-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(staging);
                try
                {
                    Store.AtomicWrite(Path.Combine(staging, "auth.json"), auth.Bytes);
                    if (config != null) Store.AtomicWrite(Path.Combine(staging, "config.toml"), config);
                    Store.AtomicWrite(Path.Combine(staging, "profile-info.json"), Encoding.UTF8.GetBytes(Json.Encode(new { Version = 1, Name = name, Email = auth.Email, LoginMode = auth.Mode, SavedAtUtc = DateTime.UtcNow.ToString("o") })));
                    Directory.Move(staging, target);
                    return target;
                }
                catch (Exception e) { throw new OperationFailure("保存当前账号到 " + target, e); }
                finally
                {
                    if (Directory.Exists(staging))
                    {
                        foreach (string file in new[] { "auth.json", "config.toml", "profile-info.json" })
                        { string path = Path.Combine(staging, file); if (File.Exists(path)) File.Delete(path); }
                        if (!Directory.EnumerateFileSystemEntries(staging).Any()) Directory.Delete(staging);
                    }
                }
            }
        }
    }
    static class AccountRemover
    {
        public static void Delete(string home, Profile selected)
        {
            string root = Path.Combine(Path.GetFullPath(home), "account");
            string target = Path.GetFullPath(selected.Directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!String.Equals(Path.GetDirectoryName(target), root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("账号必须位于 account 的直接子文件夹中。");
            Store.Regular(target);
            string lockPath = Path.Combine(home, "account-switcher.lock"); Store.Regular(lockPath);
            using (var operationLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                CheckDirectory(target);
                Directory.Delete(target, true);
            }
        }
        static void CheckDirectory(string directory)
        {
            Store.Regular(directory);
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
            {
                Store.Regular(entry);
                if (Directory.Exists(entry)) CheckDirectory(entry);
            }
        }
    }
    sealed class SwitchResult
    {
        public string BackupDirectory, RestartError;
        public bool Restarted;
    }
    sealed class SwitchPlan
    {
        public string Home, TargetDirectory;
        public bool CopyConfig;
        public Auth TargetAuth;
        public byte[] Config;
        public static SwitchPlan Prepare(string home, Profile selected, bool copyConfig)
        {
            home = Path.GetFullPath(home).TrimEnd(Path.DirectorySeparatorChar);
            string account = Path.Combine(home, "account") + Path.DirectorySeparatorChar;
            string profile = Path.GetFullPath(selected.Directory);
            if (!profile.StartsWith(account, StringComparison.OrdinalIgnoreCase) || Path.GetDirectoryName(profile) + Path.DirectorySeparatorChar != account)
                throw new InvalidDataException("账号必须位于 account 的直接子文件夹中。");
            Store.Regular(home); Store.Regular(profile);
            var plan = new SwitchPlan { Home = home, TargetDirectory = profile, CopyConfig = copyConfig, TargetAuth = Store.Read(Path.Combine(profile, "auth.json")) };
            string configPath = Path.Combine(copyConfig ? profile : home, "config.toml");
            Store.Regular(configPath);
            if (copyConfig && !File.Exists(configPath)) throw new InvalidDataException("账号目录缺少 config.toml；取消勾选“同时切换配置”后可继续。");
            plan.Config = Store.FileCredentialConfig(File.Exists(configPath) ? File.ReadAllBytes(configPath) : new byte[0]);
            Store.Regular(Path.Combine(home, "auth.json"));
            Store.Regular(Path.Combine(home, "config.toml"));
            Store.Regular(Path.Combine(home, "account-switcher-backups"));
            return plan;
        }
        public SwitchResult Apply(Action<string> progress, Action<string> beforeWrite)
        {
            string backup = Path.Combine(Home, "account-switcher-backups", DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(backup);
            string[] names = { "auth.json", "config.toml" };
            var originals = new Dictionary<string, byte[]>();
            foreach (string name in names)
            {
                string path = Path.Combine(Home, name);
                Store.Regular(path);
                originals[name] = File.Exists(path) ? File.ReadAllBytes(path) : null;
                if (originals[name] != null) Store.AtomicWrite(Path.Combine(backup, name), originals[name]);
            }
            Store.AtomicWrite(Path.Combine(backup, "restore-info.json"), Encoding.UTF8.GetBytes(Json.Encode(new { Version = 1, AuthExisted = originals["auth.json"] != null, ConfigExisted = originals["config.toml"] != null })));
            progress("已备份当前登录，正在保存刷新的凭据…");
            Auth current = originals["auth.json"] == null ? null : Store.TryRead(Path.Combine(Home, "auth.json"));
            byte[] incoming = TargetAuth.Bytes;
            if (current != null)
            {
                // Never replace a refreshed active token with a stale copy of the same account.
                if (TargetAuth.SameUser(current)) incoming = current.Bytes;
                foreach (var profile in Store.Scan(Home).Where(p => p.Auth != null && p.Auth.SameUser(current)))
                    Store.AtomicWrite(Path.Combine(profile.Directory, "auth.json"), current.Bytes);
            }
            var attempted = new List<string>();
            try
            {
                foreach (string name in names)
                {
                    progress("正在替换 " + name + "…");
                    if (beforeWrite != null) beforeWrite(name);
                    attempted.Add(name);
                    Store.AtomicWrite(Path.Combine(Home, name), name == "auth.json" ? incoming : Config);
                }
            }
            catch
            {
                bool restored = true;
                foreach (string name in attempted.AsEnumerable().Reverse())
                {
                    try
                    {
                        string path = Path.Combine(Home, name);
                        if (originals[name] == null) { Store.Regular(path); if (File.Exists(path)) File.Delete(path); }
                        else Store.AtomicWrite(path, originals[name]);
                    }
                    catch { restored = false; }
                }
                throw new InvalidDataException(restored ? "替换失败，已恢复切换前的登录与配置。备份：" + backup : "替换失败且自动恢复未完成，请从以下目录恢复 auth.json 和 config.toml：" + backup);
            }
            return new SwitchResult { BackupDirectory = backup };
        }
    }
    sealed class AppTarget
    {
        public string Name, AppId, ExePath;
        public override string ToString() { return Name; }
    }
    interface IDesktop
    {
        void Stop(Action<string> progress);
        void Start(AppTarget target);
    }
    sealed class SwitchService
    {
        public static SwitchResult Switch(string home, Profile profile, bool copyConfig, AppTarget target, IDesktop desktop, Action<string> progress)
        {
            if (target == null) throw new InvalidDataException("没有检测到 ChatGPT，请选择已安装的 ChatGPT.exe。");
            // All validation is performed before closing the desktop application.
            var plan = SwitchPlan.Prepare(home, profile, copyConfig);
            string lockPath = Path.Combine(home, "account-switcher.lock");
            Store.Regular(lockPath);
            using (var operationLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                SwitchResult result;
                string stage = "关闭 ChatGPT";
                try
                {
                    desktop.Stop(progress);
                    stage = "备份并替换登录文件";
                    result = plan.Apply(progress, null);
                }
                catch (Exception failure)
                {
                    failure = new OperationFailure(stage, failure);
                    try { desktop.Start(target); }
                    catch { throw new InvalidDataException(Store.SafeError(failure) + "\r\nChatGPT 自动重启也未完成，请手动打开应用。"); }
                    throw failure;
                }
                progress("文件已切换，正在启动 " + target.Name + "…");
                try { desktop.Start(target); result.Restarted = true; }
                catch (Exception e) { result.RestartError = Store.SafeError(e); }
                return result;
            }
        }
    }
    sealed class Desktop : IDesktop
    {
        public static List<AppTarget> FindApps()
        {
            var apps = new List<AppTarget>();
            foreach (var process in Process.GetProcessesByName("ChatGPT"))
            {
                using (process)
                {
                    try
                    {
                        string path = process.MainModule.FileName;
                        if (!TrustedExecutable(path)) continue;
                        string id = PackageId(path);
                        if (!apps.Any(a => a.AppId == id && a.ExePath == path)) apps.Add(new AppTarget { Name = id.Contains("CodexBeta_") ? "ChatGPT (Beta) · 正在运行" : "ChatGPT · 正在运行", AppId = id, ExePath = path });
                    }
                    catch { }
                }
            }
            try
            {
                var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"));
                start.Arguments = "-NoProfile -NonInteractive -Command \"[Console]::OutputEncoding = [Text.Encoding]::UTF8; @(Get-StartApps | Where-Object { $_.AppID -match '^OpenAI\\.(Codex|CodexBeta|ChatGPT)_[^!]+!' } | Select-Object Name,AppID) | ConvertTo-Json -Compress\"";
                start.UseShellExecute = false; start.CreateNoWindow = true; start.RedirectStandardOutput = true; start.RedirectStandardError = true;
                start.StandardOutputEncoding = Encoding.UTF8;
                using (var process = Process.Start(start))
                {
                    var output = process.StandardOutput.ReadToEndAsync();
                    var error = process.StandardError.ReadToEndAsync();
                    if (!process.WaitForExit(12000)) { process.Kill(); throw new IOException(); }
                    string text = output.Result.Trim();
                    object decoded = String.IsNullOrEmpty(text) ? null : new JavaScriptSerializer().DeserializeObject(text);
                    var list = decoded as object[] ?? (decoded == null ? new object[0] : new[] { decoded });
                    foreach (object item in list)
                    {
                        var data = item as Dictionary<string, object>;
                        string id = Json.String(data, "AppID");
                        if (Regex.IsMatch(id, @"^OpenAI\.(Codex|CodexBeta|ChatGPT)_[A-Za-z0-9]+![A-Za-z0-9.]+$") && !apps.Any(a => a.AppId == id))
                            apps.Add(new AppTarget { Name = Json.String(data, "Name"), AppId = id });
                    }
                }
            }
            catch { /* A custom executable can be chosen if discovery is unavailable. */ }
            return apps;
        }
        public static bool TrustedExecutable(string path)
        {
            if (!File.Exists(path) || !Path.GetFileName(path).Equals("ChatGPT.exe", StringComparison.OrdinalIgnoreCase)) return false;
            string id = PackageId(path);
            if (!String.IsNullOrEmpty(id)) return true;
            string company = FileVersionInfo.GetVersionInfo(path).CompanyName ?? "";
            return company.IndexOf("OpenAI", StringComparison.OrdinalIgnoreCase) >= 0;
        }
        static string PackageId(string path)
        {
            var match = Regex.Match(path, @"[\\/]WindowsApps[\\/](OpenAI\.(?:CodexBeta|Codex|ChatGPT))_[^\\/]+__([a-zA-Z0-9]+)[\\/]", RegexOptions.IgnoreCase);
            return match.Success ? match.Groups[1].Value + "_" + match.Groups[2].Value + "!App" : "";
        }
        static HashSet<int> RelatedIds(List<int> roots, Dictionary<int, int> tree)
        {
            var ids = new HashSet<int>(roots);
            bool changed;
            do
            {
                changed = false;
                foreach (var pair in tree) if (ids.Contains(pair.Value) && ids.Add(pair.Key)) changed = true;
            } while (changed);
            return ids;
        }
        public static object[] InspectAccess()
        {
            var roots = new List<int>();
            foreach (var p in Process.GetProcessesByName("ChatGPT")) { using (p) roots.Add(p.Id); }
            var ids = RelatedIds(roots, Native.ProcessTree());
            var results = new List<object>();
            foreach (int id in ids)
            {
                string name = ""; string error = null; int? code = null;
                try
                {
                    using (var p = Process.GetProcessById(id)) name = p.ProcessName;
                    using (var handle = Native.Capture(id)) { if (handle == null) error = "已退出"; }
                }
                catch (Exception e) { error = e.GetType().Name; var win32 = e as System.ComponentModel.Win32Exception; if (win32 != null) code = win32.NativeErrorCode; }
                results.Add(new { Name = name, ProcessId = id, Error = error, NativeErrorCode = code });
            }
            return results.ToArray();
        }
        public void Stop(Action<string> progress)
        {
            progress("正在关闭 ChatGPT 窗口和其后台进程…");
            var roots = new List<int>();
            foreach (var process in Process.GetProcessesByName("ChatGPT"))
            {
                using (process)
                {
                    try
                    {
                        if (TrustedExecutable(process.MainModule.FileName)) roots.Add(process.Id);
                        else throw new InvalidDataException("发现无法识别的 ChatGPT 进程，请先手动退出所有 ChatGPT 窗口。");
                    }
                    catch (InvalidOperationException) { }
                    catch (System.ComponentModel.Win32Exception) { throw new InvalidDataException("无法访问 ChatGPT 进程，请先手动退出应用，或以相同权限运行切换器。"); }
                }
            }
            var tree = Native.ProcessTree();
            var ids = RelatedIds(roots, tree);
            int self = Process.GetCurrentProcess().Id;
            if (ids.Contains(self)) throw new InvalidDataException("请从资源管理器单独打开切换器，再进行账号切换。");
            // Keep process handles open so a recycled PID cannot identify an unrelated process.
            var captured = new Dictionary<int, Native.TrackedProcess>();
            try
            {
                foreach (int id in ids)
                {
                    try { var p = Native.Capture(id); if (p != null) captured.Add(id, p); }
                    catch (Exception e) { throw new OperationFailure("读取 ChatGPT 子进程 " + id, e); }
                }
                foreach (int id in roots)
                {
                    Native.TrackedProcess tracked;
                    if (captured.TryGetValue(id, out tracked) && !tracked.HasExited)
                    {
                        try { using (var p = Process.GetProcessById(id)) p.CloseMainWindow(); }
                        catch (ArgumentException) { }
                        catch (InvalidOperationException) { }
                        catch (Exception e) { if (!tracked.Wait(200)) throw new OperationFailure("请求关闭 ChatGPT 窗口 " + id, e); }
                    }
                }
                var watch = Stopwatch.StartNew();
                while (watch.ElapsedMilliseconds < 6500 && captured.Values.Any(p => !p.HasExited)) Thread.Sleep(150);
                // Only captured descendants of the desktop are stopped; standalone Codex CLI is untouched.
                foreach (int id in ids.OrderByDescending(i => ProcessDepth(i, tree)))
                {
                    Native.TrackedProcess p;
                    if (captured.TryGetValue(id, out p))
                    {
                        try { p.StopAndWait(); }
                        catch (Exception e) { throw new OperationFailure("等待 ChatGPT 子进程退出 " + id, e); }
                    }
                }
                if (captured.Values.Any(p => !p.HasExited)) throw new InvalidDataException("ChatGPT 尚未完全退出，未替换登录文件，请手动退出后重试。");
                if (Process.GetProcessesByName("ChatGPT").Any(p => { using (p) { try { return TrustedExecutable(p.MainModule.FileName); } catch (InvalidOperationException) { return false; } } }))
                    throw new InvalidDataException("检测到新启动的 ChatGPT，未替换登录文件，请关闭应用后重试。");
            }
            finally { foreach (var p in captured.Values) p.Dispose(); }
        }
        static int ProcessDepth(int id, Dictionary<int, int> tree)
        {
            var visited = new HashSet<int>(); int depth = 0, parent;
            while (visited.Add(id) && tree.TryGetValue(id, out parent) && parent != 0) { depth++; id = parent; }
            return depth;
        }
        public void Start(AppTarget target)
        {
            if (!String.IsNullOrEmpty(target.AppId)) Native.Activate(target.AppId);
            else
            {
                if (!TrustedExecutable(target.ExePath)) throw new InvalidDataException("所选程序不是可识别的 ChatGPT.exe。");
                Process.Start(new ProcessStartInfo(target.ExePath) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(target.ExePath) });
            }
            var watch = Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < 15000)
            {
                foreach (var p in Process.GetProcessesByName("ChatGPT"))
                {
                    using (p)
                    {
                        try
                        {
                            string path = p.MainModule.FileName;
                            if (!p.HasExited && ((!String.IsNullOrEmpty(target.AppId) && PackageId(path) == target.AppId) || (String.IsNullOrEmpty(target.AppId) && String.Equals(path, target.ExePath, StringComparison.OrdinalIgnoreCase)))) return;
                        }
                        catch { }
                    }
                }
                Thread.Sleep(250);
            }
            throw new InvalidDataException("已发出启动请求，但未确认 ChatGPT 启动。请从开始菜单手动打开。");
        }
    }
    static class Native
    {
        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);
        [DllImport("kernel32.dll", SetLastError = true)] static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool TerminateProcess(IntPtr handle, uint exitCode);
        public sealed class TrackedProcess : IDisposable
        {
            IntPtr handle;
            public TrackedProcess(IntPtr value) { handle = value; }
            public bool Wait(uint milliseconds)
            {
                uint result = WaitForSingleObject(handle, milliseconds);
                if (result == 0xFFFFFFFF) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                return result == 0;
            }
            public bool HasExited { get { return Wait(0); } }
            public void StopAndWait()
            {
                if (HasExited) return;
                if (!TerminateProcess(handle, 0))
                {
                    int code = Marshal.GetLastWin32Error();
                    // ERROR_ACCESS_DENIED can also mean the process exited during shutdown.
                    if (!Wait(300)) throw new System.ComponentModel.Win32Exception(code);
                }
                if (!Wait(5000)) throw new InvalidDataException("后台进程退出超时；未替换登录文件。");
            }
            public void Dispose() { if (handle != IntPtr.Zero) { CloseHandle(handle); handle = IntPtr.Zero; } }
        }
        public static TrackedProcess Capture(int id)
        {
            // Request only synchronization, termination and limited query, not PROCESS_ALL_ACCESS.
            IntPtr handle = OpenProcess(0x00101001, false, (uint)id);
            if (handle != IntPtr.Zero) return new TrackedProcess(handle);
            int code = Marshal.GetLastWin32Error();
            if (code == 87) return null; // The process disappeared after the snapshot.
            throw new System.ComponentModel.Win32Exception(code);
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct ProcessEntry
        {
            public uint Size, Usage, ProcessId;
            public IntPtr Heap;
            public uint ModuleId, Threads, ParentId;
            public int Priority;
            public uint Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string File;
        }
        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry entry);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry entry);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
        public static Dictionary<int, int> ProcessTree()
        {
            var result = new Dictionary<int, int>();
            IntPtr snapshot = CreateToolhelp32Snapshot(2, 0);
            if (snapshot == new IntPtr(-1)) throw new IOException("无法读取进程列表。");
            try
            {
                var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf(typeof(ProcessEntry)) };
                if (!Process32FirstW(snapshot, ref entry)) throw new IOException("无法读取进程列表。");
                do { result[(int)entry.ProcessId] = (int)entry.ParentId; } while (Process32NextW(snapshot, ref entry));
            }
            finally { CloseHandle(snapshot); }
            return result;
        }
        [ComImport, Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")] class ApplicationActivationManager { }
        [ComImport, Guid("2e941141-7f97-4756-ba1d-9decde894a3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IApplicationActivationManager
        {
            [PreserveSig] int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string appId, [MarshalAs(UnmanagedType.LPWStr)] string arguments, uint options, out uint processId);
            [PreserveSig] int ActivateForFile([MarshalAs(UnmanagedType.LPWStr)] string appId, IntPtr items, [MarshalAs(UnmanagedType.LPWStr)] string verb, out uint processId);
            [PreserveSig] int ActivateForProtocol([MarshalAs(UnmanagedType.LPWStr)] string appId, IntPtr items, out uint processId);
        }
        public static void Activate(string appId)
        {
            object manager = new ApplicationActivationManager();
            try
            {
                uint id;
                int hr = ((IApplicationActivationManager)manager).ActivateApplication(appId, "", 0, out id);
                if (hr < 0) Marshal.ThrowExceptionForHR(hr);
            }
            finally { Marshal.FinalReleaseComObject(manager); }
        }
        public static bool ActivationAvailable()
        {
            object manager = null;
            try { manager = new ApplicationActivationManager(); return manager is IApplicationActivationManager; }
            catch { return false; }
            finally { if (manager != null) Marshal.FinalReleaseComObject(manager); }
        }
    }
    sealed class MainWindow : Form
    {
        string home;
        List<Profile> profiles = new List<Profile>();
        ListView list;
        TextBox path, saveName;
        Label current, status;
        ComboBox apps;
        CheckBox config;
        Button switchButton, refresh, browse, custom, saveCurrent, deleteAccount;
        ProgressBar progress;
        bool busy, canSave;
        public MainWindow(string initialHome) : this(initialHome, null) { }
        public MainWindow(string initialHome, string previewPath)
        {
            home = initialHome;
            Text = "Codex 账号切换器"; StartPosition = FormStartPosition.CenterScreen;
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            Size = new Size(940, 710); MinimumSize = new Size(850, 660);
            Font = new Font("Microsoft YaHei UI", 9F); BackColor = Color.FromArgb(244, 247, 250);
            AutoScaleMode = AutoScaleMode.Dpi;
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24), ColumnCount = 1, RowCount = 9 };
            int[] heights = { 64, 44, 44, 0, 46, 40, 48, 52, 48 };
            for (int i = 0; i < heights.Length; i++) layout.RowStyles.Add(i == 3 ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.Absolute, heights[i]));
            var heading = new Label { Text = "Codex 账号切换", Font = new Font(Font.FontFamily, 23, FontStyle.Bold), ForeColor = Color.FromArgb(25, 42, 64), Dock = DockStyle.Fill };
            layout.Controls.Add(heading, 0, 0);
            var folderRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3 };
            folderRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 94)); folderRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); folderRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 125));
            folderRow.Controls.Add(new Label { Text = "目录", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
            path = new TextBox { ReadOnly = true, Dock = DockStyle.Fill, Text = home, BackColor = Color.White };
            browse = MakeButton("选择目录", false); browse.Dock = DockStyle.Fill; browse.Click += ChooseHome;
            folderRow.Controls.Add(path, 1, 0); folderRow.Controls.Add(browse, 2, 0); layout.Controls.Add(folderRow, 0, 1);
            current = new Label { Dock = DockStyle.Fill, ForeColor = Color.FromArgb(25, 100, 89), TextAlign = ContentAlignment.MiddleLeft };
            layout.Controls.Add(current, 0, 2);
            list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false, BorderStyle = BorderStyle.FixedSingle };
            list.Columns.Add("账号文件夹", 180); list.Columns.Add("账号", 265); list.Columns.Add("登录文件", 215); list.Columns.Add("配置", 85);
            list.SelectedIndexChanged += delegate { UpdateSwitchButton(); };
            layout.Controls.Add(list, 0, 3);
            var appRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3 };
            appRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 94)); appRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); appRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
            appRow.Controls.Add(new Label { Text = "重启版本", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
            apps = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill }; apps.SelectedIndexChanged += delegate { UpdateSwitchButton(); };
            appRow.Controls.Add(apps, 1, 0); custom = MakeButton("选择程序…", false); custom.Dock = DockStyle.Fill; custom.Click += ChooseApp; appRow.Controls.Add(custom, 2, 0); layout.Controls.Add(appRow, 0, 4);
            config = new CheckBox { Text = "同时切换账号目录中的 config.toml", Checked = true, Dock = DockStyle.Fill }; layout.Controls.Add(config, 0, 5);
            var saveRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3 };
            saveRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 125)); saveRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); saveRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 175));
            saveRow.Controls.Add(new Label { Text = "新账号名称", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
            saveName = new TextBox { Dock = DockStyle.Fill, MaxLength = 80 };
            saveName.KeyDown += delegate(object sender, KeyEventArgs e) { if (e.KeyCode == Keys.Enter && saveCurrent.Enabled) { e.SuppressKeyPress = true; SaveCurrent(sender, EventArgs.Empty); } };
            saveRow.Controls.Add(saveName, 1, 0);
            saveCurrent = MakeButton("保存当前账号", false); saveCurrent.Dock = DockStyle.Fill; saveCurrent.Click += SaveCurrent;
            saveRow.Controls.Add(saveCurrent, 2, 0); layout.Controls.Add(saveRow, 0, 6);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
            switchButton = MakeButton("切换账号并重启", true); switchButton.Width = 205; switchButton.Click += Switch;
            refresh = MakeButton("刷新列表", false); refresh.Click += async delegate { await Reload(); };
            deleteAccount = MakeButton("删除账号", false); deleteAccount.Click += DeleteAccount;
            var backups = MakeButton("查看备份", false); backups.Click += delegate { string dir = Path.Combine(home, "account-switcher-backups"); if (Directory.Exists(dir)) Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true }); else status.Text = "还没有切换备份。"; };
            buttons.Controls.Add(switchButton); buttons.Controls.Add(refresh); buttons.Controls.Add(backups); buttons.Controls.Add(deleteAccount); layout.Controls.Add(buttons, 0, 7);
            var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
            footer.RowStyles.Add(new RowStyle(SizeType.Absolute, 8)); footer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            progress = new ProgressBar { Dock = DockStyle.Fill, Style = ProgressBarStyle.Marquee, Visible = false };
            status = new Label { Text = "选中账号后切换。操作会关闭所有 ChatGPT 窗口，请先保存正在进行的工作。", Dock = DockStyle.Fill, AutoEllipsis = true, ForeColor = Color.FromArgb(86, 99, 115), TextAlign = ContentAlignment.MiddleLeft };
            footer.Controls.Add(progress, 0, 0); footer.Controls.Add(status, 0, 1); layout.Controls.Add(footer, 0, 8);
            Controls.Add(layout);
            Shown += async delegate {
                await Reload();
                if (previewPath != null)
                {
                    using (var bitmap = new Bitmap(Width, Height)) { DrawToBitmap(bitmap, new Rectangle(0, 0, Width, Height)); bitmap.Save(previewPath, System.Drawing.Imaging.ImageFormat.Png); }
                    Close();
                }
            };
            if (previewPath != null) { ShowInTaskbar = false; Opacity = 0; }
            FormClosing += delegate(object sender, FormClosingEventArgs e) { if (busy) e.Cancel = true; };
        }
        Button MakeButton(string text, bool primary)
        {
            return new Button { Text = text, Size = new Size(112, 39), FlatStyle = FlatStyle.Flat, BackColor = primary ? Color.FromArgb(18, 118, 105) : Color.White, ForeColor = primary ? Color.White : Color.FromArgb(40, 53, 70), Cursor = Cursors.Hand, Margin = new Padding(6, 4, 0, 0) };
        }
        void UpdateSwitchButton()
        {
            switchButton.Enabled = !busy && apps.SelectedItem != null && list.SelectedItems.Count == 1 && ((Profile)list.SelectedItems[0].Tag).Error == null;
            deleteAccount.Enabled = !busy && list.SelectedItems.Count == 1;
        }
        void SetBusy(bool value)
        {
            busy = value; progress.Visible = value; list.Enabled = !value; apps.Enabled = !value; config.Enabled = !value;
            refresh.Enabled = !value; browse.Enabled = !value; custom.Enabled = !value; UpdateSwitchButton();
            saveName.Enabled = !value; saveCurrent.Enabled = !value && canSave;
        }
        async Task Reload()
        {
            SetBusy(true); status.Text = "正在检查账号文件和 ChatGPT 安装…";
            string selected = list.SelectedItems.Count == 1 ? ((Profile)list.SelectedItems[0].Tag).Name : null;
            AppTarget previousApp = apps.SelectedItem as AppTarget;
            try
            {
                var found = await Task.Run(() => new { Profiles = Directory.Exists(Path.Combine(home, "account")) ? Store.Scan(home) : new List<Profile>(), Current = Store.TryRead(Path.Combine(home, "auth.json")), Apps = Desktop.FindApps() });
                profiles = found.Profiles; list.Items.Clear();
                canSave = found.Current != null;
                var activeNames = profiles.Where(p => p.Auth != null && p.Auth.SameUser(found.Current)).Select(p => p.Name).ToArray();
                current.Text = "当前账号：" + (activeNames.Length == 0 ? (found.Current == null ? "未登录或登录文件不可读取" : (String.IsNullOrEmpty(found.Current.Email) ? found.Current.Mode : found.Current.Email) + " · 尚未保存") : String.Join(" / ", activeNames));
                foreach (Profile p in profiles)
                {
                    bool active = p.Auth != null && p.Auth.SameUser(found.Current);
                    var item = new ListViewItem(p.Name + (active ? "  · 当前" : "")) { Tag = p };
                    item.SubItems.Add(p.Auth == null ? "—" : String.IsNullOrEmpty(p.Auth.Email) ? p.Auth.Mode + " 账号" : p.Auth.Email);
                    item.SubItems.Add(p.Error ?? "格式有效（本地）"); item.SubItems.Add(File.Exists(Path.Combine(p.Directory, "config.toml")) ? "有" : "缺少");
                    if (p.Error != null) item.ForeColor = Color.Firebrick; else if (active) item.BackColor = Color.FromArgb(232, 246, 241);
                    list.Items.Add(item); if (p.Name == selected) item.Selected = true;
                }
                apps.Items.Clear(); foreach (var app in found.Apps) apps.Items.Add(app);
                if (previousApp != null)
                {
                    var match = found.Apps.FirstOrDefault(a => (!String.IsNullOrEmpty(a.AppId) && a.AppId == previousApp.AppId) || a.ExePath == previousApp.ExePath && !String.IsNullOrEmpty(a.ExePath));
                    if (match != null) apps.SelectedItem = match;
                    else if (String.IsNullOrEmpty(previousApp.AppId)) { apps.Items.Add(previousApp); apps.SelectedItem = previousApp; }
                }
                if (apps.SelectedIndex < 0 && apps.Items.Count > 0) apps.SelectedIndex = 0;
                status.Text = profiles.Count == 0 ? "还没有保存的账号。输入新账号名称后，点击“保存当前账号”。" : "已检测到 " + profiles.Count + " 个账号。可输入名称保存当前登录；切换前请保存工作。";
            }
            catch (Exception e) { canSave = false; profiles.Clear(); list.Items.Clear(); current.Text = "当前账号：无法检查"; status.Text = Store.SafeError(e); }
            finally { SetBusy(false); }
        }
        async void ChooseHome(object sender, EventArgs e)
        {
            using (var dialog = new FolderBrowserDialog { Description = "选择包含 account 文件夹的 Codex 目录", SelectedPath = home })
            {
                if (dialog.ShowDialog(this) == DialogResult.OK) { home = dialog.SelectedPath; path.Text = home; await Reload(); }
            }
        }
        void ChooseApp(object sender, EventArgs e)
        {
            using (var dialog = new OpenFileDialog { Title = "选择 ChatGPT.exe", Filter = "ChatGPT 程序 (ChatGPT.exe)|ChatGPT.exe", CheckFileExists = true })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                if (!Desktop.TrustedExecutable(dialog.FileName)) { MessageBox.Show(this, "请选择 OpenAI 安装的 ChatGPT.exe。", Text); return; }
                var app = new AppTarget { Name = "ChatGPT · 自选路径", ExePath = dialog.FileName }; apps.Items.Add(app); apps.SelectedItem = app;
            }
        }
        void ShowError(string stage, Exception error)
        {
            string log = Diagnostics.Record(home, stage, error);
            MessageBox.Show(this, Store.SafeError(error) + (log == null ? "" : "\r\n\r\n错误记录：" + log), Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        async void SaveCurrent(object sender, EventArgs e)
        {
            if (busy) return;
            string name;
            try { name = AccountRecorder.ValidateName(saveName.Text); }
            catch (Exception error) { ShowError("检查保存名称", error); return; }
            SetBusy(true); status.Text = "正在保存当前登录和配置…";
            try
            {
                string directory = await Task.Run(() => AccountRecorder.Save(home, name));
                await Reload();
                foreach (ListViewItem item in list.Items) if (((Profile)item.Tag).Name == name) { item.Selected = true; item.EnsureVisible(); }
                saveName.Clear(); status.Text = "当前账号已保存到 account / " + name + "。";
                MessageBox.Show(this, "当前账号已保存。\r\n\r\n目录：" + directory + "\r\n包含当前登录数据、已有配置和保存时间。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception error) { status.Text = "保存未完成。"; ShowError("保存当前账号", error); }
            finally { SetBusy(false); }
        }
        async void DeleteAccount(object sender, EventArgs e)
        {
            if (busy || list.SelectedItems.Count != 1) return;
            var profile = (Profile)list.SelectedItems[0].Tag;
            if (MessageBox.Show(this, "确定删除保存的账号“" + profile.Name + "”？\r\n\r\n将永久删除该账号文件夹中的登录信息、配置及其他文件。\r\n当前登录和切换备份不会被删除。", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            SetBusy(true); status.Text = "正在删除账号…";
            try
            {
                await Task.Run(() => AccountRemover.Delete(home, profile));
                await Reload();
                status.Text = "已删除保存的账号：" + profile.Name + "。";
            }
            catch (Exception error) { status.Text = "删除未完成。"; ShowError("删除账号", error); }
            finally { SetBusy(false); }
        }
        async void Switch(object sender, EventArgs e)
        {
            if (busy || list.SelectedItems.Count != 1) return;
            var profile = (Profile)list.SelectedItems[0].Tag;
            var target = apps.SelectedItem as AppTarget;
            bool copyConfig = config.Checked;
            SetBusy(true);
            try
            {
                var result = await Task.Run(() => SwitchService.Switch(home, profile, copyConfig, target, new Desktop(), message => {
                    if (!IsDisposed) BeginInvoke((Action)(() => { status.Text = message; }));
                }));
                await Reload();
                status.Text = "已切换到 " + profile.Name + (result.Restarted ? "，ChatGPT 已重启。" : "，请手动启动 ChatGPT。");
                MessageBox.Show(this, "登录文件已切换到：" + profile.Name + "\r\n" + (result.Restarted ? "ChatGPT 已重新启动。" : "自动重启未完成：" + result.RestartError) + "\r\n\r\n备份目录：" + result.BackupDirectory + "\r\n若应用要求重新登录，请登录后更新该账号的 auth.json。", Text, MessageBoxButtons.OK, result.Restarted ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            }
            catch (Exception error) { status.Text = "切换未完成。"; ShowError("切换并重启", error); }
            finally { SetBusy(false); }
        }
    }
    static class SelfTests
    {
        sealed class FakeDesktop : IDesktop
        {
            public bool Stopped, Started, FailStop, FailStart;
            public void Stop(Action<string> progress) { Stopped = true; if (FailStop) throw new IOException(); }
            public void Start(AppTarget target) { Started = true; if (FailStart) throw new IOException(); }
        }
        static byte[] Login(string subject, string token)
        {
            string claims = Convert.ToBase64String(Encoding.UTF8.GetBytes(Json.Encode(new { sub = subject, email = subject + "@example.test" }))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            return Encoding.UTF8.GetBytes(Json.Encode(new { auth_mode = "chatgpt", OPENAI_API_KEY = (string)null, tokens = new { id_token = "x." + claims + ".x", access_token = token, refresh_token = "fake-refresh-" + token, account_id = "shared-workspace" } }));
        }
        static void Assert(bool condition, string description, List<string> passed) { if (!condition) throw new Exception(description); passed.Add(description); }
        public static int Run(string reportPath)
        {
            var passed = new List<string>();
            string home = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(reportPath)), "test-work", Guid.NewGuid().ToString("N"));
            try
            {
                string first = Path.Combine(home, "account", "first"), second = Path.Combine(home, "account", "second"), invalid = Path.Combine(home, "account", "invalid");
                Directory.CreateDirectory(first); Directory.CreateDirectory(second); Directory.CreateDirectory(invalid);
                byte[] old = Login("first", "old"), fresh = Login("first", "refreshed"), next = Login("second", "next");
                File.WriteAllBytes(Path.Combine(first, "auth.json"), old); File.WriteAllBytes(Path.Combine(second, "auth.json"), next);
                File.WriteAllText(Path.Combine(first, "config.toml"), "model = \"old\"\n[desktop]\nx = true\n");
                File.WriteAllText(Path.Combine(second, "config.toml"), "model = \"next\"\ncli_auth_credentials_store = \"keyring\"\n[desktop]\nx = false\n");
                File.WriteAllBytes(Path.Combine(home, "auth.json"), fresh); File.WriteAllText(Path.Combine(home, "config.toml"), "model = \"active\"\n");
                var scan = Store.Scan(home);
                Assert(scan.Count == 3 && scan.Single(p => p.Name == "invalid").Error != null, "扫描账号与缺失文件检测", passed);
                Assert(Auth.Parse(old).SameUser(Auth.Parse(fresh)) && !Auth.Parse(old).SameUser(Auth.Parse(next)), "按用户与工作区识别账号，允许刷新令牌", passed);
                var app = new AppTarget { Name = "fake" };
                var desktop = new FakeDesktop();
                var result = SwitchService.Switch(home, scan.Single(p => p.Name == "second"), true, app, desktop, delegate { });
                Assert(desktop.Stopped && desktop.Started && result.Restarted, "关闭、替换、重启完整流程（模拟桌面）", passed);
                Assert(File.ReadAllBytes(Path.Combine(home, "auth.json")).SequenceEqual(next), "目标凭据字节完整替换", passed);
                Assert(File.ReadAllBytes(Path.Combine(first, "auth.json")).SequenceEqual(fresh), "切出前保存刷新后的凭据", passed);
                Assert(File.ReadAllBytes(Path.Combine(result.BackupDirectory, "auth.json")).SequenceEqual(fresh), "备份包含切换前的登录文件", passed);
                string config = File.ReadAllText(Path.Combine(home, "config.toml"));
                Assert(config.Contains("model = \"next\"") && config.Contains("cli_auth_credentials_store = \"file\"") && config.Contains("[desktop]\nx = false"), "账号配置切换并使用文件凭据存储", passed);
                File.WriteAllBytes(Path.Combine(second, "auth.json"), Login("second", "stale"));
                scan = Store.Scan(home);
                result = SwitchService.Switch(home, scan.Single(p => p.Name == "second"), false, app, new FakeDesktop(), delegate { });
                Assert(File.ReadAllBytes(Path.Combine(home, "auth.json")).SequenceEqual(next), "同一账号切换不会用旧令牌覆盖活动令牌", passed);
                Assert(File.ReadAllText(Path.Combine(home, "config.toml")) == config, "取消切换配置时保留现有配置", passed);
                var plan = SwitchPlan.Prepare(home, scan.Single(p => p.Name == "first"), true);
                bool failed = false;
                try { plan.Apply(delegate { }, name => { if (name == "config.toml") throw new IOException(); }); } catch (InvalidDataException) { failed = true; }
                Assert(failed && File.ReadAllBytes(Path.Combine(home, "auth.json")).SequenceEqual(next) && File.ReadAllText(Path.Combine(home, "config.toml")) == config, "第二个文件写入失败时恢复完整登录状态", passed);
                var badDesktop = new FakeDesktop(); failed = false;
                try { SwitchService.Switch(home, scan.Single(p => p.Name == "invalid"), true, app, badDesktop, delegate { }); } catch { failed = true; }
                Assert(failed && !badDesktop.Stopped, "无效账号在关闭应用之前被拦截", passed);
                badDesktop = new FakeDesktop { FailStop = true }; failed = false;
                try { SwitchService.Switch(home, scan.Single(p => p.Name == "first"), true, app, badDesktop, delegate { }); } catch { failed = true; }
                Assert(failed && badDesktop.Started && File.ReadAllBytes(Path.Combine(home, "auth.json")).SequenceEqual(next), "关闭失败不会改动凭据且尝试重新启动", passed);
                result = SwitchService.Switch(home, scan.Single(p => p.Name == "first"), false, app, new FakeDesktop { FailStart = true }, delegate { });
                Assert(!result.Restarted && result.RestartError != null && Auth.Parse(File.ReadAllBytes(Path.Combine(home, "auth.json"))).SameUser(Auth.Parse(fresh)), "重启失败与凭据切换结果分开报告", passed);
                File.Delete(Path.Combine(home, "config.toml"));
                plan = SwitchPlan.Prepare(home, scan.Single(p => p.Name == "second"), false);
                failed = false;
                try { plan.Apply(delegate { }, name => { if (name == "config.toml") throw new IOException(); }); } catch (InvalidDataException) { failed = true; }
                Assert(failed && !File.Exists(Path.Combine(home, "config.toml")) && Auth.Parse(File.ReadAllBytes(Path.Combine(home, "auth.json"))).SameUser(Auth.Parse(fresh)), "原配置不存在时仍可正确回滚", passed);
                string tableOnly = Encoding.UTF8.GetString(Store.FileCredentialConfig(Encoding.UTF8.GetBytes("[desktop]\nx = true\n")));
                Assert(tableOnly.StartsWith("cli_auth_credentials_store = \"file\"\n[desktop]"), "文件凭据设置插入 TOML 根区域", passed);
                var escaped = new Profile { Directory = Path.Combine(home, "outside"), Name = "outside" }; failed = false;
                try { SwitchPlan.Prepare(home, escaped, false); } catch (InvalidDataException) { failed = true; }
                Assert(failed, "拒绝 account 目录外的账号路径", passed);
                bool malformed = false; try { Auth.Parse(Encoding.UTF8.GetBytes("{bad json}")); } catch (InvalidDataException) { malformed = true; }
                Assert(malformed, "拒绝损坏 JSON 且不输出凭据", passed);
                using (var held = new FileStream(Path.Combine(home, "account-switcher.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                {
                    var concurrent = new FakeDesktop(); failed = false;
                    try { SwitchService.Switch(home, scan.Single(p => p.Name == "second"), false, app, concurrent, delegate { }); } catch (IOException) { failed = true; }
                    Assert(failed && !concurrent.Stopped, "并发切换被文件锁拦截", passed);
                }
                byte[] activeBeforeSave = File.ReadAllBytes(Path.Combine(home, "auth.json"));
                File.WriteAllText(Path.Combine(home, "config.toml"), "model = \"saved-current\"\n");
                string recorded = AccountRecorder.Save(home, "当前用户备份");
                Assert(File.ReadAllBytes(Path.Combine(recorded, "auth.json")).SequenceEqual(activeBeforeSave) && File.ReadAllText(Path.Combine(recorded, "config.toml")) == "model = \"saved-current\"\n", "输入中文名称保存当前登录和配置", passed);
                Assert(File.ReadAllBytes(Path.Combine(home, "auth.json")).SequenceEqual(activeBeforeSave), "保存账号不会更改当前登录", passed);
                var info = Json.Decode(File.ReadAllText(Path.Combine(recorded, "profile-info.json")));
                Assert(Json.String(info, "Name") == "当前用户备份" && !String.IsNullOrEmpty(Json.String(info, "SavedAtUtc")) && !File.ReadAllText(Path.Combine(recorded, "profile-info.json")).Contains("access_token"), "账号记录包含名称和保存时间且不重复写入令牌", passed);
                failed = false; try { AccountRecorder.Save(home, "当前用户备份"); } catch (InvalidDataException) { failed = true; }
                Assert(failed && File.ReadAllBytes(Path.Combine(recorded, "auth.json")).SequenceEqual(activeBeforeSave), "同名保存被拦截且不覆盖已有账号", passed);
                bool namesRejected = true;
                foreach (string name in new[] { "", "../escape", "folder\\escape", "CON", "NUL.txt", "LPT1", "invalid.", "invalid:name" })
                { bool rejected = false; try { AccountRecorder.ValidateName(name); } catch (InvalidDataException) { rejected = true; } namesRejected &= rejected; }
                Assert(namesRejected, "保存名称拒绝路径穿越和 Windows 非法名称", passed);
                File.Delete(Path.Combine(home, "config.toml"));
                string noConfig = AccountRecorder.Save(home, "login-only");
                Assert(File.Exists(Path.Combine(noConfig, "auth.json")) && !File.Exists(Path.Combine(noConfig, "config.toml")), "配置缺失时仍可保存登录数据", passed);
                using (var held = new FileStream(Path.Combine(home, "account-switcher.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                {
                    failed = false; try { AccountRecorder.Save(home, "concurrent-save"); } catch (IOException) { failed = true; }
                    Assert(failed && !Directory.Exists(Path.Combine(home, "account", "concurrent-save")), "保存与切换共用文件锁防止冲突", passed);
                }
                string staging = Path.Combine(home, "account", ".saving-test"); Directory.CreateDirectory(staging);
                Assert(!Store.Scan(home).Any(p => p.Directory == staging), "未完成的保存目录不会出现在账号列表", passed);
                bool exitedHandled = true;
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    using (var worker = Process.Start(new ProcessStartInfo(Application.ExecutablePath, "--test-worker exit") { UseShellExecute = false, CreateNoWindow = true }))
                    using (var tracked = Native.Capture(worker.Id))
                    {
                        if (tracked == null || !tracked.Wait(5000)) exitedHandled = false;
                        else tracked.StopAndWait();
                    }
                }
                Assert(exitedHandled, "自然退出的测试进程不会造成关闭权限误报", passed);
                using (var worker = Process.Start(new ProcessStartInfo(Application.ExecutablePath, "--test-worker stop") { UseShellExecute = false, CreateNoWindow = true }))
                using (var tracked = Native.Capture(worker.Id))
                {
                    Assert(tracked != null, "最小权限句柄能够捕获独立测试进程", passed);
                    tracked.StopAndWait(); Assert(tracked.HasExited, "最小权限关闭只作用于独立测试进程", passed);
                }
                string permissionMessage = Store.SafeError(new OperationFailure("关闭 ChatGPT", new System.ComponentModel.Win32Exception(5)));
                Assert(permissionMessage.Contains("关闭 ChatGPT") && permissionMessage.Contains("错误码 5") && !permissionMessage.Contains("检查文件权限"), "进程权限错误显示步骤和错误码", passed);
                string secret = "DO-NOT-LOG-THIS-FAKE-TOKEN";
                string diagnostic = Diagnostics.Record(home, "test", new IOException(secret));
                Assert(diagnostic != null && !File.ReadAllText(diagnostic).Contains(secret), "诊断日志不记录异常中的敏感内容", passed);
                File.WriteAllText(reportPath, Json.Encode(new { Passed = passed.Count, Tests = passed.ToArray(), RealLoginChanged = false, RealDesktopRestarted = false }), new UTF8Encoding(false));
                return 0;
            }
            catch (Exception e)
            {
                File.WriteAllText(reportPath, Json.Encode(new { Passed = passed.Count, Tests = passed.ToArray(), Failed = e.Message }), new UTF8Encoding(false));
                return 1;
            }
        }
    }
}
