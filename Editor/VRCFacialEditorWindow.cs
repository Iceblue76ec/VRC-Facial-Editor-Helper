using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using VRC.SDK3.Avatars.Components;

namespace Iceblue76ec.VRCTools
{
    /// <summary>
    /// VRC捏脸辅助工具 - Shape Key 批量调整编辑器窗口
    /// 挂载路径: Tools/Iceblue76ec/VRC捏脸辅助界面
    /// </summary>
    public class VRCFacialEditorWindow : EditorWindow
    {
        // ==================== 常量 ====================
        private const string VERSION = "0.2.0";
        private const float WINDOW_MIN_WIDTH = 800f;
        private const float WINDOW_MIN_HEIGHT = 550f;
        private const string CONFIG_ROOT = "Assets/iceblue76ec/configs";
        private const string TRANSLATION_FILENAME = "translations.json";
        private const string LOG_FILENAME = "editor_log.txt";
        private const int MAX_LOG_COUNT = 1500;
        private const float LOG_AUTO_SAVE_INTERVAL = 15f;

        // ==================== 状态 ====================
        private SkinnedMeshRenderer _targetSkinnedMesh;
        private GameObject _selectedAvatar;
        private string _currentMeshGuid = "";
        private string _currentMeshName = "";

        // 分组与形态键
        private List<BlendshapeGroup> _groups = new();
        private Dictionary<string, float> _blendshapeValues = new();
        private Dictionary<string, float> _blendshapeBackup = new();

        // 翻译
        private Dictionary<string, string> _translations = new();
        private float _translationSaveTime = 0f; // 触发延迟保存的时间点

        // 搜索
        private string _searchText = "";
        private bool _showModifiedOnly = false;
        private List<BlendshapeGroup> _filteredGroups = new();

        // 预设（已移除）

// 日志
        private List<LogEntry> _logs = new();
        private Vector2 _logScrollPosition;
        private LogLevel _logFilter = LogLevel.All;
        private float _lastLogSaveTime = 0f;

        // 上次选中的对象（用于避免重复加载）
        private GameObject _lastSelectedAvatar;

        // 标签页
        private int _selectedTab = 0;
        private static readonly string[] _tabNames = { "捏脸面板", "日志面板" };

        // 上次导出动画的目录（用于导入时默认位置）
        private string _lastExportDir = "";

        // 配置目录缓存（避免频繁遍历文件系统）
        private string[] _cachedConfigDirs = null;
        private float _configDirCacheTime = 0f;
        private const float CONFIG_DIR_CACHE_INTERVAL = 5f;

        // ==================== 初始化 ====================
        [MenuItem("Tools/Iceblue76ec/VRC捏脸辅助界面")]
        public static void ShowWindow()
        {
            var window = GetWindow<VRCFacialEditorWindow>("VRC捏脸辅助");
            window.minSize = new Vector2(WINDOW_MIN_WIDTH, WINDOW_MIN_HEIGHT);
        }

        private void OnEnable()
        {
            _lastLogSaveTime = Time.realtimeSinceStartup; // 防止首次 OnInspectorUpdate 在 LoadLogs 前清空日志
            LoadLogs();
            AddLog(LogLevel.Info, "捏脸辅助工具已启动");
            Selection.selectionChanged += OnSelectionChanged;
            TryAutoDetectVRChatAvatar();
        }

        private void OnDisable()
        {
            Selection.selectionChanged -= OnSelectionChanged;
            SaveTranslations();
            SaveLogs();
        }

        private void OnInspectorUpdate()
        {
            // 延迟保存：翻译修改后等待1秒再保存，避免频繁IO
            if (_translationSaveTime > 0f && Time.realtimeSinceStartup >= _translationSaveTime)
            {
                _translationSaveTime = 0f;
                SaveTranslations();
            }

            // 日志定时自动保存
            if (Time.realtimeSinceStartup - _lastLogSaveTime >= LOG_AUTO_SAVE_INTERVAL)
            {
                _lastLogSaveTime = Time.realtimeSinceStartup;
                SaveLogs();
            }
        }

        // ==================== 核心逻辑 ====================
        private void OnSelectionChanged()
        {
            TryAutoDetectVRChatAvatar();
        }

        private void TryAutoDetectVRChatAvatar()
        {
            var selected = Selection.activeGameObject;
            if (selected == null) return;

            // 1. 获取当前选中项所属的 Avatar 根节点
            //    使用 VRCAvatarDescriptor 直接查找（比反射/类型名匹配更安全可靠）
            var descriptor = selected.GetComponentInParent<VRCAvatarDescriptor>();
            GameObject currentRoot = descriptor != null ? descriptor.gameObject : null;

            // 如果选中的就是上次的，跳过（避免重复刷新）
            if (currentRoot != null && currentRoot == _lastSelectedAvatar && _targetSkinnedMesh != null)
            {
                return;
            }

            // 2. 如果没选中具体的 Avatar 子对象，回退到选中对象本身
            if (currentRoot == null)
            {
                // 检查选中对象本身或其父级
                descriptor = selected.GetComponent<VRCAvatarDescriptor>();
                if (descriptor != null)
                {
                    currentRoot = descriptor.gameObject;
                }
            }

            if (currentRoot == null)
            {
                AddLog(LogLevel.Warning, "未检测到 VRC Avatar Descriptor，请直接选中 Avatar 或其子对象");
                return;
            }

            // 3. 定位 Body
            _selectedAvatar = currentRoot;
            _lastSelectedAvatar = currentRoot;

            var bodyObject = FindBodyObject(currentRoot);
            if (bodyObject != null)
            {
                _targetSkinnedMesh = bodyObject.GetComponent<SkinnedMeshRenderer>();
                RefreshMeshInfo();
                LoadTranslations();
                RefreshBlendshapes();
                AddLog(LogLevel.Info, $"自动定位 Body 成功: {bodyObject.name}");
            }
        }

