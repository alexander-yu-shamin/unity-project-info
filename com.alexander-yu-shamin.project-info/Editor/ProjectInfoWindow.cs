using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.VersionControl;
using UnityEngine;
using Debug = System.Diagnostics.Debug;

namespace ProjectInfo.Editor
{
    public class ProjectInfoWindow : EditorWindow
    {
        private enum WindowTitleType
        {
            Default,
            Workspace,
            ProjectName,
            GitBranch,
            WorkspaceAndBranch
        }

        private class InfoRow
        {
            public string Title { get; set; }
            public string Message { get; set; }
            public Color? Color { get; set; }
        }

        private class InfoGroup
        {
            public bool IsExpanded { get; set; }
            public List<InfoRow> InfoRows { get; set; }
        }

        private const string DefaultWindowTitle = "ProjectInfo";
        private const WindowTitleType DefaultWindowTitleType = WindowTitleType.Workspace;
        private const string WorkspaceKey = "Workspace:";
        private const string ProjectNameKey = "Project Name:";
        private const string GitBranchKey = "Branch:";

        private const string ProjectGroupKey = "Project Info";
        private const string GitGroupKey = "Git";
        private const string CompilationGroupKey = "Compilation";
        private const string PackageGroupKey = "Package";

        private string WindowTitleTypePrefsKey { get; } = $"{nameof(ProjectInfoWindow)}.{nameof(WindowTitleType)}";
        private Vector2 ScrollViewPosition { get; set; }
        private Dictionary<string, InfoGroup> EntityMap { get; set; } = new();
        private WindowTitleType CurrentWindowTitleType { get; set; } = WindowTitleType.Default;
        private UnityEditor.PackageManager.Requests.ListRequest PackageListRequest { get; set; }

        [MenuItem("Window/General/Project Info")]
        private static void ShowProjectInfoWindow()
        {
            var window = GetWindow<ProjectInfoWindow>(DefaultWindowTitle);
            window.autoRepaintOnSceneChange = true;
            window.Show();
        }

        private void OnGUI()
        {
            ScrollViewPosition = EditorGUILayout.BeginScrollView(ScrollViewPosition);
            DrawProjectInfo();
            EditorGUILayout.EndScrollView();
        }

        private void DrawProjectInfo()
        {
            EditorGUILayout.BeginVertical();

            CurrentWindowTitleType = DrawTitleTypeEnum(CurrentWindowTitleType);

            foreach (var (key, infoGroup) in EntityMap)
            {
                GUILayout.Space(5.0f);
                var newValue = EditorGUILayout.BeginFoldoutHeaderGroup(infoGroup.IsExpanded, key, menuAction: rect =>
                {
                    var menu = new GenericMenu();
                    menu.AddItem(new GUIContent("Copy"), false, () =>
                    {
                        var sb = new StringBuilder();
                        foreach (var infoRow in infoGroup.InfoRows)
                        {
                            sb.AppendLine($"{infoRow.Title}: {infoRow.Message}");
                        }

                        EditorGUIUtility.systemCopyBuffer = sb.ToString();
                    });
                    menu.ShowAsContext();
                });

                if (infoGroup.IsExpanded != newValue)
                {
                    infoGroup.IsExpanded = newValue;
                    EditorPrefs.SetBool(GetPrefsKey(key), infoGroup.IsExpanded);
                }

                if (infoGroup.IsExpanded)
                {
                    foreach (var infoRow in infoGroup.InfoRows)
                    {
                        DrawInfoRowField(infoRow);
                    }
                }

                EditorGUILayout.EndFoldoutHeaderGroup();
            }

            if (GUILayout.Button("Copy All"))
            {
                var sb = new StringBuilder();
                foreach (var (group, infoGroup) in EntityMap)
                {
                    sb.AppendLine(group);
                    foreach (var e in infoGroup.InfoRows)
                    {
                        sb.AppendLine($"  {e.Title} {e.Message}");
                    }

                    sb.AppendLine();
                }

                EditorGUIUtility.systemCopyBuffer = sb.ToString();
            }

            EditorGUILayout.EndVertical();
        }

        protected void OnEnable()
        {
            EditorApplication.projectChanged += OnProjectChanged;
            UpdateInfo();
        }

        private void OnProjectChanged()
        {
            UpdateInfo();
        }

        private void OnDisable()
        {
            EditorApplication.projectChanged -= OnProjectChanged;
        }

        private void OnFocus()
        {
            UpdateInfo();
        }

