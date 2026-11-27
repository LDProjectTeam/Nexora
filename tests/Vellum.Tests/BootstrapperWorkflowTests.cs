using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Vellum.Core.Helpers;
using Vellum.Core.Interfaces;
using Vellum.Core.Models;
using Vellum.Core.Services;
using Xunit;

namespace Vellum.Tests
{
    [Collection("Update")]
    public class BootstrapperWorkflowTests
    {
        [Fact]
        public async Task Workflow_checks_integrity_then_starts_launcher_with_arguments()
        {
            using var temp = new TempApp();
            var updater = new FakeLauncherUpdateService();
            var integrity = new FakeLauncherIntegrityService();
            var starter = new FakeProcessStarter();
            var progress = new List<BootstrapperProgress>();
            var workflow = CreateWorkflow(updater, integrity, starter);

            var result = await workflow.RunAsync(new BootstrapperRunRequest
            {
                AppDirectory = temp.Root,
                CurrentAppVersion = "1.0.0",
                LauncherArguments = { "--profile", "two words" }
            }, new Progress<BootstrapperProgress>(progress.Add));

            Assert.True(result.Success);
            Assert.True(result.Launched);
            Assert.Single(starter.Calls);
            Assert.Equal(Path.Combine(temp.Root, "Vellum.exe"), starter.Calls[0].File);
            Assert.Equal(new[] { "--profile", "two words" }, starter.Calls[0].Args);
            Assert.Equal(temp.Root, starter.Calls[0].Directory);
            Assert.Contains(progress, p => p.Stage == BootstrapperStage.CheckingUpdates);
            Assert.Contains(progress, p => p.Stage == BootstrapperStage.VerifyingFiles);
            Assert.Contains(progress, p => p.Stage == BootstrapperStage.StartingLauncher);
            Assert.True(integrity.Calls >= 1);
        }

        [Fact]
        public async Task Workflow_never_starts_launcher_after_fatal_integrity_failure()
        {
            using var temp = new TempApp();
            var updater = new FakeLauncherUpdateService();
            var integrity = new FakeLauncherIntegrityService();
            integrity.Results.Enqueue(new LauncherIntegrityResult
            {
                Passed = false,
                Problems = { "Vellum.exe is missing." }
            });
            var starter = new FakeProcessStarter();
            var workflow = CreateWorkflow(updater, integrity, starter);

            var result = await workflow.RunAsync(new BootstrapperRunRequest
            {
                AppDirectory = temp.Root,
                CurrentAppVersion = "1.0.0"
            });

            Assert.False(result.Success);
            Assert.False(result.CanLaunchCurrent);
            Assert.Empty(starter.Calls);
        }

        [Fact]
        public async Task Workflow_applies_launcher_update_before_starting()
        {
            using var temp = new TempApp();
            var newExe = Encoding.UTF8.GetBytes("new launcher build");
            var staging = Path.Combine(temp.Root, "staging");
            Directory.CreateDirectory(staging);
            await File.WriteAllBytesAsync(Path.Combine(staging, "Vellum.exe"), newExe);
            var hash = await HashHelper.ComputeSha256Async(Path.Combine(staging, "Vellum.exe"));

            var updater = new FakeLauncherUpdateService
            {
                CheckResult = () => new LauncherUpdateStatus
                {
                    CurrentVersion = "1.0.0",
                    AvailableVersion = "2.0.0",
                    Phase = LauncherUpdatePhase.UpdateAvailable
                },
                DownloadResult = () => OperationResult.Ok(),
                AvailableManifest = new LauncherUpdateManifest
                {
                    Version = "2.0.0",
                    EntryPoint = "Vellum.exe",
                    Files =
                    {
                        new ManifestFile { Path = "Vellum.exe", Size = newExe.Length, Sha256 = hash }
                    }
                },
                ApplyRequest = new LauncherApplyRequest
                {
                    StagingDir = staging,
                    EntryPoint = "Vellum.exe",
                    Version = "2.0.0",
                    Files =
                    {
                        new ManifestFile { Path = "Vellum.exe", Size = newExe.Length, Sha256 = hash }
                    }
                }
            };
            var integrity = new FakeLauncherIntegrityService();
            var starter = new FakeProcessStarter();
            var workflow = CreateWorkflow(updater, integrity, starter);

            var result = await workflow.RunAsync(new BootstrapperRunRequest
            {
                AppDirectory = temp.Root,
                CurrentAppVersion = "1.0.0"
            });

            Assert.True(result.Success, result.ErrorMessage);
            Assert.Equal("2.0.0", result.CurrentVersion);
            Assert.Equal(newExe, await File.ReadAllBytesAsync(Path.Combine(temp.Root, "Vellum.exe")));
            Assert.Single(starter.Calls);
        }

