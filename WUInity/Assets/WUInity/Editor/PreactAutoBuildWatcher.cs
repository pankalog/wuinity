using System;
using System.Diagnostics;
using System.IO;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace WUInity.Editor
{
    [InitializeOnLoad]
    public static class PreactAutoBuildWatcher
    {
        private const string EnabledPrefKey = "WUInity.PreactAutoBuild.Enabled";
        private const string ConfigPrefKey = "WUInity.PreactAutoBuild.Config";
        private const string ToggleMenuPath = "WUInity/PREACT/Auto-build on PREACT changes";
        private const string DebugConfigMenuPath = "WUInity/PREACT/Use Debug PREACT DLL";
        private const string ReleaseConfigMenuPath = "WUInity/PREACT/Use Release PREACT DLL";

        private static FileSystemWatcher _sourceWatcher;
        private static FileSystemWatcher _projectWatcher;
        private static bool _pendingBuild;
        private static bool _buildInProgress;
        private static bool _pendingAssetRefresh;
        private static DateTime _lastChangeUtc;
        private static string _lastTriggerReason = "startup";
        private static Process _buildProcess;

        private static readonly string RepoRoot;
        private static readonly string PreactProjectPath;
        private static readonly string PreactSourcePath;
        private static readonly string UnityPreactRootPath;
        private static readonly string UnityProjectRootPath;
        private static readonly string DotnetPath;

        static PreactAutoBuildWatcher()
        {
            RepoRoot = ResolveRepoRoot();
            PreactProjectPath = Path.Combine(RepoRoot, "PREACT", "PREACTcore", "PREACTcore.csproj");
            PreactSourcePath = Path.Combine(RepoRoot, "PREACT", "PREACTcore", "Source");
            UnityProjectRootPath = Path.Combine(RepoRoot, "WUInity");
            UnityPreactRootPath = Path.Combine(RepoRoot, "WUInity", "Assets", "PREACT");
            DotnetPath = ResolveDotnetPath();

            EditorApplication.update += Update;
            EditorApplication.quitting += Stop;
            AssemblyReloadEvents.beforeAssemblyReload += Stop;

            UpdateMenuCheckmark();

            if (Enabled)
            {
                StartWatchers();
            }

            SyncNativePluginImportSettings();

            if (string.IsNullOrEmpty(DotnetPath))
            {
                Debug.LogWarning("[PREACT AutoBuild] dotnet was not found. Set PATH or install dotnet to /opt/homebrew/bin.");
            }
        }
        private static bool Enabled
        {
            get => EditorPrefs.GetBool(EnabledPrefKey, true);
            set => EditorPrefs.SetBool(EnabledPrefKey, value);
        }

        [MenuItem(ToggleMenuPath)]
        private static void ToggleAutoBuild()
        {
            Enabled = !Enabled;

            if (Enabled)
            {
                StartWatchers();
                QueueBuild("manual enable");
                Debug.Log("[PREACT AutoBuild] Enabled.");
            }
            else
            {
                StopWatchers();
                Debug.Log("[PREACT AutoBuild] Disabled.");
            }

            UpdateMenuCheckmark();
        }

        [MenuItem(DebugConfigMenuPath)]
        private static void UseDebugConfig()
        {
            EditorPrefs.SetString(ConfigPrefKey, "Debug");
            UpdateMenuCheckmark();
            SyncNativePluginImportSettings();
            QueueBuild("config switched to Debug");
            Debug.Log("[PREACT AutoBuild] Using Debug PREACT output.");
        }

        [MenuItem(DebugConfigMenuPath, true)]
        private static bool UseDebugConfigValidate()
        {
            UpdateMenuCheckmark();
            return true;
        }

        [MenuItem(ReleaseConfigMenuPath)]
        private static void UseReleaseConfig()
        {
            EditorPrefs.SetString(ConfigPrefKey, "Release");
            UpdateMenuCheckmark();
            SyncNativePluginImportSettings();
            QueueBuild("config switched to Release");
            Debug.Log("[PREACT AutoBuild] Using Release PREACT output.");
        }

        [MenuItem(ReleaseConfigMenuPath, true)]
        private static bool UseReleaseConfigValidate()
        {
            UpdateMenuCheckmark();
            return true;
        }

        [MenuItem(ToggleMenuPath, true)]
        private static bool ToggleAutoBuildValidate()
        {
            UpdateMenuCheckmark();
            return true;
        }

        [MenuItem("WUInity/PREACT/Build PREACT now")]
        private static void BuildNow()
        {
            QueueBuild("manual build");
        }

        private static void UpdateMenuCheckmark()
        {
            Menu.SetChecked(ToggleMenuPath, Enabled);
            string config = BuildConfig;
            Menu.SetChecked(DebugConfigMenuPath, config == "Debug");
            Menu.SetChecked(ReleaseConfigMenuPath, config == "Release");
        }

        private static string BuildConfig
        {
            get
            {
                string envConfig = Environment.GetEnvironmentVariable("PREACT_UNITY_CONFIG");
                if (IsValidConfig(envConfig))
                {
                    return string.Equals(envConfig, "Release", StringComparison.OrdinalIgnoreCase) ? "Release" : "Debug";
                }

                string prefConfig = EditorPrefs.GetString(ConfigPrefKey, "Debug");
                if (IsValidConfig(prefConfig))
                {
                    return string.Equals(prefConfig, "Release", StringComparison.OrdinalIgnoreCase) ? "Release" : "Debug";
                }

                return "Debug";
            }
        }

        private static bool IsValidConfig(string config)
        {
            return string.Equals(config, "Debug", StringComparison.OrdinalIgnoreCase) || string.Equals(config, "Release", StringComparison.OrdinalIgnoreCase);
        }

        private static string UnityPreactOutputPath => Path.Combine(UnityPreactRootPath, BuildConfig, "netstandard2.1");

        private static void SyncNativePluginImportSettings()
        {
            if (!Directory.Exists(UnityPreactRootPath) || !Directory.Exists(UnityProjectRootPath))
            {
                return;
            }

            string activeConfig = BuildConfig;
            string[] configs = { "Debug", "Release" };
            int changed = 0;

            for (int i = 0; i < configs.Length; i++)
            {
                string config = configs[i];
                bool editorEnabled = string.Equals(config, activeConfig, StringComparison.Ordinal);

                string gdalPath = Path.Combine(UnityPreactRootPath, config, "netstandard2.1", "ThirdParty", "GDAL", "osx-arm64");
                string sumoPath = Path.Combine(UnityPreactRootPath, config, "netstandard2.1", "ThirdParty", "SUMO", "osx-arm64");
                string preactCorePath = Path.Combine(UnityPreactRootPath, config, "netstandard2.1", "PREACTcore.dll");

                changed += ApplyImporterStateForFolder(gdalPath, editorEnabled);
                changed += ApplyImporterStateForFolder(sumoPath, editorEnabled);
                changed += ApplyImporterStateForFile(preactCorePath, editorEnabled);
            }

            if (changed > 0)
            {
                AssetDatabase.SaveAssets();
                Debug.Log($"[PREACT AutoBuild] Updated native plugin import settings for {changed} file(s). Active config: {activeConfig}.");
            }
        }

        private static int ApplyImporterStateForFolder(string absoluteFolderPath, bool editorEnabled)
        {
            if (!Directory.Exists(absoluteFolderPath))
            {
                return 0;
            }

            string[] dylibs = Directory.GetFiles(absoluteFolderPath, "*.dylib", SearchOption.TopDirectoryOnly);
            int changed = 0;

            for (int i = 0; i < dylibs.Length; i++)
            {
                string absoluteFile = dylibs[i];
                string normalizedProjectRoot = UnityProjectRootPath.Replace('\\', '/').TrimEnd('/');
                string normalizedFile = absoluteFile.Replace('\\', '/');

                if (!normalizedFile.StartsWith(normalizedProjectRoot + "/", StringComparison.Ordinal))
                {
                    continue;
                }

                string assetPath = normalizedFile.Substring(normalizedProjectRoot.Length + 1);
                PluginImporter importer = AssetImporter.GetAtPath(assetPath) as PluginImporter;
                if (importer == null)
                {
                    continue;
                }

                bool importerChanged = false;
                if (importer.GetCompatibleWithEditor() != editorEnabled)
                {
                    importer.SetCompatibleWithEditor(editorEnabled);
                    importerChanged = true;
                }

                if (!importer.GetCompatibleWithPlatform(BuildTarget.StandaloneOSX))
                {
                    importer.SetCompatibleWithPlatform(BuildTarget.StandaloneOSX, true);
                    importerChanged = true;
                }

                if (importerChanged)
                {
                    importer.SaveAndReimport();
                    changed++;
                }
            }

            return changed;
        }

        private static int ApplyImporterStateForFile(string absoluteFilePath, bool editorEnabled)
        {
            if (!File.Exists(absoluteFilePath))
            {
                return 0;
            }

            string normalizedProjectRoot = UnityProjectRootPath.Replace('\\', '/').TrimEnd('/');
            string normalizedFile = absoluteFilePath.Replace('\\', '/');
            if (!normalizedFile.StartsWith(normalizedProjectRoot + "/", StringComparison.Ordinal))
            {
                return 0;
            }

            string assetPath = normalizedFile.Substring(normalizedProjectRoot.Length + 1);
            PluginImporter importer = AssetImporter.GetAtPath(assetPath) as PluginImporter;
            if (importer == null)
            {
                return 0;
            }

            bool importerChanged = false;
            if (importer.GetCompatibleWithEditor() != editorEnabled)
            {
                importer.SetCompatibleWithEditor(editorEnabled);
                importerChanged = true;
            }

            if (importer.GetCompatibleWithPlatform(BuildTarget.StandaloneOSX) != editorEnabled)
            {
                importer.SetCompatibleWithPlatform(BuildTarget.StandaloneOSX, editorEnabled);
                importerChanged = true;
            }

            if (importerChanged)
            {
                importer.SaveAndReimport();
                return 1;
            }

            return 0;
        }

        private static void StartWatchers()
        {
            StopWatchers();

            if (!File.Exists(PreactProjectPath) || !Directory.Exists(PreactSourcePath))
            {
                Debug.LogWarning($"[PREACT AutoBuild] Missing PREACT paths. RepoRoot: {RepoRoot}, Project: {PreactProjectPath}, Source: {PreactSourcePath}, Application.dataPath: {Application.dataPath}");
                return;
            }

            _sourceWatcher = CreateWatcher(PreactSourcePath, "*.cs");
            _projectWatcher = CreateWatcher(Path.GetDirectoryName(PreactProjectPath), Path.GetFileName(PreactProjectPath));

            Debug.Log("[PREACT AutoBuild] Watching PREACT source for changes.");
        }

        private static FileSystemWatcher CreateWatcher(string directoryPath, string filter)
        {
            FileSystemWatcher watcher = new FileSystemWatcher(directoryPath, filter)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size,
                EnableRaisingEvents = true
            };

            watcher.Changed += OnSourceChanged;
            watcher.Created += OnSourceChanged;
            watcher.Deleted += OnSourceChanged;
            watcher.Renamed += OnSourceRenamed;
            watcher.Error += OnWatcherError;

            return watcher;
        }

        private static void StopWatchers()
        {
            DisposeWatcher(ref _sourceWatcher);
            DisposeWatcher(ref _projectWatcher);
        }

        private static void DisposeWatcher(ref FileSystemWatcher watcher)
        {
            if (watcher == null)
            {
                return;
            }

            watcher.EnableRaisingEvents = false;
            watcher.Changed -= OnSourceChanged;
            watcher.Created -= OnSourceChanged;
            watcher.Deleted -= OnSourceChanged;
            watcher.Renamed -= OnSourceRenamed;
            watcher.Error -= OnWatcherError;
            watcher.Dispose();
            watcher = null;
        }

        private static void OnSourceChanged(object sender, FileSystemEventArgs args)
        {
            QueueBuild($"{args.ChangeType}: {args.FullPath}");
        }

        private static void OnSourceRenamed(object sender, RenamedEventArgs args)
        {
            QueueBuild($"Renamed: {args.OldFullPath} -> {args.FullPath}");
        }

        private static void OnWatcherError(object sender, ErrorEventArgs args)
        {
            Exception ex = args.GetException();
            if (ex != null)
            {
                Debug.LogWarning($"[PREACT AutoBuild] File watcher error: {ex.Message}");
            }
            QueueBuild("watcher error recovery");
        }

        private static void QueueBuild(string reason)
        {
            _pendingBuild = true;
            _lastChangeUtc = DateTime.UtcNow;
            _lastTriggerReason = reason;
        }

        private static void Update()
        {
            if (_pendingAssetRefresh && !EditorApplication.isPlayingOrWillChangePlaymode && !EditorApplication.isCompiling && !EditorApplication.isUpdating)
            {
                _pendingAssetRefresh = false;
                Debug.Log("[PREACT AutoBuild] Refreshing Unity assets.");
                AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
            }

            if (!Enabled || !_pendingBuild || _buildInProgress)
            {
                return;
            }

            if ((DateTime.UtcNow - _lastChangeUtc).TotalMilliseconds < 700)
            {
                return;
            }

            if (EditorApplication.isCompiling || EditorApplication.isUpdating || BuildPipeline.isBuildingPlayer)
            {
                return;
            }

            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                return;
            }

            StartBuildProcess();
        }

        private static void StartBuildProcess()
        {
            _pendingBuild = false;

            if (!File.Exists(PreactProjectPath))
            {
                Debug.LogWarning($"[PREACT AutoBuild] Project not found: {PreactProjectPath}");
                return;
            }

            if (string.IsNullOrEmpty(DotnetPath))
            {
                Debug.LogError("[PREACT AutoBuild] Cannot build because dotnet was not found.");
                return;
            }

            Directory.CreateDirectory(UnityPreactOutputPath);

            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = DotnetPath,
                Arguments = $"build \"{PreactProjectPath}\" -c Debug -o \"{UnityPreactOutputPath}\" /nologo /verbosity:minimal",
                WorkingDirectory = RepoRoot,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = false,
                RedirectStandardError = false
            };

            try
            {
                _buildProcess = new Process
                {
                    StartInfo = startInfo,
                    EnableRaisingEvents = true
                };
                _buildProcess.Exited += OnBuildExited;

                _buildInProgress = true;
                _buildProcess.Start();

                Debug.Log($"[PREACT AutoBuild] Building PREACTcore ({_lastTriggerReason}).");
            }
            catch (Exception e)
            {
                _buildInProgress = false;
                Debug.LogError($"[PREACT AutoBuild] Failed to start dotnet build: {e.Message}");
            }
        }

        private static void OnBuildExited(object sender, EventArgs e)
        {
            int exitCode = 1;

            if (_buildProcess != null)
            {
                exitCode = _buildProcess.ExitCode;
                _buildProcess.Exited -= OnBuildExited;
                _buildProcess.Dispose();
                _buildProcess = null;
            }

            EditorApplication.delayCall += () =>
            {
                _buildInProgress = false;

                if (exitCode == 0)
                {
                    if (EditorApplication.isPlayingOrWillChangePlaymode)
                    {
                        _pendingAssetRefresh = true;
                        Debug.Log("[PREACT AutoBuild] Build succeeded. Asset refresh deferred until play mode ends.");
                    }
                    else
                    {
                        Debug.Log("[PREACT AutoBuild] Build succeeded. Refreshing Unity assets.");
                        AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
                    }
                }
                else
                {
                    Debug.LogError($"[PREACT AutoBuild] Build failed with exit code {exitCode}. Check PREACT build output.");
                }
            };
        }

        private static void Stop()
        {
            StopWatchers();

            if (_buildProcess != null)
            {
                try
                {
                    if (!_buildProcess.HasExited)
                    {
                        _buildProcess.Kill();
                    }
                }
                catch
                {
                }

                _buildProcess.Dispose();
                _buildProcess = null;
            }

            _buildInProgress = false;
            _pendingBuild = false;
            _pendingAssetRefresh = false;
        }

        private static string ResolveDotnetPath()
        {
            string[] candidates =
            {
                "dotnet",
                "/opt/homebrew/bin/dotnet",
                "/usr/local/share/dotnet/dotnet"
            };

            for (int i = 0; i < candidates.Length; i++)
            {
                string candidate = candidates[i];
                try
                {
                    ProcessStartInfo psi = new ProcessStartInfo
                    {
                        FileName = candidate,
                        Arguments = "--version",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };

                    using (Process process = Process.Start(psi))
                    {
                        if (process == null)
                        {
                            continue;
                        }

                        if (!process.WaitForExit(1500))
                        {
                            try
                            {
                                process.Kill();
                            }
                            catch
                            {
                            }
                            continue;
                        }

                        if (process.ExitCode == 0)
                        {
                            return candidate;
                        }
                    }
                }
                catch
                {
                }
            }

            return string.Empty;
        }

        private static string ResolveRepoRoot()
        {
            string current = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

            for (int i = 0; i < 6; i++)
            {
                string preactProject = Path.Combine(current, "PREACT", "PREACTcore", "PREACTcore.csproj");
                string wuinityFolder = Path.Combine(current, "WUInity");

                if (File.Exists(preactProject) && Directory.Exists(wuinityFolder))
                {
                    return current;
                }

                DirectoryInfo parent = Directory.GetParent(current);
                if (parent == null)
                {
                    break;
                }

                current = parent.FullName;
            }

            return Path.GetFullPath(Path.Combine(Application.dataPath, "..", ".."));
        }
    }
}