        private void RefreshMeshInfo()
        {
            if (_targetSkinnedMesh == null || _targetSkinnedMesh.sharedMesh == null)
            {
                _currentMeshGuid = "";
                _currentMeshName = "";
                return;
            }

            var mesh = _targetSkinnedMesh.sharedMesh;
            _currentMeshName = mesh.name;

            // 获取 mesh 的 GUID
            string assetPath = AssetDatabase.GetAssetPath(mesh);
            if (!string.IsNullOrEmpty(assetPath))
            {
                _currentMeshGuid = AssetDatabase.AssetPathToGUID(assetPath);
            }
            else
            {
                // 如果是内置资源，使用 hash 作为 fallback
                _currentMeshGuid = mesh.GetInstanceID().ToString();
            }

            AddLog(LogLevel.Info, $"Mesh GUID: {_currentMeshGuid}");
        }

        private GameObject FindBodyObject(GameObject avatarRoot)
        {
            // 策略 A: 从 Descriptor 的 Viseme 绑定中获取（最准）
            var descriptor = avatarRoot.GetComponent<VRCAvatarDescriptor>();
            if (descriptor != null && descriptor.VisemeSkinnedMesh != null)
            {
                return descriptor.VisemeSkinnedMesh.gameObject;
            }

            // 策略 B: 常见的命名匹配
            var allSMRs = avatarRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true);

            // 1. 优先找完全叫 "Body" 的
            var body = allSMRs.FirstOrDefault(s => s.name.Equals("Body", System.StringComparison.OrdinalIgnoreCase));
            if (body != null) return body.gameObject;

            // 2. 其次找包含 "Body" 且 BlendShape 最多的（通常是主网格）
            var bestMatch = allSMRs
                .Where(s => s.name.ToLower().Contains("body"))
                .OrderByDescending(s => s.sharedMesh != null ? s.sharedMesh.blendShapeCount : 0)
                .FirstOrDefault();

            return bestMatch?.gameObject;
        }

        private void RefreshBlendshapes()
        {
            if (_targetSkinnedMesh == null || _targetSkinnedMesh.sharedMesh == null)
                return;

            _groups.Clear();
            _blendshapeValues.Clear();
            _blendshapeBackup.Clear();

            var mesh = _targetSkinnedMesh.sharedMesh;
            int count = mesh.blendShapeCount;

            BlendshapeGroup currentGroup = null;

            for (int i = 0; i < count; i++)
            {
                string fullName = mesh.GetBlendShapeName(i);
                float value = _targetSkinnedMesh.GetBlendShapeWeight(i);

                if (TryParseGroupMarker(fullName, out string groupName))
                {
                    currentGroup = new BlendshapeGroup
                    {
                        name = groupName,
                        translation = GetTranslation(groupName)
                    };
                    _groups.Add(currentGroup);
                    continue;
                }

                var blendshape = new BlendshapeInfo
                {
                    name = fullName,
                    displayName = GetDisplayName(fullName),
                    translation = GetTranslation(fullName),
                    value = value,
                    index = i
                };

                _blendshapeValues[fullName] = value;
                _blendshapeBackup[fullName] = value;

                if (currentGroup != null)
                    currentGroup.items.Add(blendshape);
                else
                {
                    if (_groups.Count == 0 || _groups[_groups.Count - 1].items.Count > 0 || _groups[_groups.Count - 1].name != "未分类")
                    {
                        currentGroup = new BlendshapeGroup { name = "未分类" };
                        _groups.Add(currentGroup);
                    }
                    currentGroup.items.Add(blendshape);
                }
            }

            ApplyFilter();
            AddLog(LogLevel.Info, $"已加载 {_groups.Sum(g => g.items.Count)} 个形态键，{_groups.Count} 个分组");
        }

        private bool TryParseGroupMarker(string name, out string groupName)
        {
            groupName = "";
            if (string.IsNullOrEmpty(name)) return false;

            char first = name[0];
            if (!IsMarkerChar(first)) return false;
            if (first != name[name.Length - 1]) return false;

            // 提取前导特殊字符序列
            int prefixLen = 0;
            while (prefixLen < name.Length && name[prefixLen] == first)
                prefixLen++;

            // 提取尾部特殊字符序列
            int suffixStart = name.Length - 1;
            while (suffixStart >= 0 && name[suffixStart] == first)
                suffixStart--;
            suffixStart++;

            if (prefixLen == 0 || suffixStart >= name.Length || suffixStart <= prefixLen) return false;

            // 保留原始名称，翻译系统依赖完整形态键名作为 key 进行匹配
            groupName = name;
            return true;
        }