        [Fact]
        public async Task Workflow_blocks_launch_when_update_and_rollback_fail()
        {
            using var temp = new TempApp();
            var updater = new FakeLauncherUpdateService
            {
                CheckResult = () => new LauncherUpdateStatus
                {
                    CurrentVersion = "1.0.0",
                    AvailableVersion = "2.0.0",
                    Phase = LauncherUpdatePhase.UpdateAvailable
                },
                DownloadResult = () => OperationResult.Ok(),
                AvailableManifest = new LauncherUpdateManifest
                {
                    Version = "2.0.0",
                    EntryPoint = "Vellum.exe",
                    Files =
                    {
                        new ManifestFile { Path = "Vellum.exe", Size = 1, Sha256 = new string('a', 64) }
                    }
                },
                ApplyRequest = new LauncherApplyRequest
                {
                    StagingDir = Path.Combine(temp.Root, "missing-staging"),
                    EntryPoint = "Vellum.exe",
                    Version = "2.0.0",
                    Files =
                    {
                        new ManifestFile { Path = "Vellum.exe", Size = 1, Sha256 = new string('a', 64) }
                    }
                }
            };
            var starter = new FakeProcessStarter();
            var integrity = new FakeLauncherIntegrityService();
            integrity.Results.Enqueue(new LauncherIntegrityResult
            {
                Passed = false,
                Problems = { "Vellum.exe is damaged." }
            });
            var workflow = CreateWorkflow(updater, integrity, starter);

            var result = await workflow.RunAsync(new BootstrapperRunRequest
            {
                AppDirectory = temp.Root,
                CurrentAppVersion = "1.0.0"
            });

            Assert.False(result.Success);
            Assert.False(result.CanLaunchCurrent);
            Assert.Empty(starter.Calls);
        }

        [Fact]
        public async Task Workflow_offers_previous_version_when_failed_update_left_files_intact()
        {
            using var temp = new TempApp();
            var updater = new FakeLauncherUpdateService
            {
                CheckResult = () => new LauncherUpdateStatus
                {
                    CurrentVersion = "1.0.0",
                    AvailableVersion = "2.0.0",
                    Phase = LauncherUpdatePhase.UpdateAvailable
                },
                DownloadResult = () => OperationResult.Ok(),
                AvailableManifest = new LauncherUpdateManifest
                {
                    Version = "2.0.0",
                    EntryPoint = "Vellum.exe",
                    Files =
                    {
                        new ManifestFile { Path = "Vellum.exe", Size = 1, Sha256 = new string('a', 64) }
                    }
                },
                ApplyRequest = new LauncherApplyRequest
                {
                    StagingDir = Path.Combine(temp.Root, "missing-staging"),
                    EntryPoint = "Vellum.exe",
                    Version = "2.0.0",
                    Files =
                    {
                        new ManifestFile { Path = "Vellum.exe", Size = 1, Sha256 = new string('a', 64) }
                    }
                }
            };
            var starter = new FakeProcessStarter();
            var workflow = CreateWorkflow(updater, new FakeLauncherIntegrityService(), starter);

            var result = await workflow.RunAsync(new BootstrapperRunRequest
            {
                AppDirectory = temp.Root,
                CurrentAppVersion = "1.0.0"
            });

            Assert.False(result.Success);
            Assert.True(result.CanLaunchCurrent);
            Assert.Empty(starter.Calls);
        }

