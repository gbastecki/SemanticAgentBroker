using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SemanticAgentBroker.Api.Data;
using SemanticAgentBroker.Api.Features.Agents.Models;
using SemanticAgentBroker.Core.Features.Agents;
using SemanticAgentBroker.Core.Features.PluginHost;
using SemanticAgentBroker.Shared.Constants;
using SemanticAgentBroker.Shared.Domain;

namespace SemanticAgentBroker.Api.Features.Agents.Endpoints;

public static class AgentEndpoints
{
    public static void MapAgentEndpoints(this IEndpointRouteBuilder app)
    {
        // List all agents
        app.MapGet(ApiRoutes.AgentsBase, (IAgentRegistry registry) =>
        {
            return registry.GetAll().Select(a => new
            {
                a.Id,
                a.Name,
                a.Description,
                a.Icon,
                a.AccentClass,
                a.IsEnabled,
                a.IsSeeded,
                a.CreatedAt,
                MethodCount = a.MethodBindings.Count,
                Plugins = a.MethodBindings.Select(mb => mb.PluginId).Distinct().ToList()
            });
        });

        // Get agent details
        app.MapGet(ApiRoutes.AgentByName, async (string name, AppDbContext db) =>
        {
            var agent = await db.AgentDefinitions
                .Include(a => a.MethodBindings)
                .FirstOrDefaultAsync(a => a.Name == name);
            if (agent == null) return Results.NotFound();
            return Results.Ok(agent);
        });

        var adminGroup = app.MapGroup("");
        adminGroup.AddEndpointFilter(async (context, next) =>
        {
            var session = context.HttpContext.Items["SessionInfo"] as SemanticAgentBroker.Api.Features.Auth.SessionInfo;
            if (session == null || session.Role != "Admin") return Results.Forbid();
            return await next(context);
        });

        // Create a new agent
        adminGroup.MapPost(ApiRoutes.AgentsBase, async ([FromBody] CreateAgentRequest req, AppDbContext db, IAgentRegistry registry) =>
        {
            if (string.IsNullOrWhiteSpace(req.Name))
                return Results.BadRequest("Name is required.");
            if (await db.AgentDefinitions.AnyAsync(a => a.Name.ToLower() == req.Name.ToLower()))
                return Results.BadRequest($"Agent '{req.Name}' already exists.");

            var agent = new AgentDefinition
            {
                Name = req.Name,
                Description = req.Description ?? "",
                Icon = req.Icon ?? "smart_toy",
                AccentClass = req.AccentClass ?? "indigo",
                SystemInstruction = req.SystemInstruction ?? "",
                IsEnabled = true,
                IsSeeded = false,
                MethodBindings = (req.Methods ?? []).Select(m => new AgentMethodBinding
                {
                    PluginId = m.PluginId,
                    MethodId = m.MethodId
                }).ToList()
            };

            db.AgentDefinitions.Add(agent);
            await db.SaveChangesAsync();
            registry.Register(agent);

            return Results.Created($"/api/agents/{agent.Name}", agent);
        });

        // Update an agent
        adminGroup.MapPut(ApiRoutes.AgentByName, async (string name, [FromBody] UpdateAgentRequest req, AppDbContext db, IAgentRegistry registry) =>
        {
            var agent = await db.AgentDefinitions
                .Include(a => a.MethodBindings)
                .FirstOrDefaultAsync(a => a.Name == name);
            if (agent == null) return Results.NotFound();

            if (req.Description != null) agent.Description = req.Description;
            if (req.Icon != null) agent.Icon = req.Icon;
            if (req.AccentClass != null) agent.AccentClass = req.AccentClass;
            if (req.SystemInstruction != null) agent.SystemInstruction = req.SystemInstruction;
            if (req.IsEnabled.HasValue) agent.IsEnabled = req.IsEnabled.Value;
            agent.UpdatedAt = DateTime.UtcNow;

            if (req.Methods != null)
            {
                var oldBindings = await db.AgentMethodBindings.Where(b => b.AgentId == agent.Id).ToListAsync();
                db.AgentMethodBindings.RemoveRange(oldBindings);

                var newBindings = req.Methods.Select(m => new AgentMethodBinding
                {
                    Id = Guid.NewGuid(),
                    AgentId = agent.Id,
                    PluginId = m.PluginId,
                    MethodId = m.MethodId
                }).ToList();
                db.AgentMethodBindings.AddRange(newBindings);
                agent.MethodBindings = newBindings;
            }

            await db.SaveChangesAsync();
            registry.Register(agent);

            return Results.Ok(agent);
        });

        // Delete an agent
        adminGroup.MapDelete(ApiRoutes.AgentByName, async (string name, AppDbContext db, IAgentRegistry registry) =>
        {
            var agent = await db.AgentDefinitions.FirstOrDefaultAsync(a => a.Name == name);
            if (agent == null) return Results.NotFound();

            db.AgentDefinitions.Remove(agent);
            await db.SaveChangesAsync();
            registry.Remove(name);

            return Results.Ok();
        });

        // Duplicate an agent
        adminGroup.MapPost(ApiRoutes.AgentDuplicate, async (string name, AppDbContext db, IAgentRegistry registry) =>
        {
            var source = await db.AgentDefinitions
                .Include(a => a.MethodBindings)
                .FirstOrDefaultAsync(a => a.Name == name);
            if (source == null) return Results.NotFound();

            var copyName = $"{source.Name}_copy";
            int suffix = 1;
            while (await db.AgentDefinitions.AnyAsync(a => a.Name == copyName))
            {
                copyName = $"{source.Name}_copy_{suffix++}";
            }

            var copy = new AgentDefinition
            {
                Name = copyName,
                Description = source.Description,
                Icon = source.Icon,
                AccentClass = source.AccentClass,
                SystemInstruction = source.SystemInstruction,
                IsEnabled = true,
                IsSeeded = false,
                MethodBindings = source.MethodBindings.Select(mb => new AgentMethodBinding
                {
                    PluginId = mb.PluginId,
                    MethodId = mb.MethodId
                }).ToList()
            };

            db.AgentDefinitions.Add(copy);
            await db.SaveChangesAsync();
            registry.Register(copy);

            return Results.Created($"/api/agents/{copy.Name}", copy);
        });

        // Preview the generated system prompt for an agent
        app.MapGet(ApiRoutes.AgentPreviewPrompt, (string name, IAgentRegistry registry, IPluginRegistry pluginRegistry) =>
        {
            if (!registry.Contains(name))
                return Results.NotFound($"Agent '{name}' not found.");

            var agentDef = registry.Resolve(name);
            var bindings = agentDef.MethodBindings.Select(b => (b.PluginId, b.MethodId)).ToList();
            var methods = pluginRegistry.GetMethods(bindings);

            var toolsDesc = string.Join("\n", methods.Select(m =>
                $"- {m.PluginId}-{m.Method.Id}: {m.Method.Description}\n  Parameters: " +
                string.Join(", ", m.Method.Parameters.Select(p => $"{p.Name} ({p.ParameterType.Name}) - {p.Description}"))));

            return Results.Ok(new
            {
                SystemInstruction = agentDef.SystemInstruction,
                AvailableTools = toolsDesc,
                MethodCount = methods.Count
            });
        });
    }
}