        private static bool IsMarkerChar(char c)
        {
            return c == '*' || c == '#' || c == '=' || c == '-' || c == '~' || c == '+' || c == '.' || c == '|';
        }

        private string GetDisplayName(string name)
        {
            return ObjectNames.NicifyVariableName(name.Replace("vrc.", "").Replace("vrchat.", ""));
        }

        private string GetTranslation(string name)
        {
            return _translations.TryGetValue(name, out var trans) ? trans : "";
        }

        private void ApplyFilter()
        {
            _filteredGroups.Clear();

            foreach (var group in _groups)
            {
                var filtered = new BlendshapeGroup
                {
                    name = group.name,
                    translation = GetTranslation(group.name), // 确保过滤后的对象也能获取到翻译
                    isExpanded = group.isExpanded
                };

                foreach (var item in group.items)
                {
                    bool match = true;

                    // 关键字过滤
                    if (!string.IsNullOrWhiteSpace(_searchText))
                    {
                        string search = _searchText.ToLower();
                        match = item.name.ToLower().Contains(search) ||
                            item.displayName.ToLower().Contains(search) ||
                            (!string.IsNullOrEmpty(GetTranslation(item.name)) && GetTranslation(item.name).ToLower().Contains(search));
                    }

                    // 仅显示已修改的形态键
                    if (_showModifiedOnly)
                    {
                        float value = _blendshapeValues.TryGetValue(item.name, out var v) ? v : 0f;
                        if (value <= 0f)
                            match = false;
                    }

                    if (match)
                        filtered.items.Add(item);
                }

                if (filtered.items.Count > 0)
                    _filteredGroups.Add(filtered);
            }
        }

        // ==================== 翻译配置管理 ====================

        /// <summary>
        /// 获取当前 mesh 对应的配置目录路径
        /// </summary>
        private string GetMeshConfigPath()
        {
            if (string.IsNullOrEmpty(_currentMeshGuid))
                return "";
            return $"{CONFIG_ROOT}/{_currentMeshGuid}";
        }

        /// <summary>
        /// 加载翻译配置：先加载当前 mesh 的专用翻译，再从其他配置中匹配通用翻译
        /// </summary>
        private void LoadTranslations()
        {
            _translations.Clear();

            if (string.IsNullOrEmpty(_currentMeshGuid))
            {
                AddLog(LogLevel.Warning, "无法获取 Mesh GUID，跳过翻译加载");
                return;
            }

            string meshConfigPath = GetMeshConfigPath();
            string meshTranslationFile = $"{meshConfigPath}/{TRANSLATION_FILENAME}";

            // 1. 加载当前 mesh 的专用翻译
            bool hasOwnConfig = File.Exists(meshTranslationFile);
            if (hasOwnConfig)
            {
                try
                {
                    var data = JsonUtility.FromJson<TranslationData>(File.ReadAllText(meshTranslationFile));
                    if (data != null && data.translations != null)
                    {
                        foreach (var item in data.translations)
                        {
                            _translations[item.key] = item.value;
                        }
                        AddLog(LogLevel.Info, $"已加载专用翻译: {data.translations.Count} 条");
                    }
                }
                catch (System.Exception e)
                {
                    AddLog(LogLevel.Error, $"加载翻译失败: {e.Message}");
                }
            }

            // 2. 如果当前模型没有配置文件，从其他配置中匹配并继承翻译
            if (!hasOwnConfig)
            {
                int inheritedCount = TryInheritTranslationsFromOtherConfigs();
                if (inheritedCount > 0)
                {
                    AddLog(LogLevel.Info, $"已从其他配置继承 {inheritedCount} 条翻译到新配置文件");
                    // 自动保存继承的翻译
                    SaveTranslations();
                }
            }
            else
            {
                // 3. 从其他配置中匹配通用翻译（同名的形态键）
                int reusedCount = 0;
                var otherConfigDirs = GetAllConfigDirectories();

                foreach (var dir in otherConfigDirs)
                {
                    if (dir == meshConfigPath) continue; // 跳过自己

                    string otherTranslationFile = $"{dir}/{TRANSLATION_FILENAME}";
                    if (!File.Exists(otherTranslationFile)) continue;

                    try
                    {
                        var data = JsonUtility.FromJson<TranslationData>(File.ReadAllText(otherTranslationFile));
                        if (data == null || data.translations == null) continue;

                        foreach (var item in data.translations)
                        {
                            // 如果当前翻译中没有，则复用
                            if (!_translations.ContainsKey(item.key))
                            {
                                _translations[item.key] = item.value;
                                reusedCount++;
                            }
                        }
                    }
                    catch { }
                }

                if (reusedCount > 0)
                {
                    AddLog(LogLevel.Info, $"已从其他配置复用 {reusedCount} 条通用翻译");
                }
            }
        }

