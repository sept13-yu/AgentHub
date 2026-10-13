using System.Text.Json;
using System.Text.Json.Serialization;
using AgentHub.Core.ProxyCore;

namespace AgentHub.Shell;

public partial class DesktopQuotaWindow
{
    private void HandleMessage(string msg)
    {
        if (msg == "dq:close")
        {
            HideFromUser();
            return;
        }
        if (msg == "dq:expanded")
        {
            ApplyLayout(expanded: true, persist: true);
            return;
        }
        if (msg == "dq:compact")
        {
            ApplyLayout(expanded: false, persist: true);
            return;
        }
        const string appearancePrefix = "dq:appearance:";
        if (msg.StartsWith(appearancePrefix, StringComparison.Ordinal))
        {
            ApplyAppearance(msg[appearancePrefix.Length..]);
            return;
        }
        const string windowsPrefix = "dq:windows:";
        if (msg.StartsWith(windowsPrefix, StringComparison.Ordinal))
        {
            try
            {
                var ids = JsonSerializer.Deserialize<List<string>>(msg[windowsPrefix.Length..]);
                _config.DesktopQuota.WindowIds = DesktopQuotaSettings.NormalizeWindowIds(ids);
                PersistNow();
            }
            catch (JsonException) { }
        }
    }

    private async void ApplyAppearance(string json)
    {
        try
        {
            var appearance = JsonSerializer.Deserialize<DialAppearanceMessage>(json);
            if (appearance?.Size is not int size || appearance.Glow is not double glow || !double.IsFinite(glow) ||
                appearance.ArcMode is not ("quota" or "full")) return;
            var settings = _config.DesktopQuota;
            settings.DialSize = Math.Clamp(size, DesktopQuotaSettings.MinDialSize, DesktopQuotaSettings.MaxDialSize);
            settings.DialGlow = Math.Clamp(glow, 0d, 0.6d);
            settings.DialArcMode = appearance.ArcMode;
            ApplyLayout(settings.Expanded, persist: false);
            PersistNow();
            await PushStateAsync();
        }
        catch (JsonException) { }
        catch (Exception ex)
        {
            HubLog.Write("[desktop-quota] 外观状态同步失败 " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    private async System.Threading.Tasks.Task PushStateAsync()
    {
        if (!_coreReady) return;
        var payload = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["expanded"] = _config.DesktopQuota.Expanded,
            ["windowIds"] = _config.DesktopQuota.WindowIds,
            ["theme"] = _theme,
            ["size"] = _config.DesktopQuota.DialSize,
            ["glow"] = _config.DesktopQuota.DialGlow,
            ["arcMode"] = _config.DesktopQuota.DialArcMode,
        });
        await Web.CoreWebView2.ExecuteScriptAsync(
            "window.__AGENTHUB_DQ_STATE__=" + payload + ";" +
            "try{var s=window.__AGENTHUB_DQ_STATE__;" +
            "if(s&&s.theme){document.documentElement.setAttribute('data-theme',s.theme);" +
            "window.dispatchEvent(new CustomEvent('agenthub-theme',{detail:'theme:'+s.theme}));}" +
            "window.dispatchEvent(new Event('agenthub-dq-state'));}catch(e){}");
    }

    private sealed record DialAppearanceMessage(
        [property: JsonPropertyName("size")] int? Size,
        [property: JsonPropertyName("glow")] double? Glow,
        [property: JsonPropertyName("arcMode")] string? ArcMode);
}
