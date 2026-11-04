using System.IO.Compression;
using System.Text.Json.Nodes;
using Pocofarm.Core;
using Pocofarm.Web;

namespace Pocofarm.Tests;

/// <summary>設定の検証・プリセット・取り消し、診断 zip、掃除のテスト。</summary>
public class OpsTests : IDisposable
{
    readonly string dir = Directory.CreateTempSubdirectory("pocofarm-ops").FullName;

    public void Dispose() => Directory.Delete(dir, true);

    Paths NewPaths()
    {
        var p = new Paths(dir);
        Directory.CreateDirectory(p.Data);
        return p;
    }

    // ---- 設定の検証 ----

    [Fact]
    public void Validate_DefaultsAreValid() => Assert.Empty(new Config().Validate());

    [Fact]
    public void RangeTable_KeysExist()
    {
        var keys = Config.AllKeys().ToHashSet();
        Assert.All(Config.RangeKeys(), k => Assert.Contains(k, keys));
    }

    [Fact]
    public void Validate_RejectsZeroDivisorsAndBadRelations()
    {
        Assert.NotEmpty(new Config { WorkHeight = 0 }.Validate());
        Assert.NotEmpty(new Config { CaptureFps = 0 }.Validate());
        Assert.NotEmpty(new Config { ReelToCastMin = 1.0 }.Validate());      // floor(1.2) より小さい
        Assert.NotEmpty(new Config { NoBiteMin = 100 }.Validate());
        Assert.NotEmpty(new Config { ToastRoi = [0.5, 0.1, 0.2, 0.9] }.Validate());
        Assert.NotEmpty(new Config { DashboardHost = "not-an-ip" }.Validate());
    }

    [Fact]
    public void Load_InvalidValuesFallBackToDefault()
    {
        var f = Path.Combine(dir, "config.json");
        File.WriteAllText(f, """{"work_height":0,"capture_fps":"abc","reel_to_cast_min":1.0,"pause_vk":19}""");
        var c = Config.Load(f, warn: false);
        var d = new Config();
        Assert.Equal(d.WorkHeight, c.WorkHeight);
        Assert.Equal(d.CaptureFps, c.CaptureFps);
        Assert.Equal(d.ReelToCastMin, c.ReelToCastMin);
        Assert.Equal(19, c.PauseVk);
    }

    [Fact]
    public void LoadSafe_MalformedJson_UsesLastGoodThenDefaults()
    {
        var f = Path.Combine(dir, "config.json");
        File.WriteAllText(f, """{"pause_vk":19}""");
        Assert.Equal(19, Config.LoadSafe(f).PauseVk);
        File.WriteAllText(f, "{ broken");
        Assert.Equal(19, Config.LoadSafe(f).PauseVk);
        File.Delete(Path.Combine(dir, "data", "config.lastgood.json"));
        Assert.Equal(new Config().PauseVk, Config.LoadSafe(f).PauseVk);
    }

    [Fact]
    public void Apply_RejectsRelationViolation_AndKeepsOldValues()
    {
        var c = new Config();
        var err = c.Apply(new JsonObject { ["no_bite_min"] = 500 }, []);
        Assert.NotNull(err);
        Assert.Equal(new Config().NoBiteMin, c.NoBiteMin);
        Assert.NotNull(c.Apply(new JsonObject { ["work_height"] = 0 }, []));
    }

    // ---- プリセット・取り消し ----

    [Fact]
    public void Presets_UseRealKeysAndPassValidation()
    {
        foreach (var (name, p) in ConfigPresets.All)
        {
            var c = new Config();
            Assert.Null(c.Apply((JsonObject)JsonNode.Parse(p.Patch)!, []));
            Assert.Empty(c.Validate());
            Assert.NotEmpty(c.DifferingKeys());
        }
    }

    [Fact]
    public void Preset_Undo_RestoresPreviousValues()
    {
        var paths = NewPaths();
        var c = new Config();
        Assert.Null(ConfigPresets.ApplyPreset(paths, c, "low-gpu", []));
        Assert.Equal(30, c.CaptureFps);
        Assert.True(File.Exists(paths.ConfigHistory));
        Assert.Null(ConfigPresets.Undo(paths, c, [], out var keys));
        Assert.Contains("capture_fps", keys);
        Assert.Equal(new Config().CaptureFps, c.CaptureFps);
        Assert.NotNull(ConfigPresets.Undo(paths, c, [], out _));        // 履歴は尽きた
    }

    [Fact]
    public void ResetSection_RestoresDefaultsOnlyInThatSection()
    {
        var paths = NewPaths();
        var c = new Config();
        Assert.Null(c.Apply(new JsonObject { ["recast_delay"] = 3.0, ["ocr_interval"] = 0.3 }, []));
        Assert.Equal("fishing", Config.SectionOf("recast_delay"));
        Assert.Null(ConfigPresets.ResetSection(paths, c, "fishing", []));
        Assert.Equal(new Config().RecastDelay, c.RecastDelay);
        Assert.Equal(0.3, c.OcrInterval);
        Assert.NotNull(ConfigPresets.ResetSection(paths, c, "nope", []));
    }