        private void UpdateInfo()
        {
            EntityMap.Clear();

            var dataDir = new DirectoryInfo(Application.dataPath);
            var projectDir = dataDir.Parent;
            var workspaceDir = projectDir?.Parent;
            EntityMap.Add(ProjectGroupKey,  new InfoGroup()
            {
                IsExpanded = EditorPrefs.GetBool(GetPrefsKey(ProjectGroupKey), true),
                InfoRows = new List<InfoRow>()
                {
                    new() { Title = WorkspaceKey, Message = workspaceDir?.Name ?? "Unknown" },
                    new() { Title = ProjectNameKey, Message = projectDir?.Name ?? "Unknown" },
                    new() { Title = "Company Name:", Message = Application.companyName },
                    new() { Title = "Product Version:", Message = Application.productName },
                    new() { Title = "Installer Name:", Message = Application.installerName },
                    new() { Title = "Identifier:", Message = Application.identifier },
                    new() { Title = "Unity Version:", Message = Application.unityVersion },
                    new() { Title = "Runtime Platform:", Message = Application.platform.ToString() },
                    new() { Title = "Build Target Platform:", Message = EditorUserBuildSettings.activeBuildTarget.ToString() },
                    new() { Title = "Data Path:", Message = Application.dataPath },
                    new() { Title = "Console Log Path:", Message = Application.consoleLogPath }
                }
            });

            EntityMap.Add(GitGroupKey, new InfoGroup() { IsExpanded = EditorPrefs.GetBool(GetPrefsKey(GitBranchKey), true), InfoRows = UpdateGitInfo(projectDir?.FullName)});

            var group = EditorUserBuildSettings.selectedBuildTargetGroup;
            var namedGroup = NamedBuildTarget.FromBuildTargetGroup(group);

            var compilations = new List<InfoRow>
            {
                new() { Title = "Active Build Target:", Message = namedGroup.TargetName },
                new() { Title = "Define Symbols:", Message = PlayerSettings.GetScriptingDefineSymbols(namedGroup) },
                new() { Title = "Compiler Arguments:", Message = string.Join(";", PlayerSettings.GetAdditionalCompilerArguments(namedGroup)) }
            };

            var test = UnityEditor.Compilation.CompilationPipeline.GetAssemblies();

            EntityMap.Add(CompilationGroupKey, new InfoGroup()
            {
                IsExpanded = EditorPrefs.GetBool(GetPrefsKey(CompilationGroupKey), true), InfoRows = compilations
            });

            RequestPackages();

            UpdateTitle();
        }

        private void RequestPackages()
        {
            PackageListRequest = UnityEditor.PackageManager.Client.List(offlineMode: true);
            EditorApplication.update += OnPackageListUpdate;
        }

        private void OnPackageListUpdate()
        {
            if (!PackageListRequest.IsCompleted)
            {
                return;
            }

            EditorApplication.update -= OnPackageListUpdate;

            if (PackageListRequest.Status == UnityEditor.PackageManager.StatusCode.Success)
            {
                var packages = new List<InfoRow>();
                foreach (var packageInfo in PackageListRequest.Result)
                {
                    packages.Add(new InfoRow
                    {
                        Title = packageInfo.name,
                        Message = packageInfo.version
                    });
                }

                if (EntityMap.ContainsKey(PackageGroupKey))
                {
                    EntityMap[PackageGroupKey] = new InfoGroup()
                    {
                        IsExpanded = EditorPrefs.GetBool(GetPrefsKey(PackageGroupKey), true), InfoRows = packages
                    };
                }
                else
                {
                    EntityMap.Add(PackageGroupKey, new InfoGroup()
                    {
                        IsExpanded = EditorPrefs.GetBool(GetPrefsKey(PackageGroupKey), true),
                        InfoRows = packages
                    });
                }
            }

            Repaint();
        }

        private string GetWindowName(WindowTitleType titleType)
        {
            return titleType switch
            {
                WindowTitleType.Workspace => GetMessage(ProjectGroupKey, WorkspaceKey, DefaultWindowTitle),
                WindowTitleType.ProjectName => GetMessage(ProjectGroupKey, ProjectNameKey, DefaultWindowTitle),
                WindowTitleType.GitBranch => GetMessage(GitGroupKey, GitBranchKey, DefaultWindowTitle),
                WindowTitleType.WorkspaceAndBranch => GetMessage(ProjectGroupKey, WorkspaceKey, DefaultWindowTitle)
                                                      + ":"
                                                      + GetMessage(GitGroupKey, GitBranchKey, DefaultWindowTitle),
                _ => DefaultWindowTitle,
            };
        }

        private string GetMessage(string group, string title, string defaultValue)
        {
            return EntityMap.First(kv => kv.Key == group).Value?.InfoRows?.FirstOrDefault(e => e.Title == title)?.Message ?? defaultValue;
        }

