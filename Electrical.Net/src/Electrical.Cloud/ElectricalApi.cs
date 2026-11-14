using System.Text.Json.Nodes;
using Electrical.Core;
using Electrical.Core.Hosting;
using Electrical.Core.Network;
using Electrical.PowerFlow;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Electrical.Cloud;

/// <summary>Built-in study kinds.</summary>
public static partial class StudyCatalog
{
    public static StudyRunner Default(int workers = 2)
    {
        var r = new StudyRunner(workers);
        r.Register("powerflow", (net, p) =>
        {
            var method = Enum.TryParse<PowerFlowMethod>((string?)p["method"] ?? "NewtonRaphson", true, out var m) ? m : PowerFlowMethod.NewtonRaphson;
            var opts = new PowerFlowOptions { Method = method, EnforceQLimits = (bool?)p["enforceQLimits"] ?? false };
            return NetworkJson.ToJson(PowerFlowSolver.Solve(net, opts));
        });
        r.Register("dcpowerflow", (net, _) => NetworkJson.ToJson(PowerFlowSolver.Solve(net, new PowerFlowOptions { Method = PowerFlowMethod.DC })));
        r.Register("contingency", (net, _) => NetworkJson.ToJson(ContingencyAnalysis.RunN1(net)));
        RegisterExtra(r);
        return r;
    }

    static partial void RegisterExtra(StudyRunner runner);

    /// <summary>Resolves a network from a request body: <c>{"case":"case14"}</c>, <c>{"matpower":"..."}</c> or a full network JSON.</summary>
    public static PowerNetwork ResolveNetwork(JsonNode? body)
    {
        if (body is null) throw new ArgumentException("Request body is required.");
        if (body["case"] is JsonNode c) return TestCases.ByName((string)c!);
        if (body["matpower"] is JsonNode mp) return Matpower.Parse((string)mp!);
        if (body["network"] is JsonObject n) return NetworkJson.FromJson(n);
        return NetworkJson.FromJson(body);
    }
}

public static class CloudBuilderExtensions
{
    /// <summary>Registers the <see cref="StudyRunner"/> (2 workers) and an <see cref="InMemoryScenarioStore"/>.</summary>
    public static ElectricalBuilder AddCloud(this ElectricalBuilder b, int workers = 2)
    {
        if (b.TryAddModule("Cloud"))
        {
            b.Services.TryAddSingleton(_ => StudyCatalog.Default(workers));
            b.Services.TryAddSingleton<IScenarioStore, InMemoryScenarioStore>();
        }
        return b;
    }

    /// <summary>
    /// Maps the Electrical.Net REST API under <paramref name="prefix"/>:
    /// GET info, GET cases, GET cases/{name}, POST studies/{kind} (synchronous), POST jobs/{kind} (queued), GET jobs, GET jobs/{id},
    /// GET/PUT/DELETE scenarios/{name}. The tenant comes from the <c>X-Tenant</c> header (default "public").
    /// </summary>
    public static RouteGroupBuilder MapElectricalApi(this IEndpointRouteBuilder app, string prefix = "/api/electrical")
    {
        var g = app.MapGroup(prefix);
        static string Tenant(HttpContext ctx) => ctx.Request.Headers["X-Tenant"].FirstOrDefault() ?? "public";
        static IResult Json(JsonNode node, int status = 200) => Results.Content(node.ToJsonString(), "application/json", statusCode: status);
        static async Task<JsonObject> Body(HttpRequest req) =>
            req.ContentLength is 0 ? [] : (await JsonNode.ParseAsync(req.Body))?.AsObject() ?? [];

        g.MapGet("/info", () => Json(new JsonObject
        {
            ["name"] = ElectricalInfo.Name, ["version"] = ElectricalInfo.Version, ["tagline"] = ElectricalInfo.Tagline,
            ["credit"] = ElectricalInfo.CreditEn, ["kredit"] = ElectricalInfo.CreditId,
            ["modules"] = new JsonArray(ElectricalInfo.Modules.Select(m => (JsonNode)m).ToArray()),
        }));
        g.MapGet("/cases", () => Json(new JsonArray(TestCases.Names.Select(n => (JsonNode)n).ToArray())));
        g.MapGet("/cases/{name}", (string name) =>
        {
            try { return Json(NetworkJson.ToJson(TestCases.ByName(name))); }
            catch (ArgumentException e) { return Results.NotFound(e.Message); }
        });
        g.MapPost("/studies/{kind}", async (string kind, HttpRequest req, StudyRunner runner) =>
        {
            try
            {
                var body = await Body(req);
                var net = StudyCatalog.ResolveNetwork(body);
                return Json(runner.RunNow(kind, net, body["parameters"] as JsonObject));
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or KeyNotFoundException or System.Text.Json.JsonException)
            {
                return Results.BadRequest(e.Message);
            }
        });
        g.MapPost("/jobs/{kind}", async (string kind, HttpContext ctx, StudyRunner runner) =>
        {
            try
            {
                var body = await Body(ctx.Request);
                var job = await runner.SubmitAsync(Tenant(ctx), kind, StudyCatalog.ResolveNetwork(body), body["parameters"] as JsonObject);
                return Json(job.Summary(), StatusCodes.Status202Accepted);
            }
            catch (Exception e) when (e is ArgumentException or KeyNotFoundException or System.Text.Json.JsonException)
            {
                return Results.BadRequest(e.Message);
            }
        });
        g.MapGet("/jobs", (HttpContext ctx, StudyRunner runner) =>
            Json(new JsonArray(runner.List(Tenant(ctx)).Select(j => (JsonNode)j.Summary()).ToArray())));
        g.MapGet("/jobs/{id}", (string id, HttpContext ctx, StudyRunner runner) =>
        {
            var j = runner.Get(id);
            if (j is null || j.Tenant != Tenant(ctx)) return Results.NotFound();
            var s = j.Summary();
            s["result"] = j.Result?.DeepClone();
            return Json(s);
        });
        g.MapGet("/scenarios", (HttpContext ctx, IScenarioStore store) =>
            Json(new JsonArray(store.List(Tenant(ctx)).Select(n => (JsonNode)n).ToArray())));
        g.MapGet("/scenarios/{name}", (string name, HttpContext ctx, IScenarioStore store) =>
            store.Load(Tenant(ctx), name) is { } n ? Json(NetworkJson.ToJson(n)) : Results.NotFound());
        g.MapPut("/scenarios/{name}", async (string name, HttpContext ctx, IScenarioStore store) =>
        {
            var net = StudyCatalog.ResolveNetwork(await Body(ctx.Request));
            store.Save(Tenant(ctx), name, net);
            return Results.NoContent();
        });
        g.MapDelete("/scenarios/{name}", (string name, HttpContext ctx, IScenarioStore store) =>
            store.Delete(Tenant(ctx), name) ? Results.NoContent() : Results.NotFound());
        return g;
    }
}