        [Fact]
        public async Task Workflow_rolls_back_corrupted_files_and_starts_previous_version()
        {
            using var temp = new TempApp();
            var installer = new LauncherPackageInstaller(NullLogger<LauncherPackageInstaller>.Instance);
            var backupRoot = Path.Combine(PathHelper.LauncherUpdateDir,
                "workflow-test-" + Guid.NewGuid().ToString("N"));
            var oldExe = Encoding.UTF8.GetBytes("working build");
            await File.WriteAllBytesAsync(Path.Combine(temp.Root, "Vellum.exe"), oldExe);
            var oldHash = await HashHelper.ComputeSha256Async(Path.Combine(temp.Root, "Vellum.exe"));

            // First install records a known good manifest/state.
            var stagingV1 = Directory.CreateDirectory(Path.Combine(temp.Root, "stage1")).FullName;
            File.WriteAllBytes(Path.Combine(stagingV1, "Vellum.exe"), oldExe);
            var installV1 = await installer.ApplyAsync(new LauncherApplyRequest
            {
                InstallDir = temp.Root,
                StagingDir = stagingV1,
                BackupDir = Path.Combine(backupRoot, "backup1"),
                EntryPoint = "Vellum.exe",
                Version = "1.0.0",
                Relaunch = false,
                Files =
                {
                    new ManifestFile { Path = "Vellum.exe", Size = oldExe.Length, Sha256 = oldHash }
                }
            });
            Assert.True(installV1.Success, installV1.ErrorMessage);

            // Install a newer build, then damage it.
            var newExe = Encoding.UTF8.GetBytes("broken build");
            var stagingV2 = Directory.CreateDirectory(Path.Combine(temp.Root, "stage2")).FullName;
            File.WriteAllBytes(Path.Combine(stagingV2, "Vellum.exe"), newExe);
            var newHash = await HashHelper.ComputeSha256Async(Path.Combine(stagingV2, "Vellum.exe"));
            var installV2 = await installer.ApplyAsync(new LauncherApplyRequest
            {
                InstallDir = temp.Root,
                StagingDir = stagingV2,
                BackupDir = Path.Combine(backupRoot, "backup2"),
                EntryPoint = "Vellum.exe",
                Version = "2.0.0",
                Relaunch = false,
                Files =
                {
                    new ManifestFile { Path = "Vellum.exe", Size = newExe.Length, Sha256 = newHash }
                }
            });
            Assert.True(installV2.Success, installV2.ErrorMessage);
            await File.WriteAllTextAsync(Path.Combine(temp.Root, "Vellum.exe"), "corrupted");

            var starter = new FakeProcessStarter();
            var workflow = CreateWorkflow(new FakeLauncherUpdateService(),
                new LauncherIntegrityService(NullLogger<LauncherIntegrityService>.Instance), starter);

            var result = await workflow.RunAsync(new BootstrapperRunRequest
            {
                AppDirectory = temp.Root,
                CurrentAppVersion = "2.0.0"
            });

            Assert.True(result.Success, result.ErrorMessage);
            Assert.Single(starter.Calls);
            Assert.Equal(oldExe, await File.ReadAllBytesAsync(Path.Combine(temp.Root, "Vellum.exe")));
        }

