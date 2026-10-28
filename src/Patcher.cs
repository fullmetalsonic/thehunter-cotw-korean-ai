using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("AI 자동 번역 패치")]
[assembly: AssemblyDescription("theHunter: Call of the Wild 한국어 번역 패치")]

namespace HunterPatcher
{
    internal sealed class PayloadFile
    {
        public string relativePath { get; set; }
        public string sha256 { get; set; }
    }

    internal sealed class PackageManifest
    {
        public string patchVersion { get; set; }
        public string supportedSteamBuild { get; set; }
        public string codeFinalVersion { get; set; }
        public string archivesVersion { get; set; }
        public string githubRepository { get; set; }
        public PayloadFile[] files { get; set; }
    }

    internal sealed class BackupFile
    {
        public string relativePath { get; set; }
        public bool existed { get; set; }
        public string sha256 { get; set; }
        public string payloadSha256 { get; set; }
    }

    internal sealed class BackupManifest
    {
        public int format { get; set; }
        public string gameRoot { get; set; }
        public string patchVersion { get; set; }
        public BackupFile[] files { get; set; }
    }

    internal sealed class PatchException : Exception
    {
        public PatchException(string message) : base(message) { }
    }

    internal sealed class PatchEngine
    {
        internal const string BackupName = ".ai-korean-patch-v1-backup";
        internal static readonly string[] RelativePaths = {
            "settings/hp_settings/string_ids.stringlookup", "ui/font_en.gfx"
        };
        internal const string LaunchOptions = "--vfs-fs dropzone --vfs-archive archives_win64 --vfs-archive patch_win64 --vfs-archive dlc_win64 --vfs-fs .";
        internal readonly PackageManifest Manifest;
        private readonly string packageRoot;
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();

        internal PatchEngine(string directory) : this(directory, LoadEmbeddedManifest()) { }

        internal PatchEngine(string directory, PackageManifest manifest)
        {
            packageRoot = Path.GetFullPath(directory);
            Manifest = manifest;
            ValidateManifest();
        }

        private static PackageManifest LoadEmbeddedManifest()
        {
            using (Stream resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("patch-manifest.json"))
            {
                if (resource == null) throw new PatchException("패키지에 버전 정보가 없습니다. 패처를 다시 압축 해제해 주세요.");
                using (StreamReader reader = new StreamReader(resource, Encoding.UTF8))
                    return Json.Deserialize<PackageManifest>(reader.ReadToEnd());
            }
        }

        private void ValidateManifest()
        {
            StableVersion packageVersion;
            if (Manifest == null || !StableVersion.TryParse(Manifest.patchVersion, out packageVersion) || Manifest.patchVersion.StartsWith("v", StringComparison.Ordinal) || Manifest.supportedSteamBuild != "26051119" ||
                Manifest.codeFinalVersion != "3366163" || Manifest.archivesVersion != "3322479" ||
                Manifest.githubRepository != "fullmetalsonic/thehunter-cotw-korean-ai" ||
                Manifest.files == null || Manifest.files.Length != RelativePaths.Length)
                throw new PatchException("지원하지 않는 패키지 버전 정보입니다.");
            for (int i = 0; i < RelativePaths.Length; i++)
                if (Manifest.files[i] == null || Manifest.files[i].relativePath != RelativePaths[i] || !IsHash(Manifest.files[i].sha256))
                    throw new PatchException("패키지의 대상 파일 또는 SHA-256 정보가 올바르지 않습니다.");
        }

        internal static string Hash(string path)
        {
            using (SHA256 sha = SHA256.Create())
            using (FileStream file = File.OpenRead(path))
                return BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "").ToLowerInvariant();
        }

        private static bool IsHash(string value)
        {
            return value != null && Regex.IsMatch(value, "\\A[0-9a-fA-F]{64}\\z");
        }