        /// <summary>
        /// 从其他配置文件中匹配并继承翻译（用于新模型没有配置文件的情况）
        /// </summary>
        private int TryInheritTranslationsFromOtherConfigs()
        {
            var otherConfigDirs = GetAllConfigDirectories();
            int inheritedCount = 0;

            // 获取当前模型的所有形态键名称
            var currentShapeKeys = new HashSet<string>();
            foreach (var group in _groups)
            {
                currentShapeKeys.Add(group.name);
                foreach (var item in group.items)
                {
                    currentShapeKeys.Add(item.name);
                }
            }

            if (currentShapeKeys.Count == 0)
            {
                AddLog(LogLevel.Info, "当前模型没有形态键，无法继承翻译");
                return 0;
            }

            AddLog(LogLevel.Info, $"开始从其他配置匹配翻译，当前模型有 {currentShapeKeys.Count} 个形态键");

            // 遍历所有其他配置
            foreach (var dir in otherConfigDirs)
            {
                string otherTranslationFile = $"{dir}/{TRANSLATION_FILENAME}";
                if (!File.Exists(otherTranslationFile)) continue;

                try
                {
                    var data = JsonUtility.FromJson<TranslationData>(File.ReadAllText(otherTranslationFile));
                    if (data == null || data.translations == null) continue;

                    foreach (var item in data.translations)
                    {
                        // 如果当前模型的形态键中包含这个key，则继承翻译
                        if (currentShapeKeys.Contains(item.key) && !_translations.ContainsKey(item.key))
                        {
                            _translations[item.key] = item.value;
                            inheritedCount++;
                        }
                    }
                }
                catch { }
            }

            if (inheritedCount > 0)
            {
                AddLog(LogLevel.Info, $"匹配完成，从其他配置共继承 {inheritedCount} 条翻译");
            }
            else
            {
                AddLog(LogLevel.Info, "未找到可继承的翻译（其他配置的形态键与当前模型不匹配）");
            }

            return inheritedCount;
        }

        /// <summary>
        /// 获取所有配置目录（带缓存）
        /// </summary>
        private List<string> GetAllConfigDirectories()
        {
            var dirs = new List<string>();

            // 使用缓存避免频繁遍历文件系统
            if (_cachedConfigDirs != null && Time.realtimeSinceStartup - _configDirCacheTime < CONFIG_DIR_CACHE_INTERVAL)
            {
                dirs.AddRange(_cachedConfigDirs);
                return dirs;
            }

            if (!Directory.Exists(CONFIG_ROOT))
            {
                _cachedConfigDirs = new string[0];
                _configDirCacheTime = Time.realtimeSinceStartup;
                return dirs;
            }

            try
            {
                _cachedConfigDirs = Directory.GetDirectories(CONFIG_ROOT);
                dirs.AddRange(_cachedConfigDirs);
            }
            catch { }

            _configDirCacheTime = Time.realtimeSinceStartup;
            return dirs;
        }

        /// <summary>
        /// 保存翻译配置到当前 mesh 对应的配置目录
        /// </summary>
        private void SaveTranslations()
        {
            if (string.IsNullOrEmpty(_currentMeshGuid))
            {
                AddLog(LogLevel.Warning, "无法获取 Mesh GUID，无法保存翻译");
                return;
            }

            string configPath = GetMeshConfigPath();

            // 确保目录存在
            if (!Directory.Exists(configPath))
            {
                Directory.CreateDirectory(configPath);
                AddLog(LogLevel.Info, $"创建配置目录: {configPath}");
            }

            string translationFile = $"{configPath}/{TRANSLATION_FILENAME}";

            // 将 Dictionary 转换为可序列化的 List
            var translationList = _translations.Select(kvp => new TranslationItem { key = kvp.Key, value = kvp.Value }).ToList();

            var data = new TranslationData
            {
                meshGuid = _currentMeshGuid,
                meshName = _currentMeshName,
                translations = translationList
            };

            string json = JsonUtility.ToJson(data, true);
            File.WriteAllText(translationFile, json);

            AddLog(LogLevel.Info, $"已保存 {translationList.Count} 条翻译到: {translationFile}");
        }

        /// <summary>
        /// 删除当前 mesh 的翻译配置
        /// </summary>
        private void DeleteTranslations()
        {
            if (string.IsNullOrEmpty(_currentMeshGuid)) return;

            string configPath = GetMeshConfigPath();
            if (Directory.Exists(configPath))
            {
                string translationFile = $"{configPath}/{TRANSLATION_FILENAME}";
                if (File.Exists(translationFile))
                {
                    File.Delete(translationFile);
                    AddLog(LogLevel.Info, $"已删除翻译配置: {translationFile}");
                }
            }
        }

        // ==================== 绘制界面 ====================
        private void OnGUI()
        {
            DrawTabBar();
            DrawToolbar();

            if (_selectedTab == 0)
                DrawFacialPanel();
            else
                DrawLogPanel();

            // 底部版本信息
            GUILayout.Space(5);
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            EditorGUILayout.LabelField($"by iceblue76ec  版本号 {VERSION}", EditorStyles.miniLabel);
            EditorGUILayout.EndHorizontal();
        }