        private void UpdateTitle()
        {
            if (Enum.TryParse<WindowTitleType>(EditorPrefs.GetString(WindowTitleTypePrefsKey, DefaultWindowTitleType.ToString()), out var newWindowTitleType))
            {
                CurrentWindowTitleType = newWindowTitleType;
            }

            titleContent = new GUIContent(GetWindowName(CurrentWindowTitleType));
        }

        private string DrawInfoRowField(InfoRow infoRow)
        {
            if (infoRow == null)
            {
                return string.Empty;
            }

            var height = GUI.skin.textField.CalcHeight(new GUIContent(infoRow.Message), EditorGUIUtility.currentViewWidth);
            if (infoRow.Color.HasValue)
            {
                var prevColor = GUI.color;
                GUI.color = infoRow.Color.Value;
                var inputString = EditorGUILayout.TextField(infoRow.Title, infoRow.Message, GUILayout.Height(height));
                GUI.color = prevColor;
                return inputString;
            }
            else
            {
                var inputString = EditorGUILayout.TextField(infoRow.Title, infoRow.Message,GUILayout.Height(height));
                return inputString;
            }
        }

        private WindowTitleType DrawTitleTypeEnum(WindowTitleType titleType)
        {
            var newTitleType = (WindowTitleType)EditorGUILayout.EnumPopup("Window Name:", titleType);
            if (newTitleType != titleType)
            {
                EditorPrefs.SetString(WindowTitleTypePrefsKey, newTitleType.ToString());
                UpdateTitle();
            }

            return newTitleType;
        }

        private string GetPrefsKey(string key)
        {
            return nameof(ProjectInfoWindow) + key;
        }

        private List<InfoRow> UpdateGitInfo(string projectPath)
        {
            try
            {
                if (!string.IsNullOrEmpty(projectPath))
                {
                    var status = ExecuteGitCommand("status --porcelain", projectPath);
                    TryGetAheadBehind(projectPath, out var ahead, out var behind);
                    var (aheadAndBehind, aheadAndBehindColor) = FormatAheadBehind(ahead, behind);

                    var result = new List<InfoRow>()
                    {
                        new()
                        {
                            Title = GitBranchKey,
                            Message = ExecuteGitCommand("rev-parse --abbrev-ref HEAD", projectPath)
                        },
                        new()
                        {
                            Title = "Commit", 
                            Message = ExecuteGitCommand("rev-parse --short HEAD", projectPath)
                        },
                        new()
                        {
                            Title = "Status", 
                            Message = string.IsNullOrEmpty(status) ? "Clean" : "Dirty",
                            Color = string.IsNullOrEmpty(status) ? Color.green : Color.red
                        },
                        new()
                        {
                            Title = "Ahead/Behind", 
                            Message = aheadAndBehind, Color = aheadAndBehindColor
                        }
                    };

                    return result;
                }
            }
            catch
            {
                // ignored
            }

            return new List<InfoRow>()
            {
                new() { Title = GitBranchKey, Message = "No data" },
                new() { Title = "Commit", Message = "No data" },
                new() { Title = "Status", Message = "No data" },
                new() { Title = "Ahead/Behind", Message = "No data" }
            };
        }
        
        private (string, Color) FormatAheadBehind(int ahead, int behind)
        {
            if (ahead == -1 && behind == -1)
            {
                return ("No data", Color.grey);
            }

            if (ahead == 0 && behind == 0)
            {
                return ("Up to date", GUI.color);
            }

            var sb = new StringBuilder();
            if (ahead > 0)
            {
                sb.Append($"↑{ahead} ");
            }

            if (behind > 0)
            {
                sb.Append($"↓{behind}");
            }

            return (sb.ToString().Trim(), Color.yellow);
        }

        private bool TryGetAheadBehind(string projectPath, out int ahead, out int behind)
        {
            ahead = -1;
            behind = -1;

            var output = ExecuteGitCommand("rev-list --left-right --count HEAD...@{upstream}", projectPath);

            if (string.IsNullOrWhiteSpace(output))
            {
                return false;
            }

            var parts = output.Trim().Split('\t');
            if (parts.Length != 2)
            {
                return false;
            }

            return int.TryParse(parts[0], out ahead) && int.TryParse(parts[1], out behind);
        }

        private string ExecuteGitCommand(string args, string workingDirectory)
        {
            if (string.IsNullOrEmpty(workingDirectory))
            {
                return string.Empty;
            }

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = workingDirectory,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };

                using var process = Process.Start(startInfo);
                if (process == null)
                {
                    return string.Empty;
                }

                var output = process.StandardOutput.ReadToEnd();
                var error = process.StandardError.ReadToEnd();
                process.WaitForExit(2000);
                if (!string.IsNullOrEmpty(error) && string.IsNullOrWhiteSpace(output))
                {
                    return error.Trim();
                }

                return (output ?? string.Empty).Trim();
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}