        [Fact]
        public async Task Interrupted_launcher_transaction_is_recovered_before_launch()
        {
            using var temp = new TempApp();
            var oldExe = Encoding.UTF8.GetBytes("old");
            await File.WriteAllBytesAsync(Path.Combine(temp.Root, "Vellum.exe"), oldExe);
            // A directory with the target name makes the second move fail after the
            // first file was already replaced, leaving a realistic uncommitted journal.
            Directory.CreateDirectory(Path.Combine(temp.Root, "blocked.dll"));
            var newExe = Encoding.UTF8.GetBytes("new");
            var staging = Directory.CreateDirectory(Path.Combine(temp.Root, "stage-recover")).FullName;
            File.WriteAllBytes(Path.Combine(staging, "Vellum.exe"), newExe);
            File.WriteAllText(Path.Combine(staging, "blocked.dll"), "x");
            var exeHash = await HashHelper.ComputeSha256Async(Path.Combine(staging, "Vellum.exe"));
            var blockedHash = await HashHelper.ComputeSha256Async(Path.Combine(staging, "blocked.dll"));
            var backup = Path.Combine(PathHelper.LauncherUpdateDir,
                "recover-test-" + Guid.NewGuid().ToString("N"), "backup");

            var installer = new LauncherPackageInstaller(
                NullLogger<LauncherPackageInstaller>.Instance)
            {
                SuppressRollbackForTests = true
            };
            var failed = await installer.ApplyAsync(new LauncherApplyRequest
            {
                InstallDir = temp.Root,
                StagingDir = staging,
                BackupDir = backup,
                EntryPoint = "Vellum.exe",
                Version = "9.9.9",
                Relaunch = false,
                Files =
                {
                    new ManifestFile { Path = "Vellum.exe", Size = newExe.Length, Sha256 = exeHash },
                    new ManifestFile { Path = "blocked.dll", Size = 1, Sha256 = blockedHash }
                }
            });
            Assert.False(failed.Success);

            var fresh = new LauncherPackageInstaller(
                NullLogger<LauncherPackageInstaller>.Instance);
            var ignored = await fresh.RecoverInterruptedAsync(temp.Root, currentVersion: "1.2.3");
            Assert.Null(ignored);
            var recovered = await fresh.RecoverInterruptedAsync(temp.Root, currentVersion: "9.9.9");

            Assert.NotNull(recovered);
            Assert.True(recovered!.Success, recovered.ErrorMessage);
            Assert.Equal(oldExe, await File.ReadAllBytesAsync(Path.Combine(temp.Root, "Vellum.exe")));
        }

        [Fact]
        public async Task Workflow_marks_main_process_as_bootstrapped()
        {
            using var temp = new TempApp();
            var updater = new FakeLauncherUpdateService();
            var starter = new FakeProcessStarter();
            var workflow = CreateWorkflow(updater, new FakeLauncherIntegrityService(), starter);

            var result = await workflow.RunAsync(new BootstrapperRunRequest
            {
                AppDirectory = temp.Root,
                CurrentAppVersion = "1.0.0",
                LauncherArguments = { "--profile", "one" },
                MarkBootstrappedLaunch = true
            });

            Assert.True(result.Success);
            Assert.Single(starter.Calls);
            Assert.Equal(BootstrapperLaunchPolicy.ChildMarkerArgument, starter.Calls[0].Args[0]);
            Assert.Contains("--profile", starter.Calls[0].Args);
            Assert.True(BootstrapperHandshake.IsValidToken(starter.Calls[0].Input));
            Assert.True(BootstrapperLaunchPolicy.IsTrustedLaunch(
                starter.Calls[0].Input, starter.Calls[0].Args, "1"));
            Assert.DoesNotContain("--bootstrapped", starter.Calls[0].Args);
        }

        [Fact]
        public void Manual_bootstrapped_argument_or_environment_cannot_bypass_the_bootstrapper()
        {
            var args = new[] { "--bootstrapped", "--profile", "test" };
            Assert.False(BootstrapperLaunchPolicy.IsTrustedLaunch(null, args, "1"));
            Assert.False(BootstrapperLaunchPolicy.IsTrustedLaunch("", args, "1"));
            Assert.False(BootstrapperLaunchPolicy.IsTrustedLaunch("VELLUM_BOOTSTRAP_V1:bad", args, "1"));
            Assert.True(BootstrapperLaunchPolicy.IsTrustedLaunch(
                BootstrapperHandshake.CreateToken(), args, "1"));
        }

        [Fact]
        public void Health_timeout_never_starts_directly_without_an_explicit_choice()
        {
            var promptCalls = 0;
            var outcome = BootstrapperFallbackCoordinator.Run(
                () => new BootstrapperFallbackAttempt
                {
                    Started = true,
                    HealthFile = Path.Combine(Path.GetTempPath(),
                        "vellum-missing-health-" + Guid.NewGuid().ToString("N") + ".health"),
                    HasExited = () => false
                },
                TimeSpan.FromMilliseconds(250),
                _ =>
                {
                    promptCalls++;
                    return BootstrapperFallbackChoice.Close;
                });

            Assert.Equal(BootstrapperFallbackOutcome.Close, outcome);
            Assert.Equal(1, promptCalls);
            Assert.Equal(TimeSpan.FromSeconds(20), BootstrapperFallbackCoordinator.DefaultHealthTimeout);
        }