        private static bool EqualHash(string a, string b)
        {
            return String.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        internal static void RejectReparse(string path)
        {
            string current = Path.GetFullPath(path);
            while (!String.IsNullOrEmpty(current))
            {
                try
                {
                    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                        throw new PatchException("연결된 폴더·파일(junction/symlink)은 지원하지 않습니다: " + current);
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
                string parent = Path.GetDirectoryName(current);
                if (String.Equals(parent, current, StringComparison.OrdinalIgnoreCase)) break;
                current = parent;
            }
        }

        internal static string Child(string root, string relative)
        {
            string result = BoundedPath(root, relative);
            RejectReparse(result);
            return result;
        }

        private static string BoundedPath(string root, string relative)
        {
            string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string result = Path.GetFullPath(Path.Combine(fullRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!result.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new PatchException("허용된 폴더 밖의 경로입니다.");
            return result;
        }

        private string Payload(int index)
        {
            // OneDrive payloads can be cloud reparse points; SHA-256 validates their read-only bytes.
            return BoundedPath(packageRoot, "payload/dropzone/" + RelativePaths[index]);
        }

        private static string Target(string root, int index)
        {
            return Child(root, "dropzone/" + RelativePaths[index]);
        }

        internal void VerifyPayload()
        {
            for (int i = 0; i < RelativePaths.Length; i++)
            {
                string file = Payload(i);
                if (!File.Exists(file) || !EqualHash(Hash(file), Manifest.files[i].sha256))
                    throw new PatchException("패치 파일이 없거나 손상되었습니다: " + RelativePaths[i] + "\r\n압축 파일 전체를 다시 해제해 주세요.");
            }
        }

        private string ValidateGame(string selected, bool requireSupportedVersion = true)
        {
            if (String.IsNullOrWhiteSpace(selected)) throw new PatchException("게임 설치 폴더를 선택해 주세요.");
            string root;
            try { root = Path.GetFullPath(selected).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
            catch (Exception) { throw new PatchException("게임 폴더 경로가 올바르지 않습니다."); }
            if (root.StartsWith("\\\\", StringComparison.Ordinal) || !Directory.Exists(root))
                throw new PatchException("이 PC에 있는 게임 설치 폴더를 선택해 주세요.");
            RejectReparse(root);
            foreach (string relative in new[] { "theHunterCotW_F.exe", "steam_api64.dll", "versions.518791.txt", "archives_win64/game0.arc", "archives_win64/game0.tab" })
                if (!File.Exists(Child(root, relative)))
                    throw new PatchException("게임 설치 폴더를 확인해 주세요. 필요한 파일이 없습니다: " + relative);
            string versionPath = Child(root, "versions.518791.txt");
            Dictionary<string, object> versions;
            try { versions = Json.Deserialize<Dictionary<string, object>>(File.ReadAllText(versionPath, Encoding.UTF8)); }
            catch (Exception) { throw new PatchException("게임 버전 파일을 읽을 수 없습니다. Steam에서 파일 무결성을 확인해 주세요."); }
            if (requireSupportedVersion && (versions == null || !versions.ContainsKey("code_final") || !versions.ContainsKey("archives") ||
                Convert.ToString(versions["code_final"], System.Globalization.CultureInfo.InvariantCulture) != Manifest.codeFinalVersion ||
                Convert.ToString(versions["archives"], System.Globalization.CultureInfo.InvariantCulture) != Manifest.archivesVersion))
                throw new PatchException("지원하지 않는 게임 버전입니다. 지원 버전: code_final " + Manifest.codeFinalVersion + ", archives " + Manifest.archivesVersion);
            DirectoryInfo game = new DirectoryInfo(root);
            if (game.Parent == null || game.Parent.Parent == null || !String.Equals(game.Parent.Name, "common", StringComparison.OrdinalIgnoreCase) ||
                !String.Equals(game.Parent.Parent.Name, "steamapps", StringComparison.OrdinalIgnoreCase))
                throw new PatchException("Steam의 steamapps\\common 아래 게임 폴더를 선택해 주세요.");
            string acfPath = Child(game.Parent.Parent.FullName, "appmanifest_518790.acf");
            if (!File.Exists(acfPath)) throw new PatchException("Steam 설치 정보를 찾지 못했습니다(appmanifest_518790.acf).");
            string acf = File.ReadAllText(acfPath, Encoding.UTF8);
            if (VdfValue(acf, "appid") != "518790" || (requireSupportedVersion && VdfValue(acf, "buildid") != Manifest.supportedSteamBuild) ||
                !String.Equals(VdfValue(acf, "installdir"), game.Name, StringComparison.OrdinalIgnoreCase))
                throw new PatchException("지원하지 않는 Steam 빌드이거나 다른 게임의 설치 정보입니다. 지원 빌드: " + Manifest.supportedSteamBuild);
            for (int i = 0; i < RelativePaths.Length; i++)
            {
                string target = Target(root, i);
                if (Directory.Exists(target)) throw new PatchException("파일 위치에 같은 이름의 폴더가 있습니다: " + RelativePaths[i]);
            }
            Child(root, BackupName);
            return root;
        }

        internal static string VdfValue(string text, string key)
        {
            Match m = Regex.Match(text, "\"" + Regex.Escape(key) + "\"\\s*\"((?:\\\\.|[^\"\\\\])*)\"", RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value.Replace("\\\\", "\\").Replace("\\\"", "\"") : null;
        }

        private static void EnsureStopped()
        {
#if PATCHER_TESTS
            if (TestHooks.RunningGame) throw new PatchException("게임이 실행 중입니다. 게임을 완전히 종료한 뒤 다시 시도해 주세요.");
#else
            Process[] processes = Process.GetProcessesByName("theHunterCotW_F");
            try
            {
                if (processes.Length != 0) throw new PatchException("게임이 실행 중입니다. 게임을 완전히 종료한 뒤 다시 시도해 주세요.");
            }
            finally { foreach (Process process in processes) process.Dispose(); }
#endif
        }

        private static string CurrentHash(string path)
        {
            return File.Exists(path) ? Hash(path) : null;
        }

        private BackupManifest ReadBackup(string root)
        {
            string directory = Child(root, BackupName);
            if (!Directory.Exists(directory))
            {
                if (File.Exists(directory)) throw new PatchException("백업 폴더 위치에 파일이 있습니다.");
                return null;
            }
            string metadata = Child(directory, "backup.json");
            BackupManifest backup;
            try { backup = Json.Deserialize<BackupManifest>(File.ReadAllText(metadata, Encoding.UTF8)); }
            catch (Exception) { throw new PatchException("최초 백업 정보를 읽을 수 없습니다. 백업을 삭제하거나 덮어쓰지 말고 보관해 주세요."); }
            StableVersion backupVersion;
            if (backup == null || backup.format != 1 || !StableVersion.TryParse(backup.patchVersion, out backupVersion) ||
                !String.Equals(backup.gameRoot, root, StringComparison.OrdinalIgnoreCase) || backup.files == null || backup.files.Length != RelativePaths.Length)
                throw new PatchException("최초 백업 정보가 현재 게임 폴더·패치와 일치하지 않습니다. 백업을 보관해 주세요.");
            for (int i = 0; i < RelativePaths.Length; i++)
            {
                BackupFile item = backup.files[i];
                if (item == null || item.relativePath != RelativePaths[i] || !IsHash(item.payloadSha256) ||
                    (backup.patchVersion == Manifest.patchVersion && !EqualHash(item.payloadSha256, Manifest.files[i].sha256)) ||
                    (item.existed && !IsHash(item.sha256)) || (!item.existed && item.sha256 != null))
                    throw new PatchException("최초 백업 파일 정보가 손상되었습니다.");
                string original = Child(directory, "file-" + i + ".bin");
                if (item.existed && (!File.Exists(original) || !EqualHash(Hash(original), item.sha256)))
                    throw new PatchException("최초 백업 파일이 없거나 손상되었습니다: " + RelativePaths[i]);
                if (!item.existed && (File.Exists(original) || Directory.Exists(original)))
                    throw new PatchException("최초 백업에 예상하지 못한 파일이 있습니다.");
            }
            return backup;
        }

        private BackupManifest CreateBackup(string root)
        {
            string directory = Child(root, BackupName);
            string temporary = Child(root, BackupName + ".prepare-" + Guid.NewGuid().ToString("N"));
            BackupManifest backup = new BackupManifest { format = 1, gameRoot = root, patchVersion = Manifest.patchVersion, files = new BackupFile[RelativePaths.Length] };
            Directory.CreateDirectory(temporary);
            try
            {
                for (int i = 0; i < RelativePaths.Length; i++)
                {
                    string target = Target(root, i);
                    string originalHash = CurrentHash(target);
                    backup.files[i] = new BackupFile { relativePath = RelativePaths[i], existed = originalHash != null, sha256 = originalHash, payloadSha256 = Manifest.files[i].sha256 };
                    if (originalHash != null)
                    {
                        string copy = Child(temporary, "file-" + i + ".bin");
                        File.Copy(target, copy, false);
                        if (!EqualHash(Hash(copy), originalHash) || !EqualHash(CurrentHash(target), originalHash))
                            throw new PatchException("백업 중 게임 파일이 변경되었습니다. 게임과 다른 패치 도구를 종료해 주세요.");
                    }
                }
                File.WriteAllText(Child(temporary, "backup.json"), Json.Serialize(backup), new UTF8Encoding(false));
                RejectReparse(directory);
                if (Directory.Exists(directory) || File.Exists(directory)) throw new PatchException("백업 폴더가 동시에 생성되었습니다. 다시 상태를 확인해 주세요.");
                Directory.Move(temporary, directory);
                return ReadBackup(root);
            }
            finally { DeleteBackupPreparation(temporary); }
        }

        private static void DeleteBackupPreparation(string directory)
        {
            if (!Directory.Exists(directory)) return;
            RejectReparse(directory);
            foreach (string filename in new[] { "file-0.bin", "file-1.bin", "backup.json" })
            {
                string file = Child(directory, filename);
                if (File.Exists(file)) { File.SetAttributes(file, FileAttributes.Normal); File.Delete(file); }
            }
            Directory.Delete(directory, false);
        }

        internal string Inspect(string selected)
        {
            VerifyPayload();
            string root = ValidateGame(selected);
            BackupManifest backup = ReadBackup(root);
            bool installed = true;
            bool original = backup != null;
            for (int i = 0; i < RelativePaths.Length; i++)
            {
                string hash = CurrentHash(Target(root, i));
                installed &= EqualHash(hash, Manifest.files[i].sha256);
                if (backup != null) original &= EqualHash(hash, backup.files[i].sha256);
            }
            return "지원 Steam 빌드 " + Manifest.supportedSteamBuild + " / code_final " + Manifest.codeFinalVersion + " / archives " + Manifest.archivesVersion + "\r\n" +
                (installed ? "패치 파일 2개가 적용되어 있습니다." : original ? "최초 백업의 이전 상태입니다." : backup == null ? "적용 가능: 최초 적용 시 현재 파일 상태를 백업합니다." : "일부 파일 상태가 변경되었습니다. 적용·복원 전에 안전 검사를 수행합니다.") +
                "\r\n" + (backup == null ? "이 패처의 최초 백업 없음" : "최초 백업 무결성 확인됨: " + BackupName);
        }

        internal string Apply(string selected)
        {
            VerifyPayload();
            string root = ValidateGame(selected);
            using (Mutex mutex = Lock(root))
            {
                try
                {
                    EnsureStopped();
                    BackupManifest backup = ReadBackup(root);
                    if (backup == null) backup = CreateBackup(root);
                    for (int i = 0; i < RelativePaths.Length; i++)
                    {
                        string current = CurrentHash(Target(root, i));
                        if (!EqualHash(current, Manifest.files[i].sha256) && !EqualHash(current, backup.files[i].sha256))
                            throw new PatchException("최초 백업 이후 다른 내용으로 수정된 파일이 있습니다: " + RelativePaths[i] + "\r\n사용자 변경을 보존하기 위해 중단했습니다.");
                    }
                    string[] sources = Enumerable.Range(0, RelativePaths.Length).Select(Payload).ToArray();
                    string[] hashes = Manifest.files.Select(file => file.sha256).ToArray();
                    bool changed = ChangeFiles(root, sources, hashes, false);
                    return (changed ? "패치 파일 2개 적용과 SHA-256 확인을 완료했습니다." : "이미 같은 패치 파일 2개가 적용되어 있습니다.") +
                        "\r\nSteam 설정은 별도로 필요합니다. 게임 언어를 영어로 바꾸고 시작 옵션을 입력해 주세요.";
                }
                finally { mutex.ReleaseMutex(); }
            }
        }

        internal string Restore(string selected)
        {
            // A Steam update must not prevent restoring our own unchanged override files.
            string root = ValidateGame(selected, false);
            using (Mutex mutex = Lock(root))
            {
                try
                {
                    EnsureStopped();
                    BackupManifest backup = ReadBackup(root);
                    if (backup == null) throw new PatchException("이 패처의 최초 백업이 없어 이전 상태를 복원할 수 없습니다.");
                    for (int i = 0; i < RelativePaths.Length; i++)
                    {
                        string current = CurrentHash(Target(root, i));
                        if (!EqualHash(current, Manifest.files[i].sha256) && !EqualHash(current, backup.files[i].sha256))
                            throw new PatchException("패치 적용 후 수정되었거나 삭제된 파일이 있습니다: " + RelativePaths[i] + "\r\n사용자 변경을 보존하기 위해 복원을 중단했습니다.");
                    }
                    string[] sources = Enumerable.Range(0, RelativePaths.Length).Select(i => backup.files[i].existed ? Child(Child(root, BackupName), "file-" + i + ".bin") : null).ToArray();
                    bool changed = ChangeFiles(root, sources, backup.files.Select(file => file.sha256).ToArray());
                    return changed ? "최초 적용 직전의 두 파일 상태를 복원했습니다. 최초에 없던 파일은 제거했습니다.\r\n최초 백업과 다른 dropzone 파일은 보존했습니다. Steam 설정은 자동으로 바꾸지 않습니다." : "이미 최초 적용 직전의 파일 상태입니다. 최초 백업은 보존했습니다.";
                }
                finally { mutex.ReleaseMutex(); }
            }
        }

        private static Mutex Lock(string root)
        {
            string key;
            using (SHA256 sha = SHA256.Create()) key = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(root.ToUpperInvariant()))).Replace("-", "");
            Mutex mutex = new Mutex(false, "Local\\HunterAiPatch-" + key);
            bool acquired;
            try { acquired = mutex.WaitOne(0); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) { mutex.Dispose(); throw new PatchException("다른 패처가 이 게임 폴더를 처리 중입니다. 완료 후 다시 시도해 주세요."); }
            return mutex;
        }

        private sealed class Change
        {
            internal string Target, Prepared, Undo, BeforeHash, AfterHash;
            internal bool Committed;
        }

        private static bool ChangeFiles(string root, string[] sources, string[] hashes, bool strictSources = true)
        {
            List<Change> changes = new List<Change>();
            string transaction = Guid.NewGuid().ToString("N");
            bool succeeded = false;
            try
            {
                for (int i = 0; i < RelativePaths.Length; i++)
                {
                    string target = Target(root, i);
                    string before = CurrentHash(target);
                    if (EqualHash(before, hashes[i])) continue;
                    string parent = Path.GetDirectoryName(target);
                    RejectReparse(parent);
                    Directory.CreateDirectory(parent);
                    RejectReparse(parent);
                    Change change = new Change { Target = target, BeforeHash = before, AfterHash = hashes[i],
                        Prepared = Child(parent, ".ai-patch-" + transaction + ".new"), Undo = Child(parent, ".ai-patch-" + transaction + ".undo") };
                    changes.Add(change);
                    if (sources[i] != null)
                    {
                        if (strictSources) RejectReparse(sources[i]);
                        File.Copy(sources[i], change.Prepared, false);
                        File.SetAttributes(change.Prepared, FileAttributes.Normal);
                        if (!EqualHash(Hash(change.Prepared), hashes[i])) throw new PatchException("준비한 파일의 SHA-256이 일치하지 않습니다.");
                    }
                }
                EnsureStopped();
                foreach (Change change in changes)
                {
                    EnsureStopped();
                    RejectReparse(change.Target);
                    RejectReparse(change.Undo);
                    if (!EqualHash(CurrentHash(change.Target), change.BeforeHash)) throw new PatchException("처리 중 대상 파일이 변경되었습니다. 작업을 취소합니다.");
#if PATCHER_TESTS
                    TestHooks.BeforeCommit(changes.IndexOf(change));
#endif
                    if (change.AfterHash == null) File.Move(change.Target, change.Undo);
                    else if (change.BeforeHash != null) File.Replace(change.Prepared, change.Target, change.Undo);
                    else File.Move(change.Prepared, change.Target);
                    change.Committed = true;
                    if (!EqualHash(CurrentHash(change.Target), change.AfterHash)) throw new PatchException("적용한 파일의 SHA-256이 일치하지 않습니다.");
                }
                succeeded = true;
                return changes.Count != 0;
            }
            catch (Exception failure)
            {
                List<string> rollbackErrors = new List<string>();
                foreach (Change change in changes.AsEnumerable().Reverse())
                {
                    if (!change.Committed) continue;
                    try
                    {
                        RejectReparse(change.Target);
                        RejectReparse(change.Undo);
                        if (!EqualHash(CurrentHash(change.Target), change.AfterHash)) throw new PatchException("작업 중 다른 파일 변경을 감지했습니다.");
                        if (change.BeforeHash == null) File.Delete(change.Target);
                        else
                        {
                            if (!File.Exists(change.Undo) || !EqualHash(Hash(change.Undo), change.BeforeHash)) throw new PatchException("임시 복원 파일을 확인할 수 없습니다.");
                            if (File.Exists(change.Target)) File.Replace(change.Undo, change.Target, null);
                            else File.Move(change.Undo, change.Target);
                        }
                        if (!EqualHash(CurrentHash(change.Target), change.BeforeHash)) throw new PatchException("복원 SHA-256이 일치하지 않습니다.");
                    }
                    catch (Exception rollback) { rollbackErrors.Add(change.Target + ": " + rollback.Message); }
                }
                if (rollbackErrors.Count != 0)
                    throw new PatchException("작업 실패 후 일부 파일을 자동으로 되돌리지 못했습니다. 게임을 실행하지 말고 최초 백업과 임시 .undo 파일을 보관해 주세요.\r\n" + failure.Message + "\r\n" + String.Join("\r\n", rollbackErrors.ToArray()));
                throw new PatchException("작업을 완료하지 못했습니다. 변경된 파일은 작업 전 상태로 되돌렸습니다.\r\n" + FriendlyError(failure));
            }
            finally
            {
                foreach (Change change in changes)
                {
                    TryDeleteTemporary(change.Prepared);
                    // A failed rollback must retain its recovery file.
                    if (succeeded || !change.Committed || EqualHash(CurrentHashSafe(change.Target), change.BeforeHash))
                        TryDeleteTemporary(change.Undo);
                }
            }
        }

        private static string CurrentHashSafe(string path)
        {
            try { return CurrentHash(path); } catch { return "unreadable"; }
        }

        private static void TryDeleteTemporary(string path)
        {
            try { RejectReparse(path); if (File.Exists(path)) File.Delete(path); } catch { }
        }

        internal static string FriendlyError(Exception error)
        {
            if (error is UnauthorizedAccessException)
                return "게임 폴더에 쓸 권한이 없습니다. 게임과 다른 도구를 종료하고 폴더 권한을 확인하거나 패처를 관리자 권한으로 실행해 주세요.";
            if (error is IOException)
                return "파일에 접근하지 못했습니다. 게임·다른 패처를 종료하고 남은 디스크 공간과 폴더 쓰기 권한을 확인해 주세요.\r\n" + error.Message;
            return error.Message;
        }

        internal static List<string> DetectGames()
        {
            HashSet<string> steamRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddRegistryPath(steamRoots, Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath");
            AddRegistryPath(steamRoots, Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath");
            AddRegistryPath(steamRoots, Registry.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath");
            HashSet<string> libraries = new HashSet<string>(steamRoots, StringComparer.OrdinalIgnoreCase);
            foreach (string steam in steamRoots)
            {
                try
                {
                    string vdf = File.ReadAllText(Path.Combine(steam, "steamapps", "libraryfolders.vdf"), Encoding.UTF8);
                    foreach (Match match in Regex.Matches(vdf, "\"path\"\\s*\"((?:\\\\.|[^\"\\\\])*)\"", RegexOptions.IgnoreCase))
                        libraries.Add(match.Groups[1].Value.Replace("\\\\", "\\"));
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            List<string> games = new List<string>();
            foreach (string library in libraries)
            {
                try
                {
                    string acf = Path.Combine(library, "steamapps", "appmanifest_518790.acf");
                    if (!File.Exists(acf)) continue;
                    string directory = VdfValue(File.ReadAllText(acf, Encoding.UTF8), "installdir");
                    if (String.IsNullOrWhiteSpace(directory) || directory.IndexOfAny(new[] { '/', '\\', ':' }) >= 0) continue;
                    string game = Path.Combine(library, "steamapps", "common", directory);
                    if (Directory.Exists(game)) games.Add(game);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                catch (ArgumentException) { }
            }
            return games.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static void AddRegistryPath(HashSet<string> roots, RegistryKey hive, string subkey, string value)
        {
            try
            {
                using (RegistryKey key = hive.OpenSubKey(subkey))
                {
                    string path = key == null ? null : key.GetValue(value) as string;
                    if (!String.IsNullOrWhiteSpace(path) && Directory.Exists(path)) roots.Add(path);
                }
            }
            catch (System.Security.SecurityException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    internal sealed class StableVersion : IComparable<StableVersion>
    {
        private readonly string[] parts;
        private StableVersion(string[] value) { parts = value; }

        internal static bool TryParse(string text, out StableVersion version)
        {
            version = null;
            if (text == null) return false;
            Match match = Regex.Match(text, "\\Av?(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)(?:\\+[0-9A-Za-z-]+(?:\\.[0-9A-Za-z-]+)*)?\\z");
            if (!match.Success) return false;
            version = new StableVersion(new[] { match.Groups[1].Value, match.Groups[2].Value, match.Groups[3].Value });
            return true;
        }

        public int CompareTo(StableVersion other)
        {
            for (int i = 0; i < parts.Length; i++)
            {
                int length = parts[i].Length.CompareTo(other.parts[i].Length);
                if (length != 0) return length;
                int numeric = String.CompareOrdinal(parts[i], other.parts[i]);
                if (numeric != 0) return numeric;
            }
            return 0;
        }
    }

    internal sealed class UpdateResult
    {
        internal string Current, Latest, ReleaseUrl, Detail;
        internal bool? NeedsUpdate;
        internal string Display
        {
            get { return "현재 " + Current + " / 최신 " + (Latest ?? "확인 불가") + " / 업데이트 " + (NeedsUpdate.HasValue ? (NeedsUpdate.Value ? "필요" : "불필요") : "확인 불가") + "\r\n" + Detail; }
        }
    }

    internal static class UpdateChecker
    {
        internal static UpdateResult Check(PackageManifest manifest)
        {
            return CheckUsing(manifest, delegate
            {
                ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create("https://api.github.com/repos/" + manifest.githubRepository + "/releases/latest");
                request.UserAgent = "HunterAiTranslationPatcher/" + manifest.patchVersion;
                request.Accept = "application/vnd.github+json";
                request.AllowAutoRedirect = false;
                request.Timeout = 6000; request.ReadWriteTimeout = 6000;
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                using (StreamReader reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                {
                    StringBuilder body = new StringBuilder(); char[] buffer = new char[4096]; int read;
                    while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        body.Append(buffer, 0, read);
                        if (body.Length > 1024 * 1024) throw new IOException("Unexpectedly large release response.");
                    }
                    return body.ToString();
                }
            });
        }

        internal static UpdateResult CheckUsing(PackageManifest manifest, Func<string> fetch)
        {
            UpdateResult result = new UpdateResult { Current = manifest.patchVersion, ReleaseUrl = "https://github.com/" + manifest.githubRepository + "/releases", Detail = "업데이트 확인에 실패했습니다. 인터넷 연결·시간 초과·호출 제한을 확인하고 나중에 다시 시도해 주세요. 패치 적용·복원은 계속 사용할 수 있습니다." };
            try
            {
                Dictionary<string, object> release = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(fetch());
                if (release == null || !release.ContainsKey("draft") || !release.ContainsKey("prerelease") ||
                    !(release["draft"] is bool) || !(release["prerelease"] is bool) || (bool)release["draft"] || (bool)release["prerelease"])
                { result.Detail = "정식 Release 정보가 없습니다. 사전 릴리스·초안·일반 커밋은 업데이트 대상으로 사용하지 않습니다."; return result; }
                object tag, link;
                StableVersion current, latest;
                if (!release.TryGetValue("tag_name", out tag) || !(tag is string) || !StableVersion.TryParse((string)tag, out latest) ||
                    !StableVersion.TryParse(manifest.patchVersion, out current))
                { result.Detail = "Release의 버전 형식이 올바르지 않아 비교하지 않았습니다."; return result; }
                Uri uri;
                if (!release.TryGetValue("html_url", out link) || !(link is string) || !Uri.TryCreate((string)link, UriKind.Absolute, out uri) ||
                    uri.Scheme != "https" || !String.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) || uri.Port != 443 ||
                    uri.UserInfo.Length != 0 || !uri.AbsolutePath.StartsWith("/" + manifest.githubRepository + "/releases/tag/", StringComparison.OrdinalIgnoreCase))
                { result.Detail = "Release 링크를 확인할 수 없어 업데이트 정보를 사용하지 않았습니다."; return result; }
                result.Latest = ((string)tag).TrimStart('v');
                result.NeedsUpdate = latest.CompareTo(current) > 0;
                result.ReleaseUrl = uri.AbsoluteUri;
                result.Detail = result.NeedsUpdate.Value ? "새 정식 버전이 있습니다. Release에서 변경 내용을 확인한 뒤 직접 내려받으세요." : "현재 패처는 최신 정식 버전과 같거나 더 새롭습니다.";
                return result;
            }
            catch (Exception) { return result; }
        }
    }

    internal sealed class PatcherForm : Form
    {
        private readonly PatchEngine engine;
        private readonly ComboBox path = new ComboBox();
        private readonly TextBox status = new TextBox();
        private readonly List<Button> controls = new List<Button>();
        private readonly Label updateStatus = new Label();
        private readonly Button updateButton = new Button();
        private readonly LinkLabel releaseLink = new LinkLabel();
        private string releaseUrl;
        private bool updateChecking;
        private bool busy;

        internal PatcherForm(PatchEngine patchEngine)
        {
            engine = patchEngine;
            Text = "AI 자동 번역 패치 v" + engine.Manifest.patchVersion;
            Font = new Font("맑은 고딕", 10F);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(790, 675);
            MinimumSize = new Size(680, 665);
            StartPosition = FormStartPosition.CenterScreen;
            TableLayoutPanel layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), ColumnCount = 3, RowCount = 10 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            for (int i = 4; i < 10; i++) layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Label title = new Label { Text = "theHunter: Call of the Wild  ·  한국어 번역", AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, 0, 0, 10) };
            layout.Controls.Add(title, 0, 0); layout.SetColumnSpan(title, 3);
            Label folder = new Label { Text = "게임 설치 폴더 (theHunterCotW_F.exe가 있는 폴더)", AutoSize = true, Margin = new Padding(0, 2, 0, 6) };
            layout.Controls.Add(folder, 0, 1); layout.SetColumnSpan(folder, 3);
            path.Dock = DockStyle.Fill; path.Margin = new Padding(0, 0, 6, 10);
            path.AccessibleName = "게임 설치 폴더";
            layout.Controls.Add(path, 0, 2);
            Button browse = Button("폴더 선택", delegate { using (FolderBrowserDialog dialog = new FolderBrowserDialog { Description = "theHunterCotW_F.exe가 있는 게임 폴더 선택", SelectedPath = path.Text, ShowNewFolderButton = false }) if (dialog.ShowDialog(this) == DialogResult.OK) { path.Text = dialog.SelectedPath; RefreshState(); } });
            layout.Controls.Add(browse, 1, 2);
            layout.Controls.Add(Button("상태 확인", delegate { RefreshState(); }), 2, 2);
            status.Multiline = true; status.ReadOnly = true; status.ScrollBars = ScrollBars.Vertical; status.Dock = DockStyle.Fill; status.BackColor = SystemColors.Window; status.Margin = new Padding(0, 0, 0, 12); status.AccessibleName = "버전과 패치 상태";
            layout.Controls.Add(status, 0, 3); layout.SetColumnSpan(status, 3);
            FlowLayoutPanel actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 12) };
            actions.Controls.Add(Button("패치 적용", delegate { string root = path.Text; Run(delegate { return engine.Apply(root); }, true); }));
            actions.Controls.Add(Button("이전 상태 복원", delegate { string root = path.Text; Run(delegate { return engine.Restore(root); }, true); }));
            layout.Controls.Add(actions, 0, 4); layout.SetColumnSpan(actions, 3);
            Label instructions = new Label { AutoSize = true, Dock = DockStyle.Fill, Text = "Steam에서 한 번 설정해 주세요\r\n1. 게임 → 속성 → 일반 → 언어: English(영어)\r\n2. 같은 화면의 시작 옵션에 아래 문구 입력\r\n패처는 Steam 설정을 자동으로 변경하지 않습니다.", Margin = new Padding(0, 0, 0, 8) };
            layout.Controls.Add(instructions, 0, 5); layout.SetColumnSpan(instructions, 3);
            TextBox options = new TextBox { Text = PatchEngine.LaunchOptions, ReadOnly = true, Multiline = true, Height = 55, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical, AccessibleName = "Steam 시작 옵션", Margin = new Padding(0, 0, 6, 8) };
            layout.Controls.Add(options, 0, 6); layout.SetColumnSpan(options, 2);
            layout.Controls.Add(Button("옵션 복사", delegate { try { Clipboard.SetText(PatchEngine.LaunchOptions); status.Text = "시작 옵션을 복사했습니다. Steam 게임 속성의 시작 옵션에 붙여 넣으세요."; } catch (Exception error) { ShowError(error); } }), 2, 6);
            Label note = new Label { Text = "적용·복원 전 게임을 종료하세요. 최초 백업은 게임 폴더의 " + PatchEngine.BackupName + "에 보관됩니다.", AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 0) };
            layout.Controls.Add(note, 0, 7); layout.SetColumnSpan(note, 3);
            updateStatus.AutoSize = true; updateStatus.Dock = DockStyle.Fill; updateStatus.Margin = new Padding(0, 12, 0, 6);
            updateStatus.Text = "현재 " + engine.Manifest.patchVersion + " / 최신 확인 중 / 업데이트 확인 중";
            layout.Controls.Add(updateStatus, 0, 8); layout.SetColumnSpan(updateStatus, 3);
            FlowLayoutPanel updates = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0) };
            updateButton.Text = "업데이트 확인"; updateButton.AutoSize = true; updateButton.Padding = new Padding(8, 3, 8, 3);
            updateButton.Click += delegate { CheckUpdates(); }; updates.Controls.Add(updateButton);
            releaseUrl = "https://github.com/" + engine.Manifest.githubRepository + "/releases";
            releaseLink.Text = "Release 보기"; releaseLink.AutoSize = true; releaseLink.Margin = new Padding(8, 10, 0, 0);
            releaseLink.LinkClicked += delegate { try { Process.Start(releaseUrl); } catch (Exception error) { ShowError(error); } };
            updates.Controls.Add(releaseLink); layout.Controls.Add(updates, 0, 9); layout.SetColumnSpan(updates, 3);
            Controls.Add(layout);
            Shown += delegate
            {
                foreach (string game in PatchEngine.DetectGames()) path.Items.Add(game);
                if (path.Items.Count != 0) path.SelectedIndex = 0;
                if (!String.IsNullOrWhiteSpace(path.Text)) RefreshState();
                else status.Text = "게임 폴더를 자동으로 찾지 못했습니다. 폴더 선택을 눌러 설치 폴더를 지정해 주세요.\r\n지원 Steam 빌드: " + engine.Manifest.supportedSteamBuild;
                CheckUpdates();
            };
            FormClosing += delegate(object sender, FormClosingEventArgs e) { if (busy) { e.Cancel = true; status.AppendText("\r\n파일 작업이 끝난 뒤 창을 닫을 수 있습니다."); } };
        }

        private Button Button(string text, EventHandler click)
        {
            Button button = new Button { Text = text, AutoSize = true, Padding = new Padding(8, 3, 8, 3), Margin = new Padding(0, 0, 6, 8) };
            button.Click += click; controls.Add(button); return button;
        }

        private void RefreshState() { string root = path.Text; Run(delegate { return engine.Inspect(root); }, false); }

        private void CheckUpdates()
        {
            if (updateChecking) return;
            updateChecking = true; updateButton.Enabled = false;
            updateStatus.Text = "현재 " + engine.Manifest.patchVersion + " / 최신 확인 중 / 업데이트 확인 중";
            Task.Factory.StartNew(delegate { return UpdateChecker.Check(engine.Manifest); }).ContinueWith(delegate(Task<UpdateResult> task)
            {
                try
                {
                    if (IsDisposed || !IsHandleCreated) return;
                    BeginInvoke((Action)delegate
                    {
                        if (IsDisposed) return;
                        updateChecking = false; updateButton.Enabled = true;
                        if (task.IsFaulted) updateStatus.Text = "현재 " + engine.Manifest.patchVersion + " / 최신 확인 불가 / 업데이트 확인 불가";
                        else { updateStatus.Text = task.Result.Display; releaseUrl = task.Result.ReleaseUrl; }
                    });
                }
                catch (InvalidOperationException) { }
            });
        }

        private void Run(Func<string> operation, bool message)
        {
            if (busy) return;
            busy = true; path.Enabled = false; foreach (Button button in controls) button.Enabled = false;
            status.Text = "파일과 버전을 확인하고 있습니다…";
            Task.Factory.StartNew(operation).ContinueWith(delegate(Task<string> task)
            {
                BeginInvoke((Action)delegate
                {
                    busy = false; path.Enabled = true; foreach (Button button in controls) button.Enabled = true;
                    if (task.IsFaulted) ShowError(task.Exception.GetBaseException());
                    else { status.Text = task.Result; if (message) MessageBox.Show(this, task.Result, Text, MessageBoxButtons.OK, MessageBoxIcon.Information); }
                });
            });
        }

        private void ShowError(Exception error)
        {
            status.Text = PatchEngine.FriendlyError(error);
            MessageBox.Show(this, status.Text, "작업 중단", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                PatchEngine engine = new PatchEngine(AppDomain.CurrentDomain.BaseDirectory);
                if (args.Length == 1 && args[0] == "--verify-package") { engine.VerifyPayload(); return 0; }
                if (args.Length != 0) throw new PatchException("지원하지 않는 실행 옵션입니다. 패처를 두 번 클릭해 실행해 주세요.");
                Application.Run(new PatcherForm(engine));
                return 0;
            }
            catch (Exception error) { MessageBox.Show(PatchEngine.FriendlyError(error), "AI 자동 번역 패치", MessageBoxButtons.OK, MessageBoxIcon.Error); return 1; }
        }
    }
}
