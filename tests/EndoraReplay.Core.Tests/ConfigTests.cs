using EndoraReplay.Core.Config;
using EndoraReplay.Core.Input;
using EndoraReplay.Core.Model;
using EndoraReplay.Core.Remote;

namespace EndoraReplay.Core.Tests;

public class ConfigTests
{
    [Theory]
    [InlineData("1080p50", 1920, 1080, 50.0)]
    [InlineData("1080p59.94", 1920, 1080, 59.94)]
    [InlineData("720p50", 1280, 720, 50.0)]
    [InlineData("1080p25", 1920, 1080, 25.0)]
    public void Parses_video_formats(string name, int w, int h, double fps)
    {
        var f = VideoFormat.Parse(name);
        Assert.Equal(w, f.Width);
        Assert.Equal(h, f.Height);
        Assert.Equal(fps, f.FramesPerSecond, 2);
        Assert.Equal(name, f.Name);
    }

    [Fact]
    public void Default_settings_are_valid_and_round_trip()
    {
        using var dir = new TempDir();
        var s = new AppSettings();
        Assert.Empty(s.Validate());
        Assert.Equal(8, s.Atem.ReplayInput);
        Assert.False(s.Automation.Enabled); // never switch away from live without explicit configuration
        Assert.Equal(8, s.Sources.Count(x => x.Role == SourceRole.Camera));

        var path = Path.Combine(dir.Path, "settings.json");
        s.Atem.Address = "10.0.0.5";
        s.Save(path);
        var loaded = AppSettings.Load(path);
        Assert.Equal("10.0.0.5", loaded.Atem.Address);
        Assert.Equal(s.KeyBindings.Count, loaded.KeyBindings.Count);
    }

    [Fact]
    public void Validation_reports_problems()
    {
        var s = new AppSettings { VideoFormat = "banana" };
        s.Atem.ReplayInput = 0;
        s.Sources[0].Enabled = s.Sources[1].Enabled = true;
        s.Sources[0].DeviceId = s.Sources[1].DeviceId = "dev";
        s.Automation.TransitionIn = TransitionMode.EndoraStinger;
        var problems = s.Validate();
        Assert.Contains(problems, p => p.Contains("banana"));
        Assert.Contains(problems, p => p.Contains("replay input"));
        Assert.Contains(problems, p => p.Contains("more than one source"));
        Assert.Contains(problems, p => p.Contains("stinger"));
    }

    [Fact]
    public void Example_config_file_is_valid()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "config", "settings.example.json"))) dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        var s = AppSettings.Load(Path.Combine(dir!, "config", "settings.example.json"));
        Assert.Empty(s.Validate());
    }

    [Fact]
    public void Default_key_bindings_have_no_conflicts()
    {
        Assert.Empty(KeyBindingDefaults.FindConflicts(KeyBindingDefaults.All));
        var b = new Dictionary<string, string>(KeyBindingDefaults.All) { [OperatorActions.Tag] = "space" };
        var c = Assert.Single(KeyBindingDefaults.FindConflicts(b));
        Assert.Contains(OperatorActions.Replay, c.Actions);
    }

    [Fact]
    public void Access_codes_are_readable_and_compared_safely()
    {
        var code = AccessCodeGenerator.Create();
        Assert.Matches("^[2-9A-HJ-NP-Z]{4}-[2-9A-HJ-NP-Z]{4}$", code);
        Assert.True(AccessCodeGenerator.Matches(code, code.ToLowerInvariant()));
        Assert.False(AccessCodeGenerator.Matches(code, "AAAA-AAAA"));
        Assert.False(AccessCodeGenerator.Matches(code, null));
    }
}