    [Fact]
    public void History_IsCapped()
    {
        var paths = NewPaths();
        var c = new Config();
        for (int i = 0; i < 30; i++) Assert.Null(ConfigPresets.ApplyAndSave(paths, c, new JsonObject { ["recast_delay"] = 2.0 + i * 0.01 }, []));
        Assert.Equal(20, File.ReadAllLines(paths.ConfigHistory).Length);
    }

    // ---- 診断 zip ----

    [Fact]
    public void Redact_RemovesSecretKeysRecursively()
    {
        var json = DiagBundle.RedactedJson("""{"update_source":"\\\\nas\\x.exe","webhook_url":"https://x","Api_Token":"t","nested":{"my_password":"p","keep":1},"capture_fps":60}""")!;
        Assert.DoesNotContain("update_source", json);
        Assert.DoesNotContain("webhook_url", json);
        Assert.DoesNotContain("Api_Token", json);
        Assert.DoesNotContain("my_password", json);
        Assert.Contains("capture_fps", json);
        Assert.Contains("keep", json);
        Assert.Null(DiagBundle.RedactedJson("{ broken"));
    }

    [Fact]
    public void DiagBundle_ContainsExpectedEntries()
    {
        var paths = NewPaths();
        File.WriteAllLines(paths.Log, Enumerable.Range(1, 800).Select(i => "line" + i));
        File.WriteAllText(paths.CrashLog, "crash!\n");
        File.WriteAllText(paths.Status, """{"state":"running"}""");
        File.WriteAllText(paths.ConfigFile, """{"update_source":"\\\\nas\\secret.exe","capture_fps":60}""");
        Directory.CreateDirectory(paths.Evidence);
        for (int i = 0; i < 12; i++)
        {
            var jpg = Path.Combine(paths.Evidence, $"e{i:00}.jpg");
            File.WriteAllBytes(jpg, [1, 2, 3]);
            File.WriteAllText(Path.ChangeExtension(jpg, ".json"), "{}");
            File.SetLastWriteTimeUtc(jpg, DateTime.UtcNow.AddMinutes(-i));
        }

        using var ms = new MemoryStream();
        DiagBundle.Build(paths, ms);
        using var zip = new ZipArchive(new MemoryStream(ms.ToArray()));
        var names = zip.Entries.Select(e => e.FullName).ToHashSet();
        Assert.Superset(new HashSet<string> { "fishing.log.tail.txt", "crash.log.tail.txt", "status.json", "config.redacted.json", "system.txt", "README.txt" }, names);
        Assert.Equal(20, names.Count(n => n.StartsWith("evidence/")));         // jpg+json の 10 組
        Assert.Contains("evidence/e00.jpg", names);
        Assert.DoesNotContain("evidence/e11.jpg", names);

        string Read(string n) { using var r = new StreamReader(zip.GetEntry(n)!.Open()); return r.ReadToEnd(); }
        var tail = Read("fishing.log.tail.txt");
        Assert.Contains("line800", tail);
        Assert.DoesNotContain("line299\n", tail);
        Assert.Equal(500, tail.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        var cfg = Read("config.redacted.json");
        Assert.DoesNotContain("secret", cfg);
        Assert.Contains("capture_fps", cfg);
    }

    [Fact]
    public void DiagBundle_EmptyDataDir_StillBuilds()
    {
        using var ms = new MemoryStream();
        DiagBundle.Build(new Paths(dir), ms);
        using var zip = new ZipArchive(new MemoryStream(ms.ToArray()));
        Assert.NotNull(zip.GetEntry("README.txt"));
    }

    // ---- 掃除 ----

    [Fact]
    public void PruneArchive_DeletesOnlyOldEvents()
    {
        var a = Path.Combine(dir, "archive");
        Directory.CreateDirectory(a);
        var now = new DateTime(2026, 6, 30, 12, 0, 0);
        File.WriteAllText(Path.Combine(a, "events_20260101_000000.jsonl"), "");
        File.WriteAllText(Path.Combine(a, "events_20260629_000000.jsonl"), "");
        File.WriteAllText(Path.Combine(a, "other.txt"), "");
        Assert.Equal(0, Housekeeping.PruneArchive(a, 0, now));
        Assert.Equal(1, Housekeeping.PruneArchive(a, 30, now));
        Assert.False(File.Exists(Path.Combine(a, "events_20260101_000000.jsonl")));
        Assert.True(File.Exists(Path.Combine(a, "events_20260629_000000.jsonl")));
        Assert.True(File.Exists(Path.Combine(a, "other.txt")));
    }

    [Fact]
    public void LogAppend_RotatesLargeFile()
    {
        var f = Path.Combine(dir, "crash.log");
        File.WriteAllBytes(f, new byte[5_000_001]);
        Log.Append(f, "new\n");
        Assert.True(File.Exists(f + ".1"));
        Assert.Equal("new\n", File.ReadAllText(f));
    }
}
