using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace EndoraReplay.App.Services;

/// <summary>
/// Endora licence key (same rules as the other Endora apps): the key comes from
/// tools.endoraweddingfilms.com, is checked with the Endora server, and works 7 days offline. No free trial.
/// </summary>
public static class Licence
{
    private const string Product = "replay";
    private const string Api = "https://www.endoraweddingfilms.com/perfex_crm/endora_bridge/shop/check";
    private const string Buy = "https://tools.endoraweddingfilms.com/#replay";
    private static readonly TimeSpan Grace = TimeSpan.FromDays(7);
    private static readonly TimeSpan Recheck = TimeSpan.FromHours(12);
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EndoraReplay", "licence.json");

    private sealed class State
    {
        public string Key { get; set; } = "";
        public bool Valid { get; set; }
        public DateTime LastOk { get; set; }
        public string? Expires { get; set; }
        public string Reason { get; set; } = "";
    }

    private static State Load()
    {
        try { return JsonSerializer.Deserialize<State>(File.ReadAllText(FilePath)) ?? new State(); }
        catch (Exception) { return new State(); }
    }

    private static void Save(State s)
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!); File.WriteAllText(FilePath, JsonSerializer.Serialize(s)); }
        catch (Exception) { /* read-only profile: keep in memory */ }
    }

    private static string Machine()
    {
        var raw = $"{Environment.MachineName}|{Environment.UserName}|{Environment.ProcessorCount}|replay";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash, 0, 16).ToLowerInvariant();
    }

    private static string? Problem(State s)
    {
        if (string.IsNullOrEmpty(s.Key)) return "Enter your licence key to start Endora Replay.";
        if (!s.Valid) return string.IsNullOrEmpty(s.Reason) ? "This licence key is not valid for Endora Replay." : s.Reason;
        if (!string.IsNullOrEmpty(s.Expires) && string.CompareOrdinal(DateTime.Now.ToString("yyyy-MM-dd"), s.Expires) > 0)
            return $"Your licence ended on {s.Expires}. Buy a new licence to keep using Endora Replay.";
        if (DateTime.UtcNow - s.LastOk > Grace) return "Connect to the internet once so Endora Replay can check your licence.";
        return null;
    }

    private static async Task<JsonNode?> CheckAsync(string key)
    {
        var url = $"{Api}?key={Uri.EscapeDataString(key)}&product={Product}&machine={Machine()}";
        var json = await Http.GetStringAsync(url);
        return JsonNode.Parse(json);
    }

    private static void Apply(State s, string key, JsonNode j)
    {
        s.Key = key;
        s.Valid = j["valid"]?.GetValue<bool>() == true;
        if (s.Valid) { s.LastOk = DateTime.UtcNow; s.Reason = ""; }
        else s.Reason = j["reason"]?.GetValue<string>() ?? "";
        s.Expires = j["expires"] is JsonValue v && v.TryGetValue<string>(out var e) ? e : null;
    }

    /// <summary>Re-check the saved key (network errors keep the last good answer).</summary>
    private static async Task RefreshAsync(State s, bool force)
    {
        if (string.IsNullOrEmpty(s.Key) || (!force && s.Valid && DateTime.UtcNow - s.LastOk < Recheck)) return;
        try
        {
            var j = await CheckAsync(s.Key);
            if (j?["ok"]?.GetValue<bool>() == true) { Apply(s, s.Key, j); Save(s); }
        }
        catch (Exception) { /* offline: grace period applies */ }
    }

    /// <summary>Called at start. Returns false when the user closed the window without a working key.</summary>
    public static async Task<bool> EnsureAsync()
    {
        var s = Load();
        await RefreshAsync(s, false);
        if (Problem(s) is null) return true;
        return ShowWindow(s, Problem(s)!);
    }

    /// <summary>Licence window: shows the key and lets the operator enter or replace it.</summary>
    public static bool ShowWindow() { var s = Load(); return ShowWindow(s, Problem(s) ?? $"Licensed with key {s.Key}. Enter another key to replace it."); }

    private static bool ShowWindow(State s, string message)
    {
        var gold = new SolidColorBrush(Color.FromRgb(200, 169, 106));
        var ink = new SolidColorBrush(Color.FromRgb(11, 10, 9));
        var win = new Window { Title = "Endora Replay – Licence", Width = 460, Height = 420, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterScreen, Background = ink };
        var panel = new StackPanel { Margin = new Thickness(36), VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(new TextBlock { Text = "Endora Replay", FontSize = 28, FontFamily = new FontFamily("Georgia"), Foreground = gold, HorizontalAlignment = HorizontalAlignment.Center });
        var msg = new TextBlock { Text = message, Foreground = Brushes.Gainsboro, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 10, 0, 16) };
        panel.Children.Add(msg);
        var box = new TextBox { FontSize = 16, FontFamily = new FontFamily("Consolas"), Padding = new Thickness(8), CharacterCasing = CharacterCasing.Upper, HorizontalContentAlignment = HorizontalAlignment.Center };
        panel.Children.Add(box);
        var go = new Button { Content = "Activate", Margin = new Thickness(0, 10, 0, 0), Padding = new Thickness(10), Background = gold, Foreground = ink, FontWeight = FontWeights.Bold, IsDefault = true };
        panel.Children.Add(go);
        var err = new TextBlock { Foreground = new SolidColorBrush(Color.FromRgb(255, 138, 128)), TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 8, 0, 0) };
        panel.Children.Add(err);
        var buy = new TextBlock { Text = "No key yet? Buy a licence", Foreground = gold, Cursor = Cursors.Hand, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 14, 0, 0), TextDecorations = TextDecorations.Underline };
        buy.MouseLeftButtonUp += (_, _) => { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Buy) { UseShellExecute = true }); } catch (Exception) { } };
        panel.Children.Add(buy);
        win.Content = panel;
        var ok = false;
        go.Click += async (_, _) =>
        {
            var key = box.Text.Trim().ToUpperInvariant();
            if (key.Length == 0) { err.Text = "Paste the licence key from your WhatsApp message."; return; }
            go.IsEnabled = false; err.Text = "Checking…";
            try
            {
                var j = await CheckAsync(key);
                if (j?["ok"]?.GetValue<bool>() == true && j["valid"]?.GetValue<bool>() == true)
                {
                    Apply(s, key, j); Save(s); ok = true; win.Close(); return;
                }
                err.Text = j?["reason"]?.GetValue<string>() ?? "This licence key is not valid for Endora Replay.";
            }
            catch (Exception) { err.Text = "Could not reach the licence server. Check the internet."; }
            go.IsEnabled = true;
        };
        win.ShowDialog();
        return ok || Problem(s) is null;
    }
}