        [Fact]
        public void Bootstrapper_premature_exit_and_missing_health_require_a_user_choice()
        {
            var promptCalls = 0;
            var outcome = BootstrapperFallbackCoordinator.Run(
                () => new BootstrapperFallbackAttempt
                {
                    Started = true,
                    HealthFile = Path.Combine(Path.GetTempPath(), "never-created.health"),
                    HasExited = () => true
                },
                TimeSpan.FromSeconds(20),
                _ =>
                {
                    promptCalls++;
                    return BootstrapperFallbackChoice.StartDirectly;
                });

            Assert.Equal(BootstrapperFallbackOutcome.StartDirectly, outcome);
            Assert.Equal(1, promptCalls);
        }

        [Fact]
        public void Retry_starts_a_fresh_bootstrapper_attempt()
        {
            var starts = 0;
            var prompts = 0;
            var health = Path.Combine(Path.GetTempPath(),
                "vellum-retry-health-" + Guid.NewGuid().ToString("N") + ".health");
            var outcome = BootstrapperFallbackCoordinator.Run(
                () =>
                {
                    starts++;
                    if (starts == 2)
                        File.WriteAllText(health, "ok");
                    return new BootstrapperFallbackAttempt
                    {
                        Started = true,
                        HealthFile = health,
                        HasExited = () => false
                    };
                },
                TimeSpan.FromMilliseconds(250),
                _ =>
                {
                    prompts++;
                    return BootstrapperFallbackChoice.Retry;
                });

            Assert.Equal(BootstrapperFallbackOutcome.ExitToBootstrapper, outcome);
            Assert.Equal(2, starts);
            Assert.Equal(1, prompts);
            try { File.Delete(health); } catch { }
        }

        [Fact]
        public async Task Single_instance_guard_rejects_a_second_bootstrapper()
        {
            var name = @"Local\Vellum.Bootstrapper.Test." + Guid.NewGuid().ToString("N");
            using var first = SingleInstanceGuard.TryAcquire(name);
            Assert.NotNull(first);
            Assert.True(first!.Owned);

            var secondOwned = await Task.Run(() =>
            {
                using var second = SingleInstanceGuard.TryAcquire(name);
                return second?.Owned == true;
            });
            Assert.False(secondOwned);
        }

