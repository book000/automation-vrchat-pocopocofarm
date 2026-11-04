using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Pocofarm.Core;

namespace Pocofarm.Web;

/// <summary>自己点検・診断 zip・設定ツール(プリセット/区分リセット/取り消し)のエンドポイント。DashboardServer から 1 行で登録する。</summary>
public static class ExtraEndpoints
{
    static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    static IResult JsonResult(object o, int status = 200) =>
        Results.Text(JsonSerializer.Serialize(o, Json), "application/json; charset=utf-8", statusCode: status);

    /// <summary>エンドポイントを登録する。</summary>
    public static void Map(WebApplication app, Paths paths, Config cfg)
    {
        app.MapGet("/api/selfcheck", () =>
        {
            try { if (File.Exists(paths.SelfCheck)) return Results.Text(File.ReadAllText(paths.SelfCheck), "application/json; charset=utf-8"); }
            catch (IOException) { }
            return JsonResult(new { done = false });
        });

        app.MapGet("/api/diag.zip", (HttpContext ctx) =>
        {
            if (!DashboardServer.Private(ctx)) return Results.StatusCode(403);
            using var ms = new MemoryStream();
            DiagBundle.Build(paths, ms);
            return Results.File(ms.ToArray(), "application/zip", $"pocofarm-diag-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        });

        app.MapGet("/api/config/tools", () => JsonResult(ConfigPresets.Tools(paths, cfg)));
        app.MapPost("/api/config/preset", (HttpContext ctx, string? name) => Change(ctx, "プリセット " + name, restart => ConfigPresets.ApplyPreset(paths, cfg, name ?? "", restart)));
        app.MapPost("/api/config/reset-section", (HttpContext ctx, string? name) => Change(ctx, "区分リセット " + name, restart => ConfigPresets.ResetSection(paths, cfg, name ?? "", restart)));
        app.MapPost("/api/config/undo", (HttpContext ctx) => Change(ctx, "設定の取り消し", restart => ConfigPresets.Undo(paths, cfg, restart, out _)));
    }

    static IResult Change(HttpContext ctx, string what, Func<List<string>, string?> action)
    {
        if (!DashboardServer.Private(ctx)) return Results.StatusCode(403);
        var restart = new List<string>();
        var err = action(restart);
        if (err is not null) return JsonResult(new { ok = false, error = err }, 400);
        Log.Info($"ダッシュボードから設定を変更: {what} ({ctx.Connection.RemoteIpAddress})");
        return JsonResult(new { ok = true, restart_required = restart });
    }
}
