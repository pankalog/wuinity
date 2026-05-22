using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace WUInity.Editor
{
    [InitializeOnLoad]
    public static class WUInityDevEnvironmentBootstrap
    {
        private const string StartupWuiKey = "WUInity.Dev.StartupWuiPath";
        private const string ProjLibKey = "WUInity.Dev.PROJ_LIB";
        private const string ProjDataKey = "WUInity.Dev.PROJ_DATA";
        private const string SumoHomeKey = "WUInity.Dev.SUMO_HOME";

        static WUInityDevEnvironmentBootstrap()
        {
            ApplyStoredEnvironment();
        }

        public static void ApplyStoredEnvironment()
        {
            ApplyEnvFromPref("WUINITY_STARTUP_WUI", StartupWuiKey);
            ApplyEnvFromPref("PROJ_LIB", ProjLibKey);
            ApplyEnvFromPref("PROJ_DATA", ProjDataKey);
            ApplyEnvFromPref("SUMO_HOME", SumoHomeKey);
        }

        private static void ApplyEnvFromPref(string envVar, string prefKey)
        {
            string value = EditorPrefs.GetString(prefKey, string.Empty);
            if (!string.IsNullOrWhiteSpace(value))
            {
                Environment.SetEnvironmentVariable(envVar, value.Trim());
            }
        }
    }

    public class WUInityDevSettingsWindow : EditorWindow
    {
        private const string StartupWuiKey = "WUInity.Dev.StartupWuiPath";
        private const string ProjLibKey = "WUInity.Dev.PROJ_LIB";
        private const string ProjDataKey = "WUInity.Dev.PROJ_DATA";
        private const string SumoHomeKey = "WUInity.Dev.SUMO_HOME";

        private string _startupWuiPath;
        private string _projLib;
        private string _projData;
        private string _sumoHome;

        [MenuItem("WUInity/Dev/Configuration")]
        public static void OpenWindow()
        {
            WUInityDevSettingsWindow window = GetWindow<WUInityDevSettingsWindow>("WUInity Dev Config");
            window.minSize = new Vector2(700f, 250f);
            window.Show();
        }

        private void OnEnable()
        {
            _startupWuiPath = EditorPrefs.GetString(StartupWuiKey, string.Empty);
            _projLib = EditorPrefs.GetString(ProjLibKey, string.Empty);
            _projData = EditorPrefs.GetString(ProjDataKey, string.Empty);
            _sumoHome = EditorPrefs.GetString(SumoHomeKey, string.Empty);
        }

        private void OnGUI()
        {
            GUILayout.Label("Startup WUI", EditorStyles.boldLabel);
            DrawPathRow(ref _startupWuiPath, "Pick WUI", PickStartupWui);

            GUILayout.Space(8f);
            GUILayout.Label("Environment Variables", EditorStyles.boldLabel);
            DrawLabeledTextField("PROJ_LIB", ref _projLib);
            DrawLabeledTextField("PROJ_DATA", ref _projData);
            DrawLabeledTextField("SUMO_HOME", ref _sumoHome);

            GUILayout.Space(12f);
            if (GUILayout.Button("Save + Apply To Unity Process", GUILayout.Height(30f)))
            {
                SaveAndApply();
            }

            GUILayout.Space(8f);
            GUILayout.Label("Current Runtime Values", EditorStyles.boldLabel);
            GUILayout.Label("WUINITY_STARTUP_WUI = " + (Environment.GetEnvironmentVariable("WUINITY_STARTUP_WUI") ?? "<empty>"));
            GUILayout.Label("PROJ_LIB = " + (Environment.GetEnvironmentVariable("PROJ_LIB") ?? "<empty>"));
            GUILayout.Label("PROJ_DATA = " + (Environment.GetEnvironmentVariable("PROJ_DATA") ?? "<empty>"));
            GUILayout.Label("SUMO_HOME = " + (Environment.GetEnvironmentVariable("SUMO_HOME") ?? "<empty>"));
        }

        private void DrawPathRow(ref string value, string buttonText, Action onPick)
        {
            GUILayout.BeginHorizontal();
            value = EditorGUILayout.TextField(value);
            if (GUILayout.Button(buttonText, GUILayout.Width(120f)))
            {
                onPick();
            }
            GUILayout.EndHorizontal();
        }

        private void DrawLabeledTextField(string label, ref string value)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(120f));
            value = EditorGUILayout.TextField(value);
            GUILayout.EndHorizontal();
        }

        private void PickStartupWui()
        {
            string repoRoot = Path.GetFullPath(Path.Combine(Application.dataPath, "..", ".."));
            string examples = Path.Combine(repoRoot, "Examples");
            string initialDirectory = Directory.Exists(examples) ? examples : repoRoot;
            string picked = EditorUtility.OpenFilePanel("Pick startup WUI file", initialDirectory, "wui");
            if (!string.IsNullOrWhiteSpace(picked))
            {
                _startupWuiPath = picked;
            }
        }

        private void SaveAndApply()
        {
            EditorPrefs.SetString(StartupWuiKey, _startupWuiPath ?? string.Empty);
            EditorPrefs.SetString(ProjLibKey, _projLib ?? string.Empty);
            EditorPrefs.SetString(ProjDataKey, _projData ?? string.Empty);
            EditorPrefs.SetString(SumoHomeKey, _sumoHome ?? string.Empty);
            WUInityDevEnvironmentBootstrap.ApplyStoredEnvironment();
            Debug.Log("[WUInity Dev Config] Saved and applied environment settings.");
        }
    }
}
