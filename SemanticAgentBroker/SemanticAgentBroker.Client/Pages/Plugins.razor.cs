using System.Net.Http.Json;

namespace SemanticAgentBroker.Client.Pages;

public partial class Plugins : IAsyncDisposable
{
    private List<PluginInfo>? plugins;
    private string? expandedPlugin;
    private string pluginSearchTerm = "";
    private bool _isDisposed;

    public ValueTask DisposeAsync()
    {
        _isDisposed = true;
        return ValueTask.CompletedTask;
    }

    private IEnumerable<PluginInfo> FilteredPlugins(IEnumerable<PluginInfo> source)
    {
        if (string.IsNullOrWhiteSpace(pluginSearchTerm)) return source;
        var terms = pluginSearchTerm.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                      .Select(t => t.ToLowerInvariant()).ToList();
        return source.Where(p => terms.All(term => p.DisplayName.ToLowerInvariant().Contains(term)));
    }

    private class PluginInfo
    {
        public string Id { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string Description { get; set; } = "";
        public string Version { get; set; } = "";
        public string Author { get; set; } = "";
        public string? Icon { get; set; }
        public List<string>? Tags { get; set; }
        public bool IsEnabled { get; set; }
        public bool IsBuiltIn { get; set; }
        public string Category { get; set; } = "Custom";
        public string LoadState { get; set; } = "Discovered";
        public string? LastError { get; set; }
        public string? MethodManifestJson { get; set; }
        public List<MethodInfo>? Methods { get; set; }

        public string DllFileName { get; set; } = "";
        public List<string> RequiredPlugins { get; set; } = new();
        public string TasksJson { get; set; } = "[]";
        public List<PluginTaskDto>? PluginTasks { get; set; }
    }

    private class PluginTaskDto
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public bool IsEnabled { get; set; }
    }

    private class MethodInfo
    {
        public string Id { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string Description { get; set; } = "";
        public bool IsDangerous { get; set; }
        public string? ReturnDescription { get; set; }
        public List<ParamInfo>? Parameters { get; set; }
    }

    private class ParamInfo
    {
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public string Type { get; set; } = "";
        public bool IsRequired { get; set; }
    }

    protected override async Task OnInitializedAsync()
    {
        await LoadPlugins();
    }

    private async Task LoadPlugins()
    {
        try
        {
            plugins = await Http.GetFromJsonAsync<List<PluginInfo>>("api/plugins");
            if (!_isDisposed && plugins != null)
            {
                foreach (var p in plugins)
                {
                    if (!string.IsNullOrEmpty(p.MethodManifestJson) && p.MethodManifestJson != "[]")
                    {
                        try
                        {
                            p.Methods = System.Text.Json.JsonSerializer.Deserialize<List<MethodInfo>>(
                                p.MethodManifestJson,
                                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                        }
                        catch { p.Methods = new(); }
                    }
                    else
                    {
                        p.Methods = new();
                    }
                }
            }
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); }
    }

    private async Task TogglePlugin(string pluginId)
    {
        try
        {
            await Http.PostAsync($"api/plugins/{pluginId}/toggle", null);
            await LoadPlugins();
            if (!_isDisposed) StateHasChanged();
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); }
    }

    private async Task ToggleTask(string pluginId, string taskId)
    {
        try
        {
            var response = await Http.PostAsync($"api/plugins/{pluginId}/tasks/{taskId}/toggle", null);
            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<PluginTaskDto>();
                var p = plugins?.FirstOrDefault(x => x.Id == pluginId);
                var t = p?.PluginTasks?.FirstOrDefault(x => x.Id == taskId);
                if (t != null && result != null)
                {
                    t.IsEnabled = result.IsEnabled;
                }
                if (!_isDisposed) StateHasChanged();
            }
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); }
    }

    private async Task ReloadPlugin(string pluginId)
    {
        try
        {
            await Http.PostAsync($"api/plugins/{pluginId}/reload", null);
            await LoadPlugins();
            if (!_isDisposed) StateHasChanged();
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); }
    }

    private bool isRescanning = false;

    private async Task RescanPlugins()
    {
        if (isRescanning) return;
        isRescanning = true;
        if (!_isDisposed) StateHasChanged();

        try
        {
            await Http.PostAsync("api/plugins/reload", null);
            await LoadPlugins();
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); }
        finally
        {
            isRescanning = false;
            if (!_isDisposed) StateHasChanged();
        }
    }

    private async Task ToggleExpand(string pluginId)
    {
        if (expandedPlugin == pluginId)
        {
            expandedPlugin = null;
        }
        else
        {
            expandedPlugin = pluginId;
            var p = plugins?.FirstOrDefault(x => x.Id == pluginId);
            if (p != null)
            {
                try
                {
                    p.PluginTasks = await Http.GetFromJsonAsync<List<PluginTaskDto>>($"api/plugins/{pluginId}/tasks");
                }
                catch (Exception ex) { Console.Error.WriteLine(ex); }
            }
        }
    }

    private string GetPluginCardClass(PluginInfo p) => (p.Category, p.LoadState) switch
    {
        ("BuiltInRequired", _) => "required-card",
        (_, "NotLoaded") => "not-loaded",
        (_, "Discovered") => p.IsEnabled ? "" : "discovered",
        _ => p.IsEnabled ? "" : "disabled-plugin"
    };

    private string GetPluginIconClass(PluginInfo p) => p.LoadState switch
    {
        "Loaded" => "icon-loaded",
        "NotLoaded" => "icon-not-loaded",
        _ => "icon-discovered"
    };

    private string GetLoadStateLabel(string state) => state switch
    {
        "Loaded" => "● Loaded",
        "NotLoaded" => "○ Not Loaded",
        "Discovered" => "◇ New",
        _ => state
    };

    private string GetCategoryLabel(string category) => category switch
    {
        "BuiltInRequired" => "REQUIRED",
        "BuiltInOptional" => "OPTIONAL",
        "Custom" => "CUSTOM",
        _ => category
    };

    private string GetCategoryBadgeClass(string category) => category switch
    {
        "BuiltInRequired" => "cat-required",
        "BuiltInOptional" => "cat-optional",
        "Custom" => "cat-custom",
        _ => "cat-custom"
    };
}