        private void DrawTabBar()
        {
            EditorGUILayout.BeginHorizontal("toolbar", GUILayout.Height(28));
            int newTab = GUILayout.Toolbar(_selectedTab, _tabNames, "LargeButton", GUILayout.Height(24));
            if (newTab != _selectedTab)
            {
                _selectedTab = newTab;
                if (_selectedTab == 1) // 切换到日志面板时，自动滚动到底部
                {
                    _logScrollPosition = new Vector2(0, float.MaxValue);
                }
            }
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
        }

        private void DrawToolbar()
        {
            // 工具栏行
            EditorGUILayout.BeginHorizontal("toolbar", GUILayout.Height(28));

            // Avatar 选择
            EditorGUILayout.LabelField("Avatar:", GUILayout.Width(50));
            _selectedAvatar = (GameObject)EditorGUILayout.ObjectField(_selectedAvatar, typeof(GameObject), true, GUILayout.Width(160));

            if (GUILayout.Button("刷新", GUILayout.Width(50)))
            {
                TryAutoDetectVRChatAvatar();
            }

            GUILayout.Space(5);

            // SkinnedMesh 选择
            if (_selectedAvatar != null)
            {
                EditorGUILayout.LabelField("Mesh:", GUILayout.Width(40));
                var smr = (SkinnedMeshRenderer)EditorGUILayout.ObjectField(_targetSkinnedMesh, typeof(SkinnedMeshRenderer), true, GUILayout.Width(200));
                if (smr != _targetSkinnedMesh)
                {
                    _targetSkinnedMesh = smr;
                    RefreshMeshInfo();
                    RefreshBlendshapes();
                }
            }

            GUILayout.Space(15);

            // 显示当前 Mesh GUID
            if (!string.IsNullOrEmpty(_currentMeshGuid))
            {
                EditorGUILayout.LabelField($"ID: {_currentMeshGuid}", EditorStyles.miniLabel);
            }

            GUILayout.FlexibleSpace();

            EditorGUILayout.EndHorizontal();

            // 搜索栏（仅捏脸面板显示）
            if (_selectedTab == 0)
            {
                EditorGUILayout.BeginHorizontal("toolbar", GUILayout.Height(25));
                EditorGUILayout.LabelField("搜索:", GUILayout.Width(40));
                GUI.SetNextControlName("SearchField");
                _searchText = EditorGUILayout.TextField(_searchText, GUI.skin.textField, GUILayout.Width(200));
                if (GUILayout.Button("×", GUILayout.Width(20)))
                {
                    _searchText = "";
                    GUI.FocusControl("");
                    ApplyFilter();
                }
                if (GUILayout.Button("搜索", GUILayout.Width(45)))
                {
                    ApplyFilter();
                }

                // 回车键搜索（不受焦点影响）
                if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Return)
                {
                    ApplyFilter();
                    Event.current.Use();
                }

                GUILayout.Space(5);

                int totalCount = _groups.Sum(g => g.items.Count);
                int filteredCount = _filteredGroups.Sum(g => g.items.Count);
                string countText = _showModifiedOnly
                    ? $"已修改 {_blendshapeValues.Count(kvp => kvp.Value > 0)} / 共 {totalCount} 个"
                    : (filteredCount == totalCount ? $"共 {totalCount} 个" : $"显示 {filteredCount} / {totalCount}");

                EditorGUILayout.LabelField(countText, EditorStyles.miniLabel, GUILayout.Width(120));

                GUILayout.FlexibleSpace();

                _showModifiedOnly = EditorGUILayout.ToggleLeft("仅显示已修改", _showModifiedOnly, GUILayout.Width(110));
                if (GUI.changed)
                {
                    ApplyFilter();
                }

                EditorGUILayout.EndHorizontal();

                // 操作按钮行
                EditorGUILayout.BeginHorizontal("toolbar", GUILayout.Height(28));

                float btnWidth = (position.width - 40) / 6;
                if (GUILayout.Button("导出CSV", GUILayout.Width(btnWidth)))
                {
                    ExportTranslationCSV();
                }
                if (GUILayout.Button("导入CSV", GUILayout.Width(btnWidth)))
                {
                    ImportTranslationCSV();
                }
                if (GUILayout.Button("导出动画", GUILayout.Width(btnWidth)))
                {
                    ExportAnimation();
                }
                if (GUILayout.Button("导入动画", GUILayout.Width(btnWidth)))
                {
                    ImportAnimation();
                }
                if (GUILayout.Button("全部折叠", GUILayout.Width(btnWidth)))
                {
                    foreach (var g in _filteredGroups) g.isExpanded = false;
                }
                if (GUILayout.Button("全部展开", GUILayout.Width(btnWidth)))
                {
                    foreach (var g in _filteredGroups) g.isExpanded = true;
                }

                EditorGUILayout.EndHorizontal();
            }
        }

        private void DrawFacialPanel()
        {
            if (_targetSkinnedMesh == null || _groups.Count == 0)
            {
                // 空状态时只显示一行提示，不占用多余空间
                GUILayout.Space(20);
                EditorGUILayout.LabelField("请选中场景中的 Avatar 对象", EditorStyles.miniLabel, GUILayout.ExpandWidth(true));
                return;
            }

            EditorGUILayout.BeginVertical(GUILayout.ExpandWidth(true));

            // 形态键列表
            float scrollHeight = position.height - 110;
            _logScrollPosition = EditorGUILayout.BeginScrollView(_logScrollPosition, GUILayout.Height(scrollHeight));

            foreach (var group in _filteredGroups)
            {
                DrawGroup(group);
            }

            EditorGUILayout.EndScrollView();

            EditorGUILayout.EndVertical();
        }

        private void DrawGroup(BlendshapeGroup group)
        {
            if (group.items.Count == 0) return;

            // 分组行
            EditorGUILayout.BeginHorizontal();

            // 折叠按钮
            bool isExpanded = EditorGUILayout.Foldout(group.isExpanded, "", true, EditorStyles.foldout);

            if (isExpanded != group.isExpanded)
            {
                group.isExpanded = isExpanded;
            }

            // 分组名称 + 数量
            EditorGUILayout.LabelField($"{group.name} ({group.items.Count})", EditorStyles.boldLabel, GUILayout.Width(260));

            // 翻译输入框（始终从字典读取，保持一致性）
            EditorGUI.BeginChangeCheck();
            string currentTrans = GetTranslation(group.name);
            string trans = EditorGUILayout.TextField(currentTrans, GUILayout.Width(190));
            if (EditorGUI.EndChangeCheck())
            {
                group.translation = string.IsNullOrEmpty(trans) ? null : trans;
                if (string.IsNullOrEmpty(trans))
                    _translations.Remove(group.name);
                else
                    _translations[group.name] = trans;
                _translationSaveTime = Time.realtimeSinceStartup + 1f;
            }

            // 右侧空白占位，与形态键行的滑块+重置区域对齐
            GUILayout.Space(500);
            EditorGUILayout.EndHorizontal();

            if (group.isExpanded)
            {
                EditorGUI.indentLevel++;
                foreach (var item in group.items)
                {
                    DrawBlendshapeItem(item);
                }
                EditorGUI.indentLevel--;
            }
        }

        private void DrawBlendshapeItem(BlendshapeInfo item)
        {
            EditorGUILayout.BeginHorizontal();

            // 占位，与分组行的折叠按钮对齐
            GUILayout.Space(40);

            // 形态键名称
            EditorGUILayout.LabelField(item.displayName, EditorStyles.label, GUILayout.Width(260));

            // 翻译输入框（始终显示）
            EditorGUI.BeginChangeCheck();
            string trans = EditorGUILayout.TextField(_translations.TryGetValue(item.name, out var t) ? t : "", GUILayout.Width(200));
            if (EditorGUI.EndChangeCheck())
            {
                if (string.IsNullOrEmpty(trans))
                    _translations.Remove(item.name);
                else
                    _translations[item.name] = trans;
                _translationSaveTime = Time.realtimeSinceStartup + 1f;
            }

            // 滑块
            float oldValue = _blendshapeValues[item.name];
            float newValue = EditorGUILayout.Slider(oldValue, 0f, 100f, GUILayout.Width(250));

            // 重置按钮
            if (GUILayout.Button("重置", GUILayout.Width(50)))
            {
                newValue = _blendshapeBackup[item.name];
            }

            EditorGUILayout.EndHorizontal();

            if (Mathf.Abs(newValue - oldValue) > 0.01f)
            {
                _blendshapeValues[item.name] = newValue;
                ApplyBlendshape(item.name, newValue);
            }
        }

        private void DrawLogPanel()
        {
            EditorGUILayout.BeginVertical(GUILayout.ExpandWidth(true));

            // 日志过滤器
            EditorGUILayout.BeginHorizontal("toolbar", GUILayout.Height(25));
            EditorGUILayout.LabelField("级别:", GUILayout.Width(35));
            _logFilter = (LogLevel)EditorGUILayout.EnumPopup(_logFilter, GUILayout.Width(80));
            GUILayout.Space(10);
            if (GUILayout.Button("清空", GUILayout.Width(50)))
            {
                _logs.Clear();
                _logScrollPosition = Vector2.zero;
            }
            if (GUILayout.Button("导出日志", GUILayout.Width(70)))
            {
                ExportLogs();
            }
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(5);

            // 日志文本（可复制+可滚动）
            var filteredLogs = _logs.Where(l => _logFilter == LogLevel.All || l.level == _logFilter).ToList();
            string logText = filteredLogs.Count > 0
                ? string.Join("\n", filteredLogs.Select(l => $"{l.timestamp}  [{l.level}]  {l.message}"))
                : "无日志记录";

            float scrollHeight = position.height - 100;
            GUIStyle textStyle = new GUIStyle(EditorStyles.textArea)
            {
                fontSize = 11,
                wordWrap = true
            };

            _logScrollPosition = EditorGUILayout.BeginScrollView(_logScrollPosition, GUILayout.Height(scrollHeight));
            EditorGUILayout.TextArea(logText, textStyle, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
            EditorGUILayout.EndScrollView();

            EditorGUILayout.EndVertical();
        }

        // ==================== 动作 ====================
        private void ApplyBlendshape(string name, float value)
        {
            if (_targetSkinnedMesh == null) return;

            int index = _targetSkinnedMesh.sharedMesh.GetBlendShapeIndex(name);
            if (index >= 0)
            {
                Undo.RecordObject(_targetSkinnedMesh, $"Blendshape {name}");
                _targetSkinnedMesh.SetBlendShapeWeight(index, value);
            }
        }

        private void ApplyAll()
        {
            if (_targetSkinnedMesh == null) return;

            Undo.RecordObject(_targetSkinnedMesh, "Apply All Blendshapes");
            EditorUtility.SetDirty(_targetSkinnedMesh);
            AddLog(LogLevel.Info, $"已应用 {_blendshapeValues.Count} 个形态键修改");
        }

        private void ResetAllToZero()
        {
            foreach (var name in _blendshapeValues.Keys.ToList())
            {
                _blendshapeValues[name] = 0f;
                ApplyBlendshape(name, 0f);
            }
            AddLog(LogLevel.Info, "所有形态键已归零");
        }

        private void ResetAllToBackup()
        {
            foreach (var kvp in _blendshapeBackup)
            {
                _blendshapeValues[kvp.Key] = kvp.Value;
                ApplyBlendshape(kvp.Key, kvp.Value);
            }
            AddLog(LogLevel.Info, "已恢复到初始状态");
        }

        // ==================== 翻译管理 ====================
        private void ExportTranslationCSV()
        {
            string avatarName = _selectedAvatar != null ? _selectedAvatar.name : "Unknown";
            string defaultName = $"{avatarName} blendshapes_export.csv";
            string path = EditorUtility.SaveFilePanel("导出形态键列表 (CSV)", "", defaultName, "csv");
            if (string.IsNullOrEmpty(path)) return;

            var lines = new List<string> { "原名,翻译" };
            foreach (var group in _groups)
            {
                // 先导出分组标记本身（如 === Eye ===）
                string groupTrans = _translations.TryGetValue(group.name, out var gt) ? gt : "";
                lines.Add($"\"{group.name}\",\"{groupTrans}\"");

                // 再导出组内的形态键
                foreach (var item in group.items)
                {
                    string trans = _translations.TryGetValue(item.name, out var t) ? t : "";
                    lines.Add($"\"{item.name}\",\"{trans}\"");
                }
            }

            File.WriteAllLines(path, lines);
            AddLog(LogLevel.Info, $"已导出 CSV 到: {path}");
        }

        private void ImportTranslationCSV()
        {
            string path = EditorUtility.OpenFilePanel("导入翻译 (CSV)", "", "csv");
            if (string.IsNullOrEmpty(path)) return;

            try
            {
                var lines = File.ReadAllLines(path);
                int imported = 0;

                foreach (var line in lines.Skip(1))
                {
                    var parts = ParseCSVLine(line);
                    if (parts.Length >= 2)
                    {
                        _translations[parts[0]] = parts[1];
                        imported++;
                    }
                }

                RefreshBlendshapes(); // 刷新显示翻译
                AddLog(LogLevel.Info, $"已导入 {imported} 条翻译（请手动保存）");
            }
            catch (System.Exception e)
            {
                AddLog(LogLevel.Error, $"导入翻译失败: {e.Message}");
            }
        }

        private string[] ParseCSVLine(string line)
        {
            var result = new List<string>();
            var current = "";
            bool inQuotes = false;

            foreach (char c in line)
            {
                if (c == '"')
                {
                    inQuotes = !inQuotes;
                }
                else if (c == ',' && !inQuotes)
                {
                    result.Add(current);
                    current = "";
                }
                else
                {
                    current += c;
                }
            }
            result.Add(current);
            return result.ToArray();
        }

        // ==================== 动画导入/导出 ====================
        private void ExportAnimation()
        {
            // 确保导出目录存在
            string exportDir = "Assets/iceblue76ec/export";
            if (!Directory.Exists(exportDir))
            {
                Directory.CreateDirectory(exportDir);
                UnityEditor.AssetDatabase.Refresh();
            }

            // 记录导出目录
            _lastExportDir = exportDir;

            string avatarName = _selectedAvatar != null ? _selectedAvatar.name : "Unknown";
            string timestamp = System.DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string defaultName = $"{avatarName} blendshapes {timestamp}.anim";
            string absPath = EditorUtility.SaveFilePanel("导出形态键动画", exportDir, defaultName, "anim");
            if (string.IsNullOrEmpty(absPath)) return;

            // 更新最后导出目录为实际选择的目录
            _lastExportDir = Path.GetDirectoryName(absPath);

            // 转换为相对路径
            string assetPath = absPath;
            if (absPath.StartsWith(Application.dataPath))
            {
                assetPath = "Assets" + absPath.Substring(Application.dataPath.Length);
            }

            try
            {
                var clip = new AnimationClip();

                foreach (var kvp in _blendshapeValues)
                {
                    int index = _targetSkinnedMesh.sharedMesh.GetBlendShapeIndex(kvp.Key);
                    if (index >= 0)
                    {
                        var binding = EditorCurveBinding.FloatCurve(
                            "",
                            typeof(SkinnedMeshRenderer),
                            $"blendShape.{kvp.Key}"
                        );

                        var keyframes = new Keyframe[1];
                        keyframes[0] = new Keyframe(0, kvp.Value);
                        var curve = new AnimationCurve(keyframes);

                        UnityEditor.AnimationUtility.SetEditorCurve(clip, binding, curve);
                    }
                }

                UnityEditor.AssetDatabase.CreateAsset(clip, assetPath);
                UnityEditor.AssetDatabase.SaveAssets();
                AddLog(LogLevel.Info, $"已导出动画到: {assetPath}");
            }
            catch (System.Exception e)
            {
                AddLog(LogLevel.Error, $"导出动画失败: {e.Message}");
            }
        }

        private void ImportAnimation()
        {
            string absPath = EditorUtility.OpenFilePanel("导入形态键动画", _lastExportDir, "anim");
            if (string.IsNullOrEmpty(absPath)) return;

            // 转换为相对路径
            string assetPath = absPath;
            if (absPath.StartsWith(Application.dataPath))
            {
                assetPath = "Assets" + absPath.Substring(Application.dataPath.Length);
            }

            try
            {
                var clip = UnityEditor.AssetDatabase.LoadAssetAtPath<AnimationClip>(assetPath);
                if (clip == null)
                {
                    AddLog(LogLevel.Error, "无法加载动画文件");
                    return;
                }

                var bindings = AnimationUtility.GetCurveBindings(clip);
                int imported = 0;

                foreach (var binding in bindings)
                {
                    if (binding.propertyName.StartsWith("blendShape."))
                    {
                        string shapeName = binding.propertyName.Substring("blendShape.".Length);
                        var curve = AnimationUtility.GetEditorCurve(clip, binding);
                        if (curve != null && curve.length > 0)
                        {
                            float value = curve.keys[0].value;
                            _blendshapeValues[shapeName] = value;
                            ApplyBlendshape(shapeName, value);
                            imported++;
                        }
                    }
                }

                AddLog(LogLevel.Info, $"已从动画导入 {imported} 个形态键");
            }
            catch (System.Exception e)
            {
                AddLog(LogLevel.Error, $"导入动画失败: {e.Message}");
            }
        }

        // ==================== 日志 ====================
        private void AddLog(LogLevel level, string message)
        {
            _logs.Add(new LogEntry
            {
                level = level,
                message = message,
                timestamp = System.DateTime.Now.ToString("HH:mm:ss")
            });

            if (_logs.Count > MAX_LOG_COUNT)
            {
                _logs.RemoveRange(0, _logs.Count - MAX_LOG_COUNT);
            }
        }

        private string GetLogFilePath()
        {
            if (!Directory.Exists(CONFIG_ROOT))
                Directory.CreateDirectory(CONFIG_ROOT);
            return $"{CONFIG_ROOT}/{LOG_FILENAME}";
        }

        private void SaveLogs()
        {
            string path = GetLogFilePath();
            try
            {
                var lines = _logs.Select(l => $"{l.timestamp}|{(int)l.level}|{l.message}");
                File.WriteAllLines(path, lines);
            }
            catch { }
        }

        private void LoadLogs()
        {
            string path = GetLogFilePath();
            if (!File.Exists(path)) return;

            try
            {
                var lines = File.ReadAllLines(path);
                _logs.Clear();
                foreach (var line in lines)
                {
                    var parts = line.Split('|');
                    if (parts.Length >= 3 && System.Enum.TryParse<LogLevel>(parts[1], out var level))
                    {
                        _logs.Add(new LogEntry
                        {
                            timestamp = parts[0],
                            level = level,
                            message = parts[2]
                        });
                    }
                }
            }
            catch { }
        }

        private void ExportLogs()
        {
            string path = EditorUtility.SaveFilePanel("导出日志", "", $"VRCFacialEditor_log_{System.DateTime.Now:yyyyMMdd_HHmmss}.txt", "txt");
            if (string.IsNullOrEmpty(path)) return;

            var lines = _logs.Select(l => $"{l.timestamp} [{l.level}] {l.message}");
            File.WriteAllLines(path, lines);
            AddLog(LogLevel.Info, $"已导出日志到: {path}");
        }
    }

    // ==================== 数据结构 ====================
    public class BlendshapeGroup
    {
        public string name;
        public string translation;
        public bool isExpanded = true;
        public List<BlendshapeInfo> items = new();
    }

    public class BlendshapeInfo
    {
        public string name;
        public string displayName;
        public string translation;
        public float value;
        public int index;
    }

    [System.Serializable]
    public class TranslationData
    {
        public string meshGuid;
        public string meshName;
        public List<TranslationItem> translations;
    }

    [System.Serializable]
    public class TranslationItem
    {
        public string key;
        public string value;
    }

    public enum LogLevel
    {
        All,
        Info,
        Warning,
        Error
    }

    public class LogEntry
    {
        public LogLevel level;
        public string message;
        public string timestamp;
    }
}
