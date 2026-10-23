using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using Mihomo.Desktop.Controls;
using Mihomo.Desktop.Services;

namespace Mihomo.Desktop.Pages;

public sealed class ProfilesPage : NativePage
{
    private readonly TextBlock path = Ui.Text("正在读取配置…", 16, true);
    private readonly TextBlock detail = Ui.Muted("");
    private readonly TextBox editor = new() { AcceptsReturn = true, AcceptsTab = true, TextWrapping = TextWrapping.NoWrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 14, Padding = new Thickness(16), UndoLimit = 100 };
    private readonly TextBlock editorPosition = Ui.Muted("");
    private readonly Grid editPane = new() { Visibility = Visibility.Collapsed };
    private readonly StackPanel overview = new();
    private readonly DataGrid resourceUpdates = Ui.Table(("资源", nameof(ResourceRow.Name), -2), ("状态", nameof(ResourceRow.Status), 130), ("当前版本", nameof(ResourceRow.Current), -1), ("信息", nameof(ResourceRow.Message), -2));
    private string revision = "";
    private string savedContent = "";
    private bool loading;
    private bool loaded;
    private bool saving;
    private bool dirty;
    private bool writable = true;
    private bool updateWasActive;
    private long documentGeneration;
    private long draftGeneration;
    private int maxBytes = 4 * 1024 * 1024;

