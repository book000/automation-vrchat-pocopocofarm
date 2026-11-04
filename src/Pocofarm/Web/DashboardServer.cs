using System.Net;
using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Pocofarm.Core;
using Pocofarm.Farm;
using Pocofarm.Platform;

namespace Pocofarm.Web;

/// <summary>LAN ダッシュボード(Kestrel)。実行状態・釣果・ログ・証拠画像を HTTP で配信する。画面は exe に埋め込んだ単一ページ。</summary>
public static class DashboardServer
{
    static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    static IResult JsonResult(object o) =>
        Results.Text(JsonSerializer.Serialize(o, Json), "application/json; charset=utf-8");

    static byte[] Page()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("web.index.html")!;
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>起動する。ホストの停止は戻り値を Dispose/StopAsync する。</summary>
    public static async Task<WebApplication> StartAsync(Paths paths, Config cfg, string host, int port)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Parse(host), port));
        var app = builder.Build();
        var dash = new Dashboard(paths, cfg);
        var page = Page();
        Directory.CreateDirectory(paths.Data);

        app.Use(async (ctx, next) => { ctx.Response.Headers.CacheControl = "no-store"; await next(); });
        // 全リクエスト共通: LAN 内のアドレスからのみ受け付ける。Host は IP かローカル名だけ(DNS リバインディング対策)。
        // 書き込み系は別オリジンのページからの送信(CSRF)を拒否する。Origin が無い(curl など)場合は通す
        app.Use(async (ctx, next) =>
        {
            if (!Private(ctx)) { ctx.Response.StatusCode = 403; return; }
            if (!HostAllowed(ctx.Request.Host.Host)) { ctx.Response.StatusCode = 421; return; }
            if (!HttpMethods.IsGet(ctx.Request.Method) && !HttpMethods.IsHead(ctx.Request.Method)
                && ctx.Request.Headers.Origin.ToString() is { Length: > 0 } origin
                && !(Uri.TryCreate(origin, UriKind.Absolute, out var o) && string.Equals(o.Authority, ctx.Request.Host.ToString(), StringComparison.OrdinalIgnoreCase)))
            { ctx.Response.StatusCode = 403; return; }
            await next();
        });
        app.MapGet("/", () => Results.Bytes(page, "text/html; charset=utf-8"));
        app.MapGet("/index.html", () => Results.Bytes(page, "text/html; charset=utf-8"));
        app.MapGet("/api/state", () => JsonResult(dash.BuildState()));
        app.MapGet("/api/evidence", (int? limit, string? q) => JsonResult(dash.ListEvidence(limit ?? 48, q ?? "")));
        app.MapGet("/api/events", (string? outcome, int? limit) => JsonResult(dash.ListAttempts(outcome ?? "", limit ?? 200)));
        app.MapGet("/api/archives", () => JsonResult(dash.ListArchives()));
        app.MapGet("/api/archive", (string? name) => dash.ArchiveDetail(name ?? "") is { } d ? JsonResult(d) : Results.NotFound("not found"));
        app.MapGet("/farm_live.jpg", () => File.Exists(paths.FarmLive) ? Results.Bytes(ReadShared(paths.FarmLive), "image/jpeg") : Results.NotFound());
        app.MapGet("/live.jpg", () => File.Exists(paths.Live) ? Results.Bytes(ReadShared(paths.Live), "image/jpeg") : Results.NotFound());
        var farm = new FarmShots(paths, cfg, dash);
        _ = farm.RunAsync(app.Lifetime.ApplicationStopping);
        // 撮影・画角調整の状況だけを返す軽い問い合わせ(ダッシュボードが短い間隔で呼ぶ)
        app.MapGet("/api/farm/phase", () => JsonResult(new { phase = farm.Phase, note = farm.Note, busy = SpoutCamera.Gate.CurrentCount == 0 }));
        app.MapGet("/api/farm", (int? limit) => JsonResult(new { items = farm.List(Math.Clamp(limit ?? 24, 1, 200)), interval = cfg.FarmShotInterval, inset = new[] { cfg.FarmInsetX, cfg.FarmInsetY }, water_min = cfg.FarmWaterMinChange, now = EventLog.Now(), next_at = farm.NextAt, streaming = SpoutCamera.IsStreaming(), busy = SpoutCamera.Gate.CurrentCount == 0, phase = farm.Phase, note = farm.Note, error = farm.LastError }));
        app.MapGet("/farm/{name}", (HttpContext ctx, string name) =>
        {
            var path = Path.Combine(paths.Farm, name);
            if (!FarmShots.SafeName(name) || !File.Exists(path)) return Results.NotFound("not found");
            ctx.Response.Headers.CacheControl = "max-age=600";
            return Results.Bytes(ReadShared(path), "image/jpeg");
        });
        app.MapGet("/evidence/{name}", (HttpContext ctx, string name) =>
        {
            var path = Path.Combine(paths.Evidence, name);
            if (!Dashboard.SafeName(name) || !File.Exists(path)) return Results.NotFound("not found");
            ctx.Response.Headers.CacheControl = "max-age=60";
            return Results.Bytes(ReadShared(path), name.EndsWith(".jpg", StringComparison.Ordinal) ? "image/jpeg" : "application/json");
        });
        // 操作系は LAN(プライベートアドレス)からのみ受け付ける
        app.MapPost("/api/pause", (HttpContext ctx, string? on) =>
        {
            if (!Private(ctx)) return Results.StatusCode(403);
            bool pause = (on ?? "1") == "1";
            dash.RequestPause(pause);
            Log.Info($"ダッシュボードから{(pause ? "一時停止" : "再開")}要求 ({ctx.Connection.RemoteIpAddress})");
            return JsonResult(new { paused = pause });
        });
        app.MapPost("/api/farm/shoot", async (HttpContext ctx) =>
        {
            if (!Private(ctx)) return Results.StatusCode(403);
            Log.Info($"ダッシュボードからファーム撮影 ({ctx.Connection.RemoteIpAddress})");
            var err = await farm.ShootNowAsync();
            return JsonResult(new { ok = err is null, error = err });
        });
        app.MapPost("/api/farm/calibrate", async (HttpContext ctx) =>
        {
            if (!Private(ctx)) return Results.StatusCode(403);
            Log.Info($"ダッシュボードから画角調整 ({ctx.Connection.RemoteIpAddress})");
            var err = await farm.ShootNowAsync(calibrate: true);
            return JsonResult(new { ok = err is null, error = err });
        });
        app.MapGet("/api/config", () => JsonResult(new { items = cfg.Describe(), file = paths.ConfigFile }));
        // 設定変更: {"キー": 値, ...}(null で既定値へ戻す)。検証に通れば即座に反映し、config.json へ保存する
        var configGate = new object();
        app.MapPost("/api/config", async (HttpContext ctx) =>
        {
            if (!Private(ctx)) return Results.StatusCode(403);
            JsonObject? patch;
            try { patch = await JsonNode.ParseAsync(ctx.Request.Body) as JsonObject; }
            catch (JsonException) { patch = null; }
            if (patch is null || patch.Count == 0) return Results.Text(JsonSerializer.Serialize(new { ok = false, error = "JSON オブジェクトで送ってください" }, Json), "application/json; charset=utf-8", statusCode: 400);
            var restart = new List<string>();
            string? err;
            lock (configGate)
            {
                err = cfg.Apply(patch, restart);
                if (err is null) AtomicFile.Write(paths.ConfigFile, JsonSerializer.SerializeToUtf8Bytes(cfg, Config.Json));
            }
            if (err is not null) return Results.Text(JsonSerializer.Serialize(new { ok = false, error = err }, Json), "application/json; charset=utf-8", statusCode: 400);
            Log.Info($"ダッシュボードから設定を変更: {string.Join(", ", patch.Select(kv => kv.Key))} ({ctx.Connection.RemoteIpAddress})");
            return JsonResult(new { ok = true, restart_required = restart });
        });
        app.MapPost("/api/neko_ack", (HttpContext ctx) =>
        {
            if (!Private(ctx)) return Results.StatusCode(403);
            dash.RequestNekoAck();
            return JsonResult(new { ack = true });
        });
        app.MapPost("/api/reset", (HttpContext ctx) =>
        {
            if (!Private(ctx)) return Results.StatusCode(403);
            dash.RequestReset();
            Log.Warn($"ダッシュボードからデータリセット要求 ({ctx.Connection.RemoteIpAddress})");
            return JsonResult(new { reset = true });
        });

        MapManualControl(app, paths);

        await app.StartAsync();
        Log.Info($"ダッシュボード起動: http://{host}:{port}/");
        return app;
    }

    static bool HostAllowed(string host)
    {
        host = host.Trim('[', ']');
        return IPAddress.TryParse(host, out _) || host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase);
    }

    static bool Private(HttpContext ctx)
    {
        var ip = ctx.Connection.RemoteIpAddress;
        if (ip is null) return false;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip)) return true;
        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6) return ip.IsIPv6LinkLocal || ip.IsIPv6UniqueLocal;
        var b = ip.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254);
    }

    /// <summary>書き込み中のファイルでも共有読みで全体を読む。</summary>
    static byte[] ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var buf = new byte[fs.Length];
        fs.ReadExactly(buf);
        return buf;
    }

    // ---------------------------------------------------------------- 手動操作(再投げ・今すぐ引く・視点・ドライラン・予約停止)

    /// <summary>手動操作を control.json へ書く。受け付けるかの判断(一時停止中・ドライラン中は拒否)は釣り側が行う。</summary>
    static void MapManualControl(WebApplication app, Paths paths)
    {
        app.MapPost("/api/control", (HttpContext ctx, string? cmd, string? arg, int? amount) =>
        {
            if (!Private(ctx)) return Results.StatusCode(403);
            if (cmd is not ("recast" or "reel_now" or "nudge" or "dry_run" or "pause_after_result" or "pause_until"))
                return Results.BadRequest("unknown cmd");
            Pocofarm.Fishing.ControlChannel.WriteCommand(paths, cmd, arg, amount ?? 0);
            Log.Info($"ダッシュボードから手動操作 {cmd} {arg} ({ctx.Connection.RemoteIpAddress})");
            return JsonResult(new { ok = true, cmd });
        });
    }
}
