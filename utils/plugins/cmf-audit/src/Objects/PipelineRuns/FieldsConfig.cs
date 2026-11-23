using System.Text.Json;

namespace audit.Objects.PipelineRuns
{
    /// <summary>
    /// Reads a small JSON file that declares the project-specific fields and settings in one place, each
    /// with a note on what it is used for. Explicit command-line values win; the file only fills options
    /// that were left out or still have their built-in default.
    /// </summary>
    public static class FieldsConfig
    {
        /// <summary>Options the file can set, with their built-in defaults (null = no default).</summary>
        private static readonly Dictionary<string, (Func<PipelineRunsOptions, string?> Get, Action<PipelineRunsOptions, string> Set, string? Default)> Known =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["milestoneField"] = (o => o.milestoneField, (o, v) => o.milestoneField = v, PipelineRunsSettings.DefaultMilestoneField),
                ["storyPointsField"] = (o => o.storyPointsField, (o, v) => o.storyPointsField = v, PipelineRunsSettings.DefaultStoryPointsField),
                ["featureType"] = (o => o.featureType, (o, v) => o.featureType = v, PipelineRunsSettings.DefaultFeatureType),
                ["epicType"] = (o => o.epicType, (o, v) => o.epicType = v, PipelineRunsSettings.DefaultEpicType),
                ["removedStates"] = (o => o.removedStates, (o, v) => o.removedStates = v, PipelineRunsSettings.DefaultRemovedStates),
                ["doneStates"] = (o => o.doneStates, (o, v) => o.doneStates = v, null),
                ["scopeTypes"] = (o => o.scopeTypes, (o, v) => o.scopeTypes = v, PipelineRunsSettings.DefaultScopeTypes),
                ["teamFromIterationPath"] = (o => o.teamFromIterationPath, (o, v) => o.teamFromIterationPath = v, null),
                ["sprintFromIterationPath"] = (o => o.sprintFromIterationPath, (o, v) => o.sprintFromIterationPath = v, null),
                ["workItemFields"] = (o => o.workItemFields, (o, v) => o.workItemFields = v, null),
            };

        /// <summary>
        /// Applies the file to the options. Format: {"fields": {"milestoneField": {"value": "Custom.Milestone", "purpose": "..."}, "epicType": "Epic"}}.
        /// A value can be a plain string or an object with "value" and an optional "purpose".
        /// </summary>
        public static void Apply(PipelineRunsOptions options, string json)
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (!doc.RootElement.TryGetProperty("fields", out var fields) || fields.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException("--fieldsConfig: the file must contain a \"fields\" object");
            }

            foreach (var field in fields.EnumerateObject())
            {
                if (!Known.TryGetValue(field.Name, out var entry))
                {
                    throw new ArgumentException($"--fieldsConfig: unknown setting '{field.Name}'. Known: {string.Join(", ", Known.Keys)}");
                }

                string? value = field.Value.ValueKind switch
                {
                    JsonValueKind.String => field.Value.GetString(),
                    JsonValueKind.Object when field.Value.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String => v.GetString(),
                    _ => throw new ArgumentException($"--fieldsConfig: '{field.Name}' must be a string or an object with a \"value\" string"),
                };
                if (value == null) continue;

                var current = entry.Get(options);
                if (string.IsNullOrEmpty(current) || (entry.Default != null && current == entry.Default))
                {
                    entry.Set(options, value);
                }
            }
        }
    }
}