    public ProfilesPage(AppSession session) : base(session, "profiles", "配置", "管理当前 YAML，导入订阅并更新规则与 Geo 数据")
    {
        editor.Id("ConfigEditor"); editor.TextChanged += EditorChanged; editor.SelectionChanged += (_, _) => UpdatePosition();
        var fileActions = Ui.Row(Action("编辑配置", "EditConfig", OpenEditor, true), Action("重新加载", "ReloadConfig", Reload), Action("打开文件夹", "OpenConfigFolder", () => Command("desktop_open_directory", new { kind = "config" })));
        overview.Children.Add(Ui.Card(Ui.Column(path, detail, fileActions), 24)); detail.Margin = new Thickness(0, 8, 0, 20);
        overview.Children.Add(Ui.Card(Ui.Column(Ui.Text("导入配置", 18, true), Ui.Muted("支持 YAML 文件、订阅地址或粘贴内容。也可以将 .yaml / .yml 文件拖入此页。"), Ui.Row(Action("从文件导入", "ImportConfigFile", ChooseFile), Action("从 URL 导入", "ImportConfigUrl", ImportUrl), Action("粘贴 YAML", "PasteConfig", Paste))), 24));
        resourceUpdates.Height = 220; resourceUpdates.Id("ResourceUpdateTable");
        overview.Children.Add(Ui.Card(Ui.Column(Ui.Text("内核资源更新", 18, true), Ui.Muted("由内核统一检查规则、Geo 数据和面板，具体范围与结果如下。Windows 应用更新在设置页管理。"), Ui.Row(Action("检查并更新", "UpdateResources", UpdateResources), Action("刷新状态", "RefreshResources", RefreshResources)), resourceUpdates), 24));
        editPane.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); editPane.RowDefinitions.Add(new RowDefinition()); editPane.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        editPane.Children.Add(Ui.Row(Action("保存并应用", "SaveConfig", () => Save(true), true), Action("仅保存", "SaveOnlyConfig", () => Save(false)), Action("放弃修改", "DiscardConfig", Discard), Action("返回配置", "CloseEditor", CloseEditor)));
        Grid.SetRow(editor, 1); editPane.Children.Add(editor); Grid.SetRow(editorPosition, 2); editorPosition.Margin = new Thickness(0, 8, 0, 0); editPane.Children.Add(editorPosition);
        var layer = new Grid(); var scroll = Ui.Scroll(overview); layer.Children.Add(scroll); layer.Children.Add(editPane); editPane.IsVisibleChanged += (_, _) => scroll.Visibility = editPane.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible; SetBody(layer);
        AllowDrop = true; DragOver += (_, e) => { e.Effects = !saving && !Session.IsUpdating && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
        Drop += async (_, e) => { if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length == 1) await RunAsync(() => ImportFile(files[0])); e.Handled = true; };
        Session.HostEvent += HostEvent;
        Session.StatusChanged += (_, _) => ApplyInteractionState();
        ApplyInteractionState();
    }
    private void HostEvent(string name, JsonElement data)
    {
        if (name != "desktop-config-changed" || saving) return;
        if (dirty) Message("磁盘上的配置已发生变化。当前草稿已保留，保存时会检查版本冲突。", true);
        else if (IsActive) _ = RunAsync(ReadDocument);
        else loaded = false;
    }
    public override async Task RefreshAsync()
    {
        if (!loaded && !dirty) await ReadDocument();
        if (IsRunning) await RefreshResources();
    }
    private Task ReadDocument() => ReadDocument(false);
    private async Task ReadDocument(bool discardDraft)
    {
        var generation = ++documentGeneration;
        var draft = draftGeneration;
        var document = await Command("desktop_read_config");
        if (generation != documentGeneration || draft != draftGeneration || (dirty && !discardDraft)) return;
        ApplyDocument(document);
    }
    private void ApplyDocument(JsonElement document)
    {
        ++documentGeneration; loaded = true;
        loading = true; savedContent = document.Text("content"); revision = document.Text("revision");
        path.Text = document.Text("path"); detail.Text = "本机配置 · " + Ui.Bytes(Encoding.UTF8.GetByteCount(savedContent));
        maxBytes = (int)document.Number("max_size", 4 * 1024 * 1024); writable = document.Get("writable").ValueKind != JsonValueKind.False;
        editor.Text = savedContent; savedContent = editor.Text; dirty = false; Session.IsDirty = false; loading = false; ApplyInteractionState(); UpdatePosition();
    }
    private void EditorChanged(object sender, TextChangedEventArgs e)
    {
        if (loading) return; ++draftGeneration; dirty = editor.Text != savedContent; Session.IsDirty = dirty; UpdatePosition();
    }
    private void UpdatePosition()
    {
        var line = editor.GetLineIndexFromCharacterIndex(editor.CaretIndex); var start = line < 0 ? 0 : editor.GetCharacterIndexFromLineIndex(line);
        editorPosition.Text = $"第 {Math.Max(0, line) + 1} 行，第 {editor.CaretIndex - Math.Max(0, start) + 1} 列    {Ui.Bytes(Encoding.UTF8.GetByteCount(editor.Text))} / {Ui.Bytes(maxBytes)}" + (dirty ? "    未保存" : "    已保存");
    }
    private async Task OpenEditor() { EnsureEditable(); if (!loaded) await ReadDocument(); EnsureEditable(); editPane.Visibility = Visibility.Visible; editor.Focus(); }
    private async Task CloseEditor() { if (await CanLeaveAsync()) editPane.Visibility = Visibility.Collapsed; }
    private async Task Save(bool reload)
    {
        EnsureEditable();
        if (saving) throw new InvalidOperationException("配置操作正在进行，请稍候。");
        var content = editor.Text; if (Encoding.UTF8.GetByteCount(content) > maxBytes) throw new InvalidOperationException($"配置不能超过 {Ui.Bytes(maxBytes)}。");
        SetBusy(true);
        try
        {
            var document = await Command("desktop_save_config", new { content, expectedRevision = revision });
            ApplyDocument(document); await Session.SetDirtyAsync(false); Message("配置已保存。");
            if (reload && IsRunning)
            {
                try { EnsureEditable(); await Command("desktop_reload_config", new { force = false }); Message("配置已保存并应用。"); }
                catch (Exception error) { Message("配置已保存，但重新加载未完成：" + error.Message, true); }
            }
        }
        finally { SetBusy(false); }
    }
    private void SetBusy(bool value)
    {
        saving = value; ApplyInteractionState();
    }
    private void ApplyInteractionState()
    {
        var updating = Session.IsUpdating;
        overview.IsEnabled = editPane.IsEnabled = AllowDrop = !saving && !updating;
        editor.IsReadOnly = !writable || saving || updating;
        if (updating) Message("正在安装应用更新，配置编辑与导入已暂时锁定。请等待更新完成。");
        else if (updateWasActive) Message("应用更新已结束，可以继续编辑配置。");
        updateWasActive = updating;
    }
    private void EnsureEditable()
    {
        if (Session.IsUpdating) throw new InvalidOperationException("正在安装应用更新，请等待更新完成后再编辑、保存或导入配置。");
    }
    private async Task Reload()
    {
        EnsureEditable();
        if (!await CanLeaveAsync()) return;
        EnsureEditable();
        SetBusy(true);
        try { await Command("desktop_reload_config", new { force = false }); await ReadDocument(); Message(IsRunning ? "配置已重新加载。" : "配置已校验，内核启动时将使用此配置。"); }
        finally { SetBusy(false); }
    }
    private async Task Discard()
    {
        EnsureEditable();
        if (saving) throw new InvalidOperationException("配置操作正在进行，请稍候。");
        if (dirty && MessageBox.Show(Window.GetWindow(this), "放弃当前未保存的配置修改？", "放弃修改", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        await DiscardDraft(); Message("已读取磁盘上的配置。");
    }
    private async Task DiscardDraft()
    {
        EnsureEditable();
        SetBusy(true);
        try { await ReadDocument(true); await Session.SetDirtyAsync(false); }
        finally { SetBusy(false); }
    }
    public override async Task<bool> CanLeaveAsync()
    {
        if (saving) { Message("配置操作正在进行，请完成后再离开此页。"); return false; }
        if (!dirty) { await Session.SetDirtyAsync(false); return true; }
        if (Session.IsUpdating) { Message("正在安装应用更新，请等待更新完成后再处理配置草稿。"); return false; }
        var choice = MessageBox.Show(Window.GetWindow(this), "配置有未保存的修改，是否保存后继续？选择“否”将放弃这些修改。", "保存配置修改", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (choice == MessageBoxResult.Cancel) return false;
        try { if (choice == MessageBoxResult.Yes) await Save(false); else await DiscardDraft(); return true; }
        catch (Exception error) { Message(error.Message, true); return false; }
    }
    private async Task ChooseFile()
    {
        EnsureEditable();
        var dialog = new OpenFileDialog { Filter = "YAML 配置|*.yaml;*.yml|所有文件|*.*", CheckFileExists = true, Multiselect = false };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) await ImportFile(dialog.FileName);
    }
    private async Task ImportFile(string file)
    {
        EnsureEditable();
        if (!new[] { ".yaml", ".yml" }.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)) throw new InvalidOperationException("请导入 .yaml 或 .yml 配置文件。");
        if (new FileInfo(file).Length > maxBytes) throw new InvalidOperationException($"配置不能超过 {Ui.Bytes(maxBytes)}。");
        var content = await File.ReadAllTextAsync(file, new UTF8Encoding(false, true)); await Import(content, false);
    }
    private async Task ImportUrl()
    {
        EnsureEditable();
        var url = InputDialog.Show(this, "从 URL 导入", "输入 HTTP 或 HTTPS 订阅地址"); if (url is null) return; await Import(url.Trim(), true);
    }
    private async Task Paste()
    {
        EnsureEditable();
        var content = InputDialog.Show(this, "粘贴配置", "粘贴完整 YAML 配置", true); if (content is not null) await Import(content, false);
    }
    private async Task Import(string value, bool url)
    {
        EnsureEditable();
        if (!await CanLeaveAsync()) return;
        EnsureEditable();
        if (MessageBox.Show(Window.GetWindow(this), "导入将替换当前配置，原配置会由后台备份。继续导入？", "导入配置", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        if (!loaded) await ReadDocument(); EnsureEditable(); SetBusy(true);
        try
        {
            var document = url ? await Command("desktop_import_config_url", new { url = value, expectedRevision = revision }) : await Command("desktop_import_config", new { content = value, expectedRevision = revision });
            ApplyDocument(document); await Session.SetDirtyAsync(false); Message("配置已导入。点击“重新加载”应用到当前内核。");
        }
        finally { SetBusy(false); }
    }
    private async Task RefreshResources()
    {
        if (!IsRunning) { resourceUpdates.ItemsSource = null; return; }
        RenderResources(await Get("upgrade/check"));
    }
    private async Task UpdateResources()
    {
        EnsureEditable();
        if (!IsRunning) throw new InvalidOperationException("请先启动内核，再更新规则与 Geo 数据。");
        if (dirty) throw new InvalidOperationException("请先保存或放弃配置草稿，再更新资源。");
        var current = await Get("upgrade/check"); var instanceId = current.Text("instance_id");
        EnsureEditable();
        var headers = new Dictionary<string, string> { ["Idempotency-Key"] = Guid.NewGuid().ToString("N"), ["X-Mihomo-Instance-ID"] = instanceId };
        var result = await Session.ApiAsync(HttpMethod.Post, "upgrade/check", headers: headers); RenderResources(result); Message("更新任务已提交，可在下方查看各资源结果。");
    }
    private void RenderResources(JsonElement data) => resourceUpdates.ItemsSource = data.Get("items").Items().Select(item => new ResourceRow(item.Text("name"), ResourceStatus(item.Text("status")), item.Text("current_version"), item.Text("error", fallback: item.Text("reason")))).ToArray();
    private static string ResourceStatus(string status) => status switch { "pending" => "等待", "checking" => "检查中", "updated" => "已更新", "up_to_date" => "已是最新", "skipped" => "已跳过", "error" => "失败", "restarting" => "重启中", _ => status };
    protected override Task LiveAsync(CancellationToken cancellationToken) => PollAsync(TimeSpan.FromSeconds(5), RefreshResources, cancellationToken);
    public sealed record ResourceRow(string Name, string Status, string Current, string Message);
}
