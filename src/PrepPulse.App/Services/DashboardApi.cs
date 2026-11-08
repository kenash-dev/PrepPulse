using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using PrepPulse.Core;
using PrepPulse.Core.Engine;
using PrepPulse.Core.Reporting;
using PrepPulse.Data;

namespace PrepPulse.App.Services;

/// <summary>The local dashboard: one HTML page and one JSON endpoint, on 127.0.0.1 only.</summary>
public static class DashboardApi
{
    public static void Map(WebApplication app, CatalogService catalog, SqliteStore store, FocusEngine engine, IClock clock)
    {
        // Only answer to localhost names, so a web page can't reach the API through DNS rebinding.
        app.Use(async (ctx, next) =>
        {
            var host = ctx.Request.Host.Host;
            if (host is not ("127.0.0.1" or "localhost"))
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }
            ctx.Response.Headers["Cache-Control"] = "no-store";
            await next(ctx);
        });

        var index = Path.Combine(AppContext.BaseDirectory, "wwwroot", "index.html");
        app.MapGet("/", () => Results.Content(File.ReadAllText(index), "text/html; charset=utf-8"));
        app.MapGet("/api/dashboard", (string? plan) => Results.Json(Build(catalog, store, engine, clock, plan)));
        app.MapGet("/api/status", () => Results.Json(engine.Snapshot));
    }

    /// <summary>Stored data plus the still-open segment from the engine, so the current session shows live.</summary>
    public static DashboardModel Build(CatalogService catalog, SqliteStore store, FocusEngine? engine, IClock clock, string? plan)
    {
        var cat = catalog.Catalog;
        var now = clock.Now;
        var data = store.Load(DashboardBuilder.RangeStart(cat, now, clock.Zone), now);
        if (engine is not null)
        {
            var live = engine.Live();
            if (live.Segments.Count > 0 || live.Gaps.Count > 0)
                data = data with
                {
                    Segments = data.Segments.Concat(live.Segments).ToList(),
                    Gaps = data.Gaps.Concat(live.Gaps).ToList(),
                };
        }
        var selected = string.IsNullOrWhiteSpace(plan) || plan == "all" ? null : plan;
        var model = DashboardBuilder.Build(cat, data, selected, now, clock.Zone, engine?.Snapshot);
        return catalog.SettingsError is { } err ? model with { PlanErrors = model.PlanErrors.Append(err).ToList() } : model;
    }

    /// <summary>The configured port if it's free, otherwise any free port.</summary>
    public static int ChoosePort(int preferred)
    {
        foreach (var port in new[] { preferred, 0 })
        {
            try
            {
                var listener = new TcpListener(IPAddress.Loopback, port);
                listener.Start();
                var chosen = ((IPEndPoint)listener.LocalEndpoint).Port;
                listener.Stop();
                return chosen;
            }
            catch (SocketException) { }
        }
        return preferred;
    }
}
