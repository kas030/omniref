using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace OmniRef.App.Services;

public sealed class LocalizationService : INotifyPropertyChanged
{
    private static readonly IReadOnlyDictionary<string, string> English =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["AppTitle"] = "OmniRef",
            ["NewWorkspace"] = "New",
            ["OpenWorkspace"] = "Open",
            ["Save"] = "Save",
            ["SaveAs"] = "Save as",
            ["AddFiles"] = "Files",
            ["AddFolder"] = "Folder",
            ["AddText"] = "Text",
            ["AddFrame"] = "Frame",
            ["Undo"] = "Undo",
            ["Redo"] = "Redo",
            ["Delete"] = "Delete",
            ["Paste"] = "Paste",
            ["Arrange"] = "Arrange",
            ["AlignLeft"] = "Align left",
            ["AlignCenter"] = "Align centers",
            ["AlignRight"] = "Align right",
            ["AlignTop"] = "Align top",
            ["AlignMiddle"] = "Align middles",
            ["AlignBottom"] = "Align bottom",
            ["DistributeHorizontal"] = "Distribute horizontally",
            ["DistributeVertical"] = "Distribute vertically",
            ["BringToFront"] = "Bring to front",
            ["BringForward"] = "Bring forward",
            ["SendBackward"] = "Send backward",
            ["SendToBack"] = "Send to back",
            ["Retry"] = "Retry",
            ["SearchPlaceholder"] = "Search title, path, text or tags",
            ["Search"] = "Search",
            ["Properties"] = "Properties",
            ["Title"] = "Title",
            ["Tags"] = "Tags",
            ["TagsHint"] = "Comma-separated tags",
            ["TextStyle"] = "Text style",
            ["FontSize"] = "Size",
            ["TextColor"] = "Text color",
            ["CardColor"] = "Card color",
            ["TextAlignLeft"] = "Align text left",
            ["TextAlignCenter"] = "Center text",
            ["TextAlignRight"] = "Align text right",
            ["Embed"] = "Embed copy",
            ["Export"] = "Export",
            ["Relink"] = "Relink",
            ["Reveal"] = "Show in Explorer",
            ["AlwaysOnTop"] = "Always on top",
            ["Theme"] = "Theme",
            ["Language"] = "中",
            ["SwitchLanguage"] = "Switch language",
            ["Exit"] = "Exit",
            ["Show"] = "Show OmniRef",
            ["Hide"] = "Hide OmniRef",
            ["NoWorkspace"] = "Drop something worth keeping close.",
            ["NoWorkspaceHint"] = "Create or open a workspace, then drag files, folders and images onto the canvas.",
            ["CreateWorkspace"] = "Create workspace",
            ["Saved"] = "Saved",
            ["Saving"] = "Saving…",
            ["Unsaved"] = "Unsaved",
            ["ReadOnly"] = "Read-only",
            ["SaveFailed"] = "Could not save",
            ["HotkeyConflict"] = "Ctrl+Alt+Space is already used by another application.",
            ["WorkspaceError"] = "Workspace error",
            ["OpenFailed"] = "Could not open the selected workspace.",
            ["MissingSource"] = "The referenced source is missing. Relink it from the Properties panel.",
            ["EmbedTooLarge"] = "Files larger than 512 MB cannot be embedded.",
            ["ReadonlyEmbedded"] = "Embedded files open as read-only copies. Export first to edit.",
            ["FrameDefault"] = "Group",
            ["TextDefault"] = "Type here",
            ["Untitled"] = "Untitled",
            ["CloseTab"] = "Close workspace",
            ["Sidebar"] = "Toggle sidebar",
            ["SidebarShort"] = "Panel",
            ["Grid"] = "Toggle canvas grid",
            ["GridShort"] = "Grid",
            ["GridSnap"] = "Snap to grid (hold Ctrl while dragging to disable)",
            ["GridSnapShort"] = "Snap",
            ["TopShort"] = "Top",
            ["ThemeShort"] = "Theme",
            ["ZoomReset"] = "Reset zoom",
            ["Compact"] = "Compact workspace",
            ["ConfirmCloseDirty"] = "This workspace could not be saved. Close it anyway?",
            ["RecoveryNotice"] = "OmniRef did not exit cleanly. Recovery workspaces have been restored.",
            ["SaveDialogTitle"] = "Save OmniRef workspace",
            ["OpenDialogTitle"] = "Open OmniRef workspace",
            ["FilesDialogTitle"] = "Add files",
            ["FolderDialogTitle"] = "Add folder",
            ["ExportDialogTitle"] = "Export embedded file",
            ["RelinkDialogTitle"] = "Relink source"
        };

    private static readonly IReadOnlyDictionary<string, string> Chinese =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["AppTitle"] = "OmniRef",
            ["NewWorkspace"] = "新建",
            ["OpenWorkspace"] = "打开",
            ["Save"] = "保存",
            ["SaveAs"] = "另存为",
            ["AddFiles"] = "文件",
            ["AddFolder"] = "文件夹",
            ["AddText"] = "文本",
            ["AddFrame"] = "分组框",
            ["Undo"] = "撤销",
            ["Redo"] = "重做",
            ["Delete"] = "删除",
            ["Paste"] = "粘贴",
            ["Arrange"] = "排列",
            ["AlignLeft"] = "左对齐",
            ["AlignCenter"] = "水平居中",
            ["AlignRight"] = "右对齐",
            ["AlignTop"] = "顶端对齐",
            ["AlignMiddle"] = "垂直居中",
            ["AlignBottom"] = "底端对齐",
            ["DistributeHorizontal"] = "水平分布",
            ["DistributeVertical"] = "垂直分布",
            ["BringToFront"] = "置于顶层",
            ["BringForward"] = "上移一层",
            ["SendBackward"] = "下移一层",
            ["SendToBack"] = "置于底层",
            ["Retry"] = "重试",
            ["SearchPlaceholder"] = "搜索标题、路径、文本或标签",
            ["Search"] = "搜索",
            ["Properties"] = "属性",
            ["Title"] = "标题",
            ["Tags"] = "标签",
            ["TagsHint"] = "使用逗号分隔标签",
            ["TextStyle"] = "文本样式",
            ["FontSize"] = "字号",
            ["TextColor"] = "文字颜色",
            ["CardColor"] = "卡片颜色",
            ["TextAlignLeft"] = "文字左对齐",
            ["TextAlignCenter"] = "文字居中",
            ["TextAlignRight"] = "文字右对齐",
            ["Embed"] = "内嵌副本",
            ["Export"] = "导出",
            ["Relink"] = "重新定位",
            ["Reveal"] = "在资源管理器中显示",
            ["AlwaysOnTop"] = "窗口置顶",
            ["Theme"] = "主题",
            ["Language"] = "EN",
            ["SwitchLanguage"] = "切换语言",
            ["Exit"] = "退出",
            ["Show"] = "显示 OmniRef",
            ["Hide"] = "隐藏 OmniRef",
            ["NoWorkspace"] = "把值得随时查看的内容放在这里。",
            ["NoWorkspaceHint"] = "新建或打开工作区，然后将文件、文件夹和图片拖到画布上。",
            ["CreateWorkspace"] = "新建工作区",
            ["Saved"] = "已保存",
            ["Saving"] = "正在保存…",
            ["Unsaved"] = "未保存",
            ["ReadOnly"] = "只读",
            ["SaveFailed"] = "保存失败",
            ["HotkeyConflict"] = "Ctrl+Alt+Space 已被其他应用占用。",
            ["WorkspaceError"] = "工作区错误",
            ["OpenFailed"] = "无法打开所选工作区。",
            ["MissingSource"] = "引用的源文件已丢失，请在属性面板中重新定位。",
            ["EmbedTooLarge"] = "超过 512 MB 的文件不能内嵌。",
            ["ReadonlyEmbedded"] = "内嵌文件会以只读副本打开；如需编辑，请先导出。",
            ["FrameDefault"] = "分组",
            ["TextDefault"] = "在这里输入",
            ["Untitled"] = "未命名",
            ["CloseTab"] = "关闭工作区",
            ["Sidebar"] = "显示或隐藏侧栏",
            ["SidebarShort"] = "侧栏",
            ["Grid"] = "显示或隐藏画布网格",
            ["GridShort"] = "网格",
            ["GridSnap"] = "吸附到网格（拖动时按住 Ctrl 可临时关闭）",
            ["GridSnapShort"] = "吸附",
            ["TopShort"] = "置顶",
            ["ThemeShort"] = "主题",
            ["ZoomReset"] = "重置缩放",
            ["Compact"] = "压缩工作区",
            ["ConfirmCloseDirty"] = "该工作区尚未成功保存，仍要关闭吗？",
            ["RecoveryNotice"] = "OmniRef 上次未正常退出，已恢复临时工作区。",
            ["SaveDialogTitle"] = "保存 OmniRef 工作区",
            ["OpenDialogTitle"] = "打开 OmniRef 工作区",
            ["FilesDialogTitle"] = "添加文件",
            ["FolderDialogTitle"] = "添加文件夹",
            ["ExportDialogTitle"] = "导出内嵌文件",
            ["RelinkDialogTitle"] = "重新定位源文件"
        };

    private IReadOnlyDictionary<string, string> _strings = English;
    private string _configuredLanguage = "auto";

    public event PropertyChangedEventHandler? PropertyChanged;

    public string this[string key] => _strings.TryGetValue(key, out var value) ? value : key;
    public string ConfiguredLanguage => _configuredLanguage;
    public bool IsChinese { get; private set; }

    public void SetLanguage(string language)
    {
        _configuredLanguage = language;
        IsChinese = language.Equals("zh-CN", StringComparison.OrdinalIgnoreCase) ||
                    (language.Equals("auto", StringComparison.OrdinalIgnoreCase) &&
                     CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals(
                         "zh",
                         StringComparison.OrdinalIgnoreCase));
        _strings = IsChinese ? Chinese : English;
        OnPropertyChanged("Item[]");
        OnPropertyChanged(nameof(IsChinese));
        OnPropertyChanged(nameof(ConfiguredLanguage));
    }

    public void Toggle() => SetLanguage(IsChinese ? "en-US" : "zh-CN");

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