        [Fact]
        public async Task Self_update_replaces_bootstrapper_and_passes_skip_flag()
        {
            using var temp = new TempApp();
            var target = Path.Combine(temp.Root, "Vellum.Bootstrapper.exe");
            var source = Path.Combine(temp.Root, "download", "Vellum.Bootstrapper.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            var oldBytes = Encoding.UTF8.GetBytes("old bootstrapper");
            var newBytes = Encoding.UTF8.GetBytes("new bootstrapper");
            await File.WriteAllBytesAsync(target, oldBytes);
            await File.WriteAllBytesAsync(source, newBytes);
            var hash = await HashHelper.ComputeSha256Async(source);
            var starter = new FakeProcessStarter();
            var updater = new BootstrapperSelfUpdater(starter,
                NullLogger<BootstrapperSelfUpdater>.Instance);

            var result = await updater.ApplyAsync(new BootstrapperSelfUpdateRequest
            {
                TargetPath = target,
                NewExecutablePath = source,
                BackupPath = Path.Combine(temp.Root, "backup", "Vellum.Bootstrapper.exe.bak"),
                Version = "2.0.0",
                Size = newBytes.Length,
                Sha256 = hash,
                OriginalArguments = new List<string> { "--profile", "test" }
            });

            Assert.True(result.Success, result.ErrorMessage);
            Assert.Equal(newBytes, await File.ReadAllBytesAsync(target));
            Assert.Equal(oldBytes, await File.ReadAllBytesAsync(
                Path.Combine(temp.Root, "backup", "Vellum.Bootstrapper.exe.bak")));
            Assert.Single(starter.Calls);
            Assert.Contains("--skip-self-update", starter.Calls[0].Args);
            Assert.Contains("--profile", starter.Calls[0].Args);
        }

        [Fact]
        public async Task Self_update_waits_for_new_bootstrapper_health_marker()
        {
            using var temp = new TempApp();
            var target = Path.Combine(temp.Root, "Vellum.Bootstrapper.exe");
            var source = Path.Combine(temp.Root, "download", "Vellum.Bootstrapper.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            var newBytes = Encoding.UTF8.GetBytes("new bootstrapper");
            await File.WriteAllBytesAsync(target, Encoding.UTF8.GetBytes("old"));
            await File.WriteAllBytesAsync(source, newBytes);
            var hash = await HashHelper.ComputeSha256Async(source);
            var health = Path.Combine(temp.Root, "health.txt");
            var starter = new FakeProcessStarter
            {
                OnStart = (_, args) =>
                {
                    var index = args.ToList().IndexOf("--bootstrapper-health");
                    if (index >= 0 && index + 1 < args.Count)
                        File.WriteAllText(args[index + 1], "ok");
                }
            };
            var updater = new BootstrapperSelfUpdater(starter,
                NullLogger<BootstrapperSelfUpdater>.Instance);

            var result = await updater.ApplyAsync(new BootstrapperSelfUpdateRequest
            {
                TargetPath = target,
                NewExecutablePath = source,
                BackupPath = Path.Combine(temp.Root, "backup", "Vellum.Bootstrapper.exe.bak"),
                Version = "2.0.0",
                Size = newBytes.Length,
                Sha256 = hash,
                OriginalArguments = new List<string>(),
                HealthFile = health
            });

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(File.Exists(health));
            Assert.Single(starter.Calls);
        }

        [Fact]
        public async Task Self_update_failure_restores_backup_and_starts_old_bootstrapper()
        {
            using var temp = new TempApp();
            var target = Path.Combine(temp.Root, "Vellum.Bootstrapper.exe");
            var source = Path.Combine(temp.Root, "download", "Vellum.Bootstrapper.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            var oldBytes = Encoding.UTF8.GetBytes("old bootstrapper");
            var newBytes = Encoding.UTF8.GetBytes("new bootstrapper");
            await File.WriteAllBytesAsync(target, oldBytes);
            await File.WriteAllBytesAsync(source, newBytes);
            var hash = await HashHelper.ComputeSha256Async(source);
            var starter = new FakeProcessStarter
            {
                // The new target fails to start once; the restored old target starts.
                ShouldStart = call => call != 0
            };
            var updater = new BootstrapperSelfUpdater(starter,
                NullLogger<BootstrapperSelfUpdater>.Instance);

            var result = await updater.ApplyAsync(new BootstrapperSelfUpdateRequest
            {
                TargetPath = target,
                NewExecutablePath = source,
                BackupPath = Path.Combine(temp.Root, "backup", "Vellum.Bootstrapper.exe.bak"),
                Version = "2.0.0",
                Size = newBytes.Length,
                Sha256 = hash,
                OriginalArguments = new List<string>()
            });

            Assert.False(result.Success);
            Assert.True(result.RestoredBackup);
            Assert.True(result.Launched);
            Assert.Equal(oldBytes, await File.ReadAllBytesAsync(target));
            Assert.Equal(2, starter.Calls.Count);
        }

        private static BootstrapperWorkflow CreateWorkflow(ILauncherUpdateService updates,
            ILauncherIntegrityService integrity, IProcessStarter starter)
            => new(updates,
                new LauncherPackageInstaller(NullLogger<LauncherPackageInstaller>.Instance),
                integrity,
                starter,
                NullLogger<BootstrapperWorkflow>.Instance);

        private sealed class TempApp : IDisposable
        {
            public TempApp()
            {
                Root = Path.Combine(Path.GetTempPath(), "VellumBootWorkflow",
                    Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Root);
                File.WriteAllText(Path.Combine(Root, "Vellum.exe"), "launcher");
                File.WriteAllText(Path.Combine(Root, "Vellum.dll"), "dll");
                File.WriteAllText(Path.Combine(Root, "Vellum.Core.dll"), "core");
                File.WriteAllText(Path.Combine(Root, "Vellum.runtimeconfig.json"), "{}");
                File.WriteAllText(Path.Combine(Root, "Vellum.deps.json"), "{}");
            }

            public string Root { get; }

            public void Dispose()
            {
                try { Directory.Delete(Root, true); } catch { }
            }
        }
    }
}
