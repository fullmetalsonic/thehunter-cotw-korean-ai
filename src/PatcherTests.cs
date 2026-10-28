using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace HunterPatcher
{
    internal static class TestHooks
    {
        internal static bool RunningGame;
        internal static int FailCommit = -1;
        internal static void BeforeCommit(int index)
        {
            if (index == FailCommit) throw new IOException("Test-only injected commit failure.");
        }
    }

    internal sealed class TestResult
    {
        public string name { get; set; }
        public string status { get; set; }
        public string detail { get; set; }
    }

    internal sealed class TestReport
    {
        public string timestampUtc { get; set; }
        public string sandbox { get; set; }
        public bool realGameModified { get; set; }
        public string payloadMode { get; set; }
        public string overall { get; set; }
        public TestResult[] results { get; set; }
    }

    internal sealed class Fixture
    {
        internal readonly string Root, Package;
        internal readonly PackageManifest Manifest;
        internal PatchEngine Engine { get { return new PatchEngine(Package, Manifest); } }
        internal string Target(int i) { return PatchEngine.Child(Root, "dropzone/" + PatchEngine.RelativePaths[i]); }
        internal string Backup(int i) { return PatchEngine.Child(Root, PatchEngine.BackupName + "/file-" + i + ".bin"); }
        internal string Acf { get { return Path.Combine(Directory.GetParent(Root).Parent.FullName, "appmanifest_518790.acf"); } }

        internal Fixture(string sandbox, string name, string realPayload)
        {
            string basePath = Path.Combine(sandbox, name);
            Root = Path.Combine(basePath, "steamapps", "common", "theHunterCotW");
            Package = Path.Combine(basePath, "package");
            Directory.CreateDirectory(Root);
            Write(Path.Combine(Root, "theHunterCotW_F.exe"), "not an executable: sandbox fixture");
            Write(Path.Combine(Root, "steam_api64.dll"), "sandbox fixture");
            Write(Path.Combine(Root, "versions.518791.txt"), "{\"code_final\":3366163,\"archives\":\"3322479\"}");
            Write(Path.Combine(Root, "archives_win64", "game0.arc"), "sandbox archive");
            Write(Path.Combine(Root, "archives_win64", "game0.tab"), "sandbox archive index");
            Write(Acf, "\"AppState\" { \"appid\" \"518790\" \"installdir\" \"theHunterCotW\" \"buildid\" \"26051119\" }");
            Manifest = new PackageManifest { patchVersion = "1.0.0", supportedSteamBuild = "26051119", codeFinalVersion = "3366163", archivesVersion = "3322479", githubRepository = "fullmetalsonic/thehunter-cotw-korean-ai", files = new PayloadFile[2] };
            for (int i = 0; i < 2; i++)
            {
                string payload = PatchEngine.Child(Package, "payload/dropzone/" + PatchEngine.RelativePaths[i]);
                Directory.CreateDirectory(Path.GetDirectoryName(payload));
                if (realPayload == null) Write(payload, "dummy Korean payload " + i + " 한글");
                else File.Copy(Path.Combine(realPayload, "dropzone", PatchEngine.RelativePaths[i]), payload);
                Manifest.files[i] = new PayloadFile { relativePath = PatchEngine.RelativePaths[i], sha256 = PatchEngine.Hash(payload) };
            }
            Write(Path.Combine(Package, "fixture-manifest.json"), new JavaScriptSerializer().Serialize(Manifest));
        }

        internal static void Write(string path, string value)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, value, new UTF8Encoding(false));
        }
    }

    internal static class TestProgram
    {
        private static readonly List<TestResult> Results = new List<TestResult>();
        private static string Sandbox;
        private static int count;

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }

        private static Fixture NewFixture() { return new Fixture(Sandbox, (++count).ToString("00"), null); }

        private static void ExpectFailure(Action operation, string messagePart)
        {
            bool threw = false;
            try { operation(); }
            catch (Exception error)
            {
                threw = true;
                Assert(error.Message.Contains(messagePart), "Expected rejection containing '" + messagePart + "', got: " + error.Message);
            }
            Assert(threw, "Unsafe operation was not rejected.");
        }

        private static void CheckNoTemps(Fixture fixture)
        {
            Assert(!Directory.GetFiles(fixture.Root, ".ai-patch-*", SearchOption.AllDirectories).Any(), "Transaction temporary files remain.");
            Assert(!Directory.GetDirectories(fixture.Root, "*.prepare-*", SearchOption.TopDirectoryOnly).Any(), "Backup preparation directory remains.");
        }

        private static void Test(string name, Action test)
        {
            TestHooks.RunningGame = false; TestHooks.FailCommit = -1;
            try { test(); Results.Add(new TestResult { name = name, status = "PASS", detail = "" }); Console.WriteLine("PASS " + name); }
            catch (Exception error) { Results.Add(new TestResult { name = name, status = "FAIL", detail = error.ToString() }); Console.WriteLine("FAIL " + name + ": " + error.Message); }
            finally { TestHooks.RunningGame = false; TestHooks.FailCommit = -1; }
        }

        private static void CheckInstalled(Fixture fixture)
        {
            for (int i = 0; i < 2; i++) Assert(PatchEngine.Hash(fixture.Target(i)) == fixture.Manifest.files[i].sha256, "Installed payload hash differs.");
            CheckNoTemps(fixture);
        }

        private static void RunDummyTests()
        {
            RunUpdateTests();
            Test("clean-apply-restore", delegate
            {
                Fixture f = NewFixture(); f.Engine.Apply(f.Root); CheckInstalled(f);
                f.Engine.Restore(f.Root);
                Assert(!File.Exists(f.Target(0)) && !File.Exists(f.Target(1)), "Initially absent files were not removed.");
                Assert(File.Exists(Path.Combine(f.Root, PatchEngine.BackupName, "backup.json")), "First backup was not retained."); CheckNoTemps(f);
            });
            Test("existing-patch-restore", delegate
            {
                Fixture f = NewFixture(); Fixture.Write(f.Target(0), "prior text patch"); Fixture.Write(f.Target(1), "prior font patch");
                string[] before = { PatchEngine.Hash(f.Target(0)), PatchEngine.Hash(f.Target(1)) };
                f.Engine.Apply(f.Root); CheckInstalled(f); f.Engine.Restore(f.Root);
                for (int i = 0; i < 2; i++) Assert(PatchEngine.Hash(f.Target(i)) == before[i], "Prior patch bytes were not restored."); CheckNoTemps(f);
            });
            Test("mixed-existing-and-absent", delegate
            {
                Fixture f = NewFixture(); Fixture.Write(f.Target(0), "old existing text"); string old = PatchEngine.Hash(f.Target(0));
                f.Engine.Apply(f.Root); f.Engine.Restore(f.Root);
                Assert(PatchEngine.Hash(f.Target(0)) == old && !File.Exists(f.Target(1)), "Mixed original state was not restored."); CheckNoTemps(f);
            });
            Test("reapply-and-idempotent-restore", delegate
            {
                Fixture f = NewFixture(); Fixture.Write(f.Target(0), "original text"); string old = PatchEngine.Hash(f.Target(0));
                f.Engine.Apply(f.Root); string metadata = PatchEngine.Hash(Path.Combine(f.Root, PatchEngine.BackupName, "backup.json"));
                f.Engine.Apply(f.Root); CheckInstalled(f); f.Engine.Restore(f.Root); f.Engine.Restore(f.Root);
                Assert(PatchEngine.Hash(f.Target(0)) == old && !File.Exists(f.Target(1)), "Idempotent restore changed original state.");
                f.Engine.Apply(f.Root); CheckInstalled(f); f.Engine.Restore(f.Root);
                Assert(PatchEngine.Hash(Path.Combine(f.Root, PatchEngine.BackupName, "backup.json")) == metadata, "First backup metadata was overwritten.");
                Assert(PatchEngine.Hash(f.Backup(0)) == old, "First original backup was overwritten."); CheckNoTemps(f);
            });
            Test("new-patch-version-after-previous-version-restore", delegate
            {
                Fixture f = NewFixture(); Fixture.Write(f.Target(0), "first original translation");
                PatchEngine previous = f.Engine;
                previous.Apply(f.Root);
                string metadata = Path.Combine(f.Root, PatchEngine.BackupName, "backup.json");
                string firstBackup = PatchEngine.Hash(metadata);
                JavaScriptSerializer serializer = new JavaScriptSerializer();
                PackageManifest nextManifest = serializer.Deserialize<PackageManifest>(serializer.Serialize(f.Manifest));
                nextManifest.patchVersion = "1.1.0";
                for (int i = 0; i < 2; i++)
                {
                    string payload = Path.Combine(f.Package, "payload", "dropzone", PatchEngine.RelativePaths[i]);
                    Fixture.Write(payload, "next public patch " + i);
                    nextManifest.files[i].sha256 = PatchEngine.Hash(payload);
                }
                PatchEngine next = new PatchEngine(f.Package, nextManifest);
                ExpectFailure(delegate { next.Apply(f.Root); }, "사용자 변경");
                previous.Restore(f.Root);
                next.Apply(f.Root);
                for (int i = 0; i < 2; i++) Assert(PatchEngine.Hash(f.Target(i)) == nextManifest.files[i].sha256, "Next patch was not installed.");
                next.Restore(f.Root);
                Assert(File.ReadAllText(f.Target(0)) == "first original translation" && !File.Exists(f.Target(1)), "Original state was lost across patch versions.");
                Assert(PatchEngine.Hash(metadata) == firstBackup, "First backup metadata was overwritten by an update.");
                CheckNoTemps(f);
            });
            Test("payload-tamper-blocks-before-write", delegate
            {
                Fixture f = NewFixture(); Fixture.Write(Path.Combine(f.Package, "payload", "dropzone", PatchEngine.RelativePaths[1]), "tampered");
                ExpectFailure(delegate { f.Engine.Apply(f.Root); }, "손상");
                Assert(!Directory.Exists(Path.Combine(f.Root, PatchEngine.BackupName)) && !Directory.Exists(Path.Combine(f.Root, "dropzone")), "Rejected payload changed game files.");
            });
            Test("unknown-steam-build", delegate
            {
                Fixture f = NewFixture(); Fixture.Write(f.Acf, File.ReadAllText(f.Acf).Replace("26051119", "99999999"));
                ExpectFailure(delegate { f.Engine.Apply(f.Root); }, "Steam 빌드");
                Assert(!Directory.Exists(Path.Combine(f.Root, PatchEngine.BackupName)), "Unsupported build created backup.");
            });
            Test("unknown-core-version", delegate
            {
                Fixture f = NewFixture(); Fixture.Write(Path.Combine(f.Root, "versions.518791.txt"), "{\"code_final\":3366164,\"archives\":\"3322479\"}");
                ExpectFailure(delegate { f.Engine.Apply(f.Root); }, "게임 버전");
                Assert(!Directory.Exists(Path.Combine(f.Root, PatchEngine.BackupName)), "Unsupported version created backup.");
            });
            Test("restore-after-steam-game-update", delegate
            {
                Fixture f = NewFixture(); Fixture.Write(f.Target(0), "translation before patch");
                f.Engine.Apply(f.Root);
                Fixture.Write(f.Acf, File.ReadAllText(f.Acf).Replace("26051119", "99999999"));
                Fixture.Write(Path.Combine(f.Root, "versions.518791.txt"), "{\"code_final\":9999999,\"archives\":\"9999999\"}");
                ExpectFailure(delegate { f.Engine.Apply(f.Root); }, "지원하지 않는");
                f.Engine.Restore(f.Root);
                Assert(File.ReadAllText(f.Target(0)) == "translation before patch" && !File.Exists(f.Target(1)), "Game update prevented safe restore.");
                CheckNoTemps(f);
            });
            Test("running-game", delegate
            {
                Fixture f = NewFixture(); TestHooks.RunningGame = true;
                ExpectFailure(delegate { f.Engine.Apply(f.Root); }, "실행 중");
                Assert(!Directory.Exists(Path.Combine(f.Root, PatchEngine.BackupName)), "Running game rejection changed target.");
                TestHooks.RunningGame = false; f.Engine.Apply(f.Root); TestHooks.RunningGame = true;
                ExpectFailure(delegate { f.Engine.Restore(f.Root); }, "실행 중");
                CheckInstalled(f);
            });
            Test("second-file-failure-rolls-back-apply", delegate
            {
                Fixture f = NewFixture(); Fixture.Write(f.Target(0), "original before failure"); string old = PatchEngine.Hash(f.Target(0));
                TestHooks.FailCommit = 1; ExpectFailure(delegate { f.Engine.Apply(f.Root); }, "되돌렸습니다");
                Assert(PatchEngine.Hash(f.Target(0)) == old && !File.Exists(f.Target(1)), "Apply failure left partial patch."); CheckNoTemps(f);
                TestHooks.FailCommit = -1; f.Engine.Apply(f.Root); CheckInstalled(f); f.Engine.Restore(f.Root);
                Assert(PatchEngine.Hash(f.Target(0)) == old && !File.Exists(f.Target(1)), "Retry lost original backup.");
            });
            Test("second-file-failure-rolls-back-restore", delegate
            {
                Fixture f = NewFixture(); Fixture.Write(f.Target(0), "original text"); Fixture.Write(f.Target(1), "original font"); f.Engine.Apply(f.Root);
                TestHooks.FailCommit = 1; ExpectFailure(delegate { f.Engine.Restore(f.Root); }, "되돌렸습니다");
                CheckInstalled(f); TestHooks.FailCommit = -1; f.Engine.Restore(f.Root);
                Assert(File.ReadAllText(f.Target(0)) == "original text" && File.ReadAllText(f.Target(1)) == "original font", "Restore retry failed."); CheckNoTemps(f);
            });
            Test("user-edit-blocks-restore-and-apply", delegate
            {
                Fixture f = NewFixture(); f.Engine.Apply(f.Root); Fixture.Write(f.Target(1), "user edited font"); string text = PatchEngine.Hash(f.Target(0));
                ExpectFailure(delegate { f.Engine.Restore(f.Root); }, "사용자 변경");
                ExpectFailure(delegate { f.Engine.Apply(f.Root); }, "사용자 변경");
                Assert(PatchEngine.Hash(f.Target(0)) == text && File.ReadAllText(f.Target(1)) == "user edited font", "User edit or other target was overwritten.");
            });
            Test("user-deletion-blocks-restore", delegate
            {
                Fixture f = NewFixture(); Fixture.Write(f.Target(1), "original font"); f.Engine.Apply(f.Root); File.Delete(f.Target(1));
                ExpectFailure(delegate { f.Engine.Restore(f.Root); }, "삭제");
                Assert(PatchEngine.Hash(f.Target(0)) == f.Manifest.files[0].sha256 && !File.Exists(f.Target(1)), "Unexpected deletion handling.");
            });
            Test("unrelated-dropzone-file-preserved", delegate
            {
                Fixture f = NewFixture(); string unrelated = Path.Combine(f.Root, "dropzone", "user-mod", "keep.bin"); Fixture.Write(unrelated, "unrelated user mod");
                string hash = PatchEngine.Hash(unrelated); f.Engine.Apply(f.Root); f.Engine.Restore(f.Root);
                Assert(PatchEngine.Hash(unrelated) == hash, "Unrelated dropzone content changed.");
            });
            Test("backup-corruption-blocks-all-writes", delegate
            {
                Fixture f = NewFixture(); Fixture.Write(f.Target(0), "old translation"); f.Engine.Apply(f.Root); Fixture.Write(f.Backup(0), "corrupt backup");
                ExpectFailure(delegate { f.Engine.Restore(f.Root); }, "손상"); ExpectFailure(delegate { f.Engine.Apply(f.Root); }, "손상"); CheckInstalled(f);
            });
            Test("backup-metadata-corruption", delegate
            {
                Fixture f = NewFixture(); f.Engine.Apply(f.Root); Fixture.Write(Path.Combine(f.Root, PatchEngine.BackupName, "backup.json"), "invalid json");
                ExpectFailure(delegate { f.Engine.Restore(f.Root); }, "백업"); CheckInstalled(f);
            });
            Test("invalid-path-and-traversal", delegate
            {
                Fixture f = NewFixture(); ExpectFailure(delegate { f.Engine.Apply(Path.Combine(Sandbox, "does-not-exist")); }, "폴더");
                ExpectFailure(delegate { f.Engine.Apply(Sandbox); }, "필요한 파일");
                ExpectFailure(delegate { PatchEngine.Child(f.Root, "../outside"); }, "폴더 밖");
                Assert(!Directory.Exists(Path.Combine(f.Root, PatchEngine.BackupName)), "Invalid path test changed game fixture.");
            });
            Test("core-file-missing", delegate
            {
                Fixture f = NewFixture(); File.Delete(Path.Combine(f.Root, "steam_api64.dll")); ExpectFailure(delegate { f.Engine.Apply(f.Root); }, "필요한 파일");
            });
            Test("restore-without-payload-files", delegate
            {
                Fixture f = NewFixture(); f.Engine.Apply(f.Root);
                for (int i = 0; i < 2; i++) File.Delete(Path.Combine(f.Package, "payload", "dropzone", PatchEngine.RelativePaths[i]));
                f.Engine.Restore(f.Root); Assert(!File.Exists(f.Target(0)) && !File.Exists(f.Target(1)), "Restore depended on external payload.");
            });
            Test("restore-in-new-process", delegate
            {
                Fixture f = NewFixture(); Fixture.Write(f.Target(0), "prior text in previous process"); string hash = PatchEngine.Hash(f.Target(0)); f.Engine.Apply(f.Root);
                ProcessStartInfo start = new ProcessStartInfo(AssemblyPath(), "--restore-sandbox \"" + f.Root + "\" \"" + f.Package + "\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                using (Process child = Process.Start(start))
                {
                    string output = child.StandardOutput.ReadToEnd() + child.StandardError.ReadToEnd();
                    Assert(child.WaitForExit(15000), "Child restore timed out."); Assert(child.ExitCode == 0, "Child restore failed: " + output);
                }
                Assert(PatchEngine.Hash(f.Target(0)) == hash && !File.Exists(f.Target(1)), "New process did not restore previous state."); CheckNoTemps(f);
            });
            Test("target-junction-rejected", delegate
            {
                Fixture f = NewFixture(); string outside = Path.Combine(Sandbox, "junction-target"); Directory.CreateDirectory(outside);
                string link = Path.Combine(f.Root, "dropzone"); CreateJunction(link, outside);
                try { ExpectFailure(delegate { f.Engine.Apply(f.Root); }, "junction/symlink"); Assert(!Directory.GetFiles(outside).Any(), "Junction target was modified."); }
                finally { Directory.Delete(link, false); }
            });
            Test("backup-junction-rejected", delegate
            {
                Fixture f = NewFixture(); string outside = Path.Combine(Sandbox, "junction-backup"); Directory.CreateDirectory(outside);
                string link = Path.Combine(f.Root, PatchEngine.BackupName); CreateJunction(link, outside);
                try { ExpectFailure(delegate { f.Engine.Apply(f.Root); }, "junction/symlink"); Assert(!Directory.GetFiles(outside).Any(), "Junction backup target was modified."); }
                finally { Directory.Delete(link, false); }
            });
            Test("read-only-payload-junction-hash-verified", delegate
            {
                Fixture f = NewFixture();
                string original = Path.Combine(f.Package, "payload", "dropzone");
                string relocated = Path.Combine(f.Package, "read-only-payload");
                Directory.Move(original, relocated); CreateJunction(original, relocated);
                try
                {
                    f.Engine.Apply(f.Root); CheckInstalled(f); f.Engine.Restore(f.Root);
                    Assert(!File.Exists(f.Target(0)) && !File.Exists(f.Target(1)), "Read-only reparse payload affected restore.");
                    Fixture.Write(Path.Combine(relocated, PatchEngine.RelativePaths[0]), "tampered linked payload");
                    ExpectFailure(delegate { f.Engine.Apply(f.Root); }, "손상");
                    Assert(!File.Exists(f.Target(0)) && !File.Exists(f.Target(1)), "Tampered linked payload changed game files.");
                }
                finally { Directory.Delete(original, false); }
            });
        }

        private static void RunUpdateTests()
        {
            Func<string, bool, bool, string> release = delegate(string version, bool prerelease, bool draft)
            {
                return new JavaScriptSerializer().Serialize(new { tag_name = version, prerelease = prerelease, draft = draft, html_url = "https://github.com/fullmetalsonic/thehunter-cotw-korean-ai/releases/tag/" + version });
            };
            Func<string, UpdateResult> check = delegate(string response) { return UpdateChecker.CheckUsing(new PackageManifest { patchVersion = "1.0.0", githubRepository = "fullmetalsonic/thehunter-cotw-korean-ai" }, delegate { return response; }); };
            Test("update-same-version", delegate { UpdateResult r = check(release("v1.0.0", false, false)); Assert(r.NeedsUpdate == false && r.Latest == "1.0.0", "Same version comparison failed."); });
            Test("update-new-version", delegate { Assert(check(release("v1.10.0", false, false)).NeedsUpdate == true, "New version was not detected."); });
            Test("update-older-release", delegate { Assert(check(release("v0.9.9", false, false)).NeedsUpdate == false, "Older release incorrectly requested an update."); });
            Test("update-semver-numeric-and-metadata", delegate
            {
                Assert(check(release("v1.0.0+build.200", false, false)).NeedsUpdate == false, "Build metadata changed precedence.");
                StableVersion a, b; Assert(StableVersion.TryParse("1.10.0", out a) && StableVersion.TryParse("1.9.99", out b), "Stable parse failed.");
                StableVersion.TryParse("1.10.0", out a); StableVersion.TryParse("1.9.99", out b); Assert(a.CompareTo(b) > 0, "Versions were compared lexically.");
                Assert(check(release("v999999999999999999999.0.0", false, false)).NeedsUpdate == true, "Large valid SemVer failed.");
            });
            Test("update-prerelease-and-draft-excluded", delegate
            {
                Assert(!check(release("v2.0.0", true, false)).NeedsUpdate.HasValue, "Prerelease flag ignored.");
                Assert(!check(release("v2.0.0", false, true)).NeedsUpdate.HasValue, "Draft flag ignored.");
                Assert(!check(release("v2.0.0-beta.1", false, false)).NeedsUpdate.HasValue, "Prerelease tag accepted on stable channel.");
            });
            Test("update-invalid-version-and-json", delegate
            {
                foreach (string version in new[] { "v01.0.0", "v1.0", "deadbeef", "v1.0.0.0", "v1.0.0+", "v1.0.0-rc.1" })
                    Assert(!check(release(version, false, false)).NeedsUpdate.HasValue, "Invalid/staging version accepted: " + version);
                Assert(!check("not json").NeedsUpdate.HasValue && !check("{}").NeedsUpdate.HasValue, "Invalid response escaped fallback.");
            });
            Test("update-offline-timeout-rate-limit-nonblocking", delegate
            {
                Fixture f = NewFixture();
                foreach (System.Net.WebExceptionStatus status in new[] { System.Net.WebExceptionStatus.NameResolutionFailure, System.Net.WebExceptionStatus.Timeout, System.Net.WebExceptionStatus.ProtocolError })
                {
                    UpdateResult r = UpdateChecker.CheckUsing(f.Manifest, delegate { throw new System.Net.WebException("test network failure", status); });
                    Assert(!r.NeedsUpdate.HasValue && r.Current == "1.0.0" && r.ReleaseUrl.EndsWith("/releases"), "Network error became a product failure.");
                }
                f.Engine.Apply(f.Root); CheckInstalled(f); f.Engine.Restore(f.Root);
                Assert(!File.Exists(f.Target(0)) && !File.Exists(f.Target(1)), "Offline check blocked local apply/restore.");
            });
            Test("update-untrusted-release-link-rejected", delegate
            {
                string response = release("v2.0.0", false, false).Replace("https://github.com/", "https://example.com/");
                Assert(!check(response).NeedsUpdate.HasValue, "Unexpected external release link accepted.");
            });
        }

        private static string AssemblyPath() { return System.Reflection.Assembly.GetExecutingAssembly().Location; }

        private static void CreateJunction(string link, string target)
        {
            // Both paths are controlled sandbox paths; no existing path is replaced.
            Assert(Path.GetFullPath(link).StartsWith(Sandbox + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "Junction path escaped sandbox.");
            Assert(Path.GetFullPath(target).StartsWith(Sandbox + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "Junction target escaped sandbox.");
            ProcessStartInfo start = new ProcessStartInfo("cmd.exe", "/d /c mklink /J \"" + link + "\" \"" + target + "\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using (Process child = Process.Start(start))
            {
                string output = child.StandardOutput.ReadToEnd() + child.StandardError.ReadToEnd(); child.WaitForExit();
                Assert(child.ExitCode == 0, "Could not create sandbox junction: " + output);
            }
        }

        private static void RealPayloadTest(string payloadRoot)
        {
            Test("real-payload-apply-hash-new-engine-restore", delegate
            {
                string manifestPath = Path.Combine(payloadRoot, "manifest.json");
                PackageManifest expected = new JavaScriptSerializer().Deserialize<PackageManifest>(File.ReadAllText(manifestPath));
                Fixture f = new Fixture(Sandbox, "real-payload", payloadRoot);
                for (int i = 0; i < 2; i++) Assert(f.Manifest.files[i].sha256.Equals(expected.files[i].sha256, StringComparison.OrdinalIgnoreCase), "Real payload differs from final manifest.");
                PatchEngine engine = new PatchEngine(f.Package, expected); Fixture.Write(f.Target(0), "existing patch before real payload"); string prior = PatchEngine.Hash(f.Target(0));
                engine.Apply(f.Root); CheckInstalled(f); new PatchEngine(f.Package, expected).Restore(f.Root);
                Assert(PatchEngine.Hash(f.Target(0)) == prior && !File.Exists(f.Target(1)), "Real payload did not restore original mixed state."); CheckNoTemps(f);
            });
        }

        private static int RestoreChild(string root, string package)
        {
            string temporary = Path.Combine(Path.GetTempPath(), "HunterAiPatchTests") + Path.DirectorySeparatorChar;
            Assert(Path.GetFullPath(root).StartsWith(temporary, StringComparison.OrdinalIgnoreCase), "Child restore only accepts test sandbox roots.");
            Assert(Path.GetFullPath(package).StartsWith(temporary, StringComparison.OrdinalIgnoreCase), "Child restore only accepts test package roots.");
            PackageManifest manifest = new JavaScriptSerializer().Deserialize<PackageManifest>(File.ReadAllText(Path.Combine(package, "fixture-manifest.json")));
            Console.WriteLine(new PatchEngine(package, manifest).Restore(root)); return 0;
        }

        private static int Main(string[] args)
        {
            try
            {
                if (args.Length == 3 && args[0] == "--restore-sandbox") return RestoreChild(args[1], args[2]);
                string reportPath = args.Length >= 2 && args[0] == "--report" ? Path.GetFullPath(args[1]) : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "patcher-validation.json");
                string realPayload = args.Length == 4 && args[2] == "--real-payload-test" ? Path.GetFullPath(args[3]) : null;
                Sandbox = Path.Combine(Path.GetTempPath(), "HunterAiPatchTests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Sandbox);
                if (realPayload == null) RunDummyTests(); else RealPayloadTest(realPayload);
                TestReport report = new TestReport { timestampUtc = DateTime.UtcNow.ToString("o"), sandbox = Sandbox, realGameModified = false, payloadMode = realPayload == null ? "dummy" : "real-package", overall = Results.All(result => result.status == "PASS") ? "PASS" : "FAIL", results = Results.ToArray() };
                Fixture.Write(reportPath, new JavaScriptSerializer().Serialize(report));
                Console.WriteLine(report.overall + "; report: " + reportPath + "; sandbox retained: " + Sandbox);
                return report.overall == "PASS" ? 0 : 1;
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 2; }
        }
    }
}